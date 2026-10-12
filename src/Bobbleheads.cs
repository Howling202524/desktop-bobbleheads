using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Serialization;
using Forms = System.Windows.Forms;

namespace DesktopDolls {
    public class DollPosition { public double X; public double Y; public double Scale = 1; public bool Visible = true; }
    public class Preferences { public bool OnTop = true; public bool ClickToSpeak = false; public List<DollPosition> Dolls = new List<DollPosition>(); }
    public class SpriteSpec {
        public string Name;
        public double HeadX, HeadY, HeadWidth, NeckX, NeckY, BodyX, BodyY, BodyWidth, FrontCut;
        public int HeadLeft, HeadTop, HeadPixelWidth, HeadPixelHeight, BodyLeft, BodyTop, BodyPixelWidth, BodyPixelHeight;
    }
    public sealed class Spring {
        public double Angle, Velocity;
        public void Kick(double direction) { Velocity += direction * 210; Velocity = Math.Max(-340, Math.Min(340, Velocity)); }
        public void Step(double dt) {
            int n = Math.Max(1, (int)Math.Ceiling(dt / .008)); double h = dt / n;
            for (int i=0;i<n;i++) { Velocity += (-95 * Angle - 5.3 * Velocity) * h; Angle += Velocity * h; }
            if (Math.Abs(Angle) < .015 && Math.Abs(Velocity) < .05) { Angle=0; Velocity=0; }
        }
    }
    public sealed class DollWindow : Window {
        public const double SceneWidth = 360, SceneHeight = 400;
        public readonly int Index;
        public readonly Spring Spring = new Spring();
        public readonly RotateTransform Rotation;
        public readonly Canvas Scene;
        public readonly SpriteSpec Spec;
        public readonly BitmapSource HeadBitmap, BodyBitmap;
        private readonly App app;
        private readonly ScaleTransform sizeTransform = new ScaleTransform();
        private Point pressScreen, pressWindow;
        private bool pressing, dragging;
        private double currentScale = 1;
        private IntPtr hwnd;
        private Window speechBubble;
        private DispatcherTimer speechTimer;
        public const string SpeechText = "国模完蛋了";

        public DollWindow(App owner, int index, SpriteSpec spec) {
            app=owner; Index=index; Spec=spec;
            Title="桌面弹簧玩偶 · "+spec.Name;
            WindowStyle=WindowStyle.None; ResizeMode=ResizeMode.NoResize; AllowsTransparency=true;
            Background=Brushes.Transparent; ShowInTaskbar=false; Topmost=app.Preferences.OnTop;
            var atlas=App.LoadBitmap("sprite-parts.png");
            HeadBitmap=new CroppedBitmap(atlas,new Int32Rect(spec.HeadLeft,spec.HeadTop,spec.HeadPixelWidth,spec.HeadPixelHeight));
            BodyBitmap=new CroppedBitmap(atlas,new Int32Rect(spec.BodyLeft,spec.BodyTop,spec.BodyPixelWidth,spec.BodyPixelHeight));
            HeadBitmap.Freeze();BodyBitmap.Freeze();
            Scene=new Canvas {Width=SceneWidth,Height=SceneHeight, Background=null, RenderTransform=sizeTransform};
            Rotation=new RotateTransform(0,spec.NeckX,spec.NeckY);
            var headOutline=new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(0,0,SceneWidth,SceneHeight)),
                new RectangleGeometry(new Rect(spec.NeckX-22,spec.NeckY+.6,44,34)));
            var back=new Canvas {Width=SceneWidth,Height=SceneHeight,RenderTransform=Rotation,Clip=headOutline};
            back.Children.Add(MakeImage(HeadBitmap,spec.HeadX,spec.HeadY,spec.HeadWidth)); Scene.Children.Add(back);
            var neck=new System.Windows.Shapes.Rectangle {Width=27,Height=38,RadiusX=7,RadiusY=7,
                Fill=new SolidColorBrush(Color.FromRgb(253,229,216)),Stroke=new SolidColorBrush(Color.FromRgb(87,57,54)),StrokeThickness=1.1};
            Canvas.SetLeft(neck,spec.NeckX-13.5);Canvas.SetTop(neck,spec.NeckY-6);Scene.Children.Add(neck);
            var body=new Canvas {Width=SceneWidth,Height=SceneHeight,
                Clip=new RectangleGeometry(new Rect(0,spec.BodyY+9,SceneWidth,SceneHeight-spec.BodyY-9))};
            body.Children.Add(MakeImage(BodyBitmap,spec.BodyX,spec.BodyY,spec.BodyWidth));Scene.Children.Add(body);
            var front=new Canvas {Width=SceneWidth,Height=SceneHeight,RenderTransform=Rotation,
                Clip=new CombinedGeometry(GeometryCombineMode.Intersect,headOutline,new RectangleGeometry(new Rect(0,0,SceneWidth,spec.FrontCut)))};
            front.Children.Add(MakeImage(HeadBitmap,spec.HeadX,spec.HeadY,spec.HeadWidth)); Scene.Children.Add(front);
            Content=Scene; SetScale(app.Preferences.Dolls[index].Scale,false);
            Left=app.Preferences.Dolls[index].X; Top=app.Preferences.Dolls[index].Y;
            MouseLeftButtonDown += OnPress; MouseMove += OnMove; MouseLeftButtonUp += OnRelease;
            LostMouseCapture += delegate { pressing=false; dragging=false; };
            ContextMenu=CreateMenu();
            LocationChanged+=delegate {PositionSpeechBubble();};SizeChanged+=delegate {PositionSpeechBubble();};
            IsVisibleChanged+=delegate {if(!IsVisible)CloseSpeechBubble();};
            Closed+=delegate {CloseSpeechBubble();};
            SourceInitialized += delegate {
                hwnd=new WindowInteropHelper(this).Handle;
                HwndSource.FromHwnd(hwnd).AddHook(WindowProc);
                int style=GetWindowLong(hwnd,-20); SetWindowLong(hwnd,-20,(style|0x80)&~0x40000);
            };
        }
        private static Image MakeImage(BitmapSource b,double x,double y,double width) {
            var image=new Image {Source=b,Width=width,Height=width*b.PixelHeight/b.PixelWidth,Stretch=Stretch.Fill};
            RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.HighQuality);
            Canvas.SetLeft(image,x); Canvas.SetTop(image,y); return image;
        }
        private Point ScreenPoint() { var p=Forms.Control.MousePosition; var source=PresentationSource.FromVisual(this);
            return source == null ? new Point(p.X,p.Y) : source.CompositionTarget.TransformFromDevice.Transform(new Point(p.X,p.Y)); }
        private void OnPress(object sender,MouseButtonEventArgs e) {
            pressScreen=ScreenPoint(); pressWindow=new Point(Left,Top); pressing=true; dragging=false;
            CaptureMouse(); e.Handled=true;
        }
        private void OnMove(object sender,MouseEventArgs e) {
            if(!pressing) return; Point now=ScreenPoint(); Vector d=now-pressScreen;
            if(!dragging && d.Length>5) dragging=true;
            if(dragging) { Left=pressWindow.X+d.X; Top=pressWindow.Y+d.Y; }
        }
        private void OnRelease(object sender,MouseButtonEventArgs e) {
            if(!pressing) return; bool wasDrag=dragging; pressing=false; dragging=false; ReleaseMouseCapture();
            if(wasDrag) app.SavePreferences(); else {
                Point p=e.GetPosition(Scene); Spring.Kick(p.X<Spec.NeckX ? -1 : 1);
                if(app.Preferences.ClickToSpeak)Say();
            } e.Handled=true;
        }
        private MenuItem Item(string title,Action action) { var i=new MenuItem {Header=title}; i.Click += delegate {action();};return i; }
        private ContextMenu CreateMenu() {
            var m=new ContextMenu(); m.Items.Add(new MenuItem {Header=Spec.Name,IsEnabled=false}); m.Items.Add(new Separator());
            m.Items.Add(Item("摇一摇",delegate {Spring.Kick(1);}));
            m.Items.Add(Item("四个一起摇",app.ShakeAll));
            m.Items.Add(Item("说话："+SpeechText,Say));
            var speech=Item("点击时显示气泡",delegate {app.SetClickToSpeak(!app.Preferences.ClickToSpeak);});speech.IsCheckable=true;
            m.Items.Add(speech);m.Opened+=delegate {speech.IsChecked=app.Preferences.ClickToSpeak;};
            m.Items.Add(Item("放大",delegate {SetScale(currentScale+.15,true);}));
            m.Items.Add(Item("缩小",delegate {SetScale(currentScale-.15,true);}));
            m.Items.Add(Item("恢复默认位置和大小",app.ResetLayout));
            var top=Item("置于最前",delegate {app.SetOnTop(!app.Preferences.OnTop);}); top.IsCheckable=true;
            m.Items.Add(top); m.Opened += delegate { top.IsChecked=app.Preferences.OnTop; };
            m.Items.Add(Item("隐藏这个玩偶（托盘可恢复）",delegate {Hide();app.SavePreferences();}));
            m.Items.Add(new Separator());m.Items.Add(Item("退出全部玩偶",app.Quit));return m;
        }
        public void SetScale(double scale,bool save) {
            double oldHeight=Height; currentScale=Math.Max(.45,Math.Min(1.8,scale));
            sizeTransform.ScaleX=currentScale; sizeTransform.ScaleY=currentScale;
            Width=SceneWidth*currentScale;Height=SceneHeight*currentScale;
            if(save) {if(!double.IsNaN(oldHeight)) Top+=oldHeight-Height;app.SavePreferences();}
        }
        public double DollScale { get {return currentScale;} }
        public void Tick(double dt) {Spring.Step(dt);Rotation.Angle=Math.Max(-18,Math.Min(18,Spring.Angle));}
        public void Say() {
            CloseSpeechBubble();
            var content=new Canvas {Width=210,Height=66};
            var border=new Border {Width=210,Height=51,Background=new SolidColorBrush(Color.FromRgb(255,250,253)),
                BorderBrush=new SolidColorBrush(Color.FromRgb(181,139,181)),BorderThickness=new Thickness(1.5),CornerRadius=new CornerRadius(13),Padding=new Thickness(12,8,12,8)};
            border.Child=new TextBlock {Text=SpeechText,FontFamily=new FontFamily("Microsoft YaHei UI"),FontSize=22,
                FontWeight=FontWeights.SemiBold,Foreground=new SolidColorBrush(Color.FromRgb(74,43,79)),TextAlignment=TextAlignment.Center};
            content.Children.Add(border);
            var tail=new System.Windows.Shapes.Polygon {Points=new PointCollection(new[]{new Point(92,49),new Point(108,63),new Point(115,49)}),
                Fill=border.Background,Stroke=border.BorderBrush,StrokeThickness=1.5};content.Children.Insert(0,tail);
            speechBubble=new Window {Owner=this,Width=210,Height=66,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,
                AllowsTransparency=true,Background=Brushes.Transparent,ShowInTaskbar=false,ShowActivated=false,Focusable=false,
                Topmost=Topmost,Content=content,IsHitTestVisible=false,Title="玩偶文字气泡"};
            speechBubble.SourceInitialized+=delegate {var h=new WindowInteropHelper(speechBubble).Handle;SetWindowLong(h,-20,GetWindowLong(h,-20)|0x20|0x80|0x8000000);};
            PositionSpeechBubble();speechBubble.Show();
            speechTimer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(4)};
            speechTimer.Tick+=delegate {CloseSpeechBubble();};speechTimer.Start();
        }
        private void PositionSpeechBubble() {
            if(speechBubble==null)return;
            speechBubble.Left=Math.Max(SystemParameters.VirtualScreenLeft,Math.Min(SystemParameters.VirtualScreenLeft+SystemParameters.VirtualScreenWidth-210,Left+Spec.NeckX*currentScale-105));
            speechBubble.Top=Math.Max(SystemParameters.VirtualScreenTop,Top+Spec.HeadY*currentScale-69);
        }
        private void CloseSpeechBubble() {
            if(speechTimer!=null){speechTimer.Stop();speechTimer=null;}
            if(speechBubble!=null){var old=speechBubble;speechBubble=null;old.Close();}
        }
        public void Capture(string path) {
            var original=Scene.RenderTransform;Scene.RenderTransform=Transform.Identity;Scene.UpdateLayout();
            var bitmap=new RenderTargetBitmap(360,400,96,96,PixelFormats.Pbgra32);bitmap.Render(Scene);Scene.RenderTransform=original;
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var f=File.Create(path))encoder.Save(f);
        }
        public void VerifySpeechBubble(string path) {
            Say();if(speechBubble==null||!speechBubble.IsVisible)throw new Exception("Text bubble did not open");
            var canvas=(Canvas)speechBubble.Content;var label=(TextBlock)((Border)canvas.Children[1]).Child;
            if(label.Text!=SpeechText)throw new Exception("Unexpected speech text");
            canvas.UpdateLayout();var bitmap=new RenderTargetBitmap(210,66,96,96,PixelFormats.Pbgra32);bitmap.Render(canvas);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var f=File.Create(path))encoder.Save(f);
            CloseSpeechBubble();
        }
        public void VerifyNativeInteraction() {
            IntPtr handle=new WindowInteropHelper(this).Handle;
            int px=(int)(176*currentScale),py=(int)(155*currentScale);
            UpdateLayout();Point face=PointToScreen(new Point(px,py));var previous=Forms.Control.MousePosition;
            try {
                SetCursorPos((int)face.X,(int)face.Y);
                SendMessage(handle,0x200,IntPtr.Zero,new IntPtr(px|(py<<16)));
                SendMessage(handle,0x201,new IntPtr(1),new IntPtr(px|(py<<16)));
                SendMessage(handle,0x202,IntPtr.Zero,new IntPtr(px|(py<<16)));
            } finally {SetCursorPos(previous.X,previous.Y);}
            if(Math.Abs(Spring.Velocity)<100)throw new Exception("Native click did not start spring: "+Spec.Name);
            Point blank=PointToScreen(new Point(3,3));
            int packed=((int)blank.X&65535)|(((int)blank.Y&65535)<<16);
            if(SendMessage(handle,0x84,IntPtr.Zero,new IntPtr(packed)).ToInt64()!=-1)
                throw new Exception("Transparent hit testing failed: "+Spec.Name);
        }
        private byte Alpha(BitmapSource bitmap,double x,double y) {
            int px=(int)x,py=(int)y;if(px<0||py<0||px>=bitmap.PixelWidth||py>=bitmap.PixelHeight) return 0;
            var bytes=new byte[4];bitmap.CopyPixels(new Int32Rect(px,py,1,1),bytes,4,0);return bytes[3];
        }
        private bool Solid(Point p) {
            if(p.X>=Spec.NeckX-13.5&&p.X<=Spec.NeckX+13.5&&p.Y>=Spec.NeckY-6&&p.Y<=Spec.NeckY+32)return true;
            double bw=Spec.BodyWidth/BodyBitmap.PixelWidth;
            if(Alpha(BodyBitmap,(p.X-Spec.BodyX)/bw,(p.Y-Spec.BodyY)/bw)>24)return true;
            Point q=Rotation.Inverse.Transform(p); double hw=Spec.HeadWidth/HeadBitmap.PixelWidth;
            return Alpha(HeadBitmap,(q.X-Spec.HeadX)/hw,(q.Y-Spec.HeadY)/hw)>24;
        }
        private IntPtr WindowProc(IntPtr handle,int message,IntPtr wParam,IntPtr lParam,ref bool handled) {
            if(message==0x84 && !pressing && (ContextMenu==null||!ContextMenu.IsOpen)) {
                long lp=lParam.ToInt64();Point p=PointFromScreen(new Point((short)(lp&65535),(short)((lp>>16)&65535)));
                p=new Point(p.X/currentScale,p.Y/currentScale);
                if(!Solid(p)) {handled=true;return new IntPtr(-1);}
            }return IntPtr.Zero;
        }
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h,int n);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h,int n,int value);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h,int message,IntPtr wParam,IntPtr lParam);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x,int y);
    }

    public sealed class App : Application {
        public Preferences Preferences=new Preferences();
        public List<DollWindow> Dolls=new List<DollWindow>();
        private readonly string settingsPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FourBobbleheads","settings.xml");
        private Forms.NotifyIcon tray;
        private DispatcherTimer timer;
        private Stopwatch clock=Stopwatch.StartNew();
        private double lastTime;
        private bool smoke;
        private string output;
        private bool quitting;

        [STAThread] public static int Main(string[] args) {
            bool test=args.Length>0&&args[0]=="--smoke";
            bool first;using(var mutex=new Mutex(true,test?"FourBobbleheadsSmoke":"FourBobbleheadsDesktop",out first)) {
                if(!first) {MessageBox.Show("玩偶已经在运行，可在右下角托盘菜单中管理。","桌面弹簧玩偶");return 0;}
                try {var app=new App();app.smoke=test;app.output=args.Length>1?args[1]:Environment.CurrentDirectory;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;app.Run();return 0;}
                catch(Exception ex) {string log=Path.Combine(Path.GetTempPath(),"FourBobbleheads-error.txt");File.WriteAllText(log,ex.ToString());if(!test)MessageBox.Show("启动失败，错误记录："+log,"桌面弹簧玩偶");return 1;}
            }
        }
        public static BitmapSource LoadBitmap(string resource) {
            using(Stream stream=Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)) {
                if(stream==null)throw new InvalidDataException("Missing sprite "+resource);
                var b=new BitmapImage();b.BeginInit();b.CacheOption=BitmapCacheOption.OnLoad;b.StreamSource=stream;b.EndInit();
                var converted=new FormatConvertedBitmap(b,PixelFormats.Bgra32,null,0);converted.Freeze();return converted;
            }
        }
        private SpriteSpec[] LoadSpecs() {
            using(Stream s=Assembly.GetExecutingAssembly().GetManifestResourceStream("sprites.xml"))
                return (SpriteSpec[])new XmlSerializer(typeof(SpriteSpec[])).Deserialize(s);
        }
        protected override void OnStartup(StartupEventArgs e) {
            base.OnStartup(e);if(!smoke)LoadPreferences();
            while(Preferences.Dolls.Count<4)Preferences.Dolls.Add(new DollPosition());
            if(smoke||Preferences.Dolls.All(p=>p.X==0&&p.Y==0))SetDefaultPositions();
            SpriteSpec[] specs=LoadSpecs();for(int i=0;i<4;i++){var w=new DollWindow(this,i,specs[i]);Dolls.Add(w);w.Show();if(!Preferences.Dolls[i].Visible)w.Hide();}
            EnsureVisiblePositions();if(!smoke)CreateTray();
            lastTime=clock.Elapsed.TotalSeconds;timer=new DispatcherTimer(DispatcherPriority.Render){Interval=TimeSpan.FromMilliseconds(16)};
            timer.Tick+=delegate {double now=clock.Elapsed.TotalSeconds,dt=Math.Min(.05,now-lastTime);lastTime=now;foreach(var w in Dolls)w.Tick(dt);};timer.Start();
            if(smoke) RunSmoke();
        }
        private void SetDefaultPositions() {
            Rect r=SystemParameters.WorkArea;double scale=Math.Min(.85,Math.Max(.45,(r.Width-40)/(4*DollWindow.SceneWidth)));
            double total=4*DollWindow.SceneWidth*scale,start=r.Left+(r.Width-total)/2;
            for(int i=0;i<4;i++)Preferences.Dolls[i]=new DollPosition {X=start+i*DollWindow.SceneWidth*scale,Y=r.Bottom-DollWindow.SceneHeight*scale,Scale=scale};
        }
        private void EnsureVisiblePositions() {
            Rect virtualArea=new Rect(SystemParameters.VirtualScreenLeft,SystemParameters.VirtualScreenTop,SystemParameters.VirtualScreenWidth,SystemParameters.VirtualScreenHeight);
            foreach(var w in Dolls) {if(!virtualArea.IntersectsWith(new Rect(w.Left,w.Top,w.Width,w.Height))) {w.Left=SystemParameters.WorkArea.Left;w.Top=SystemParameters.WorkArea.Bottom-w.Height;}}
        }
        public void ResetLayout() {SetDefaultPositions();foreach(var w in Dolls){var p=Preferences.Dolls[w.Index];w.SetScale(p.Scale,false);w.Left=p.X;w.Top=p.Y;w.Show();}SavePreferences();}
        public void ShakeAll(){foreach(var w in Dolls)w.Spring.Kick(w.Index%2==0?1:-1);}
        public void SetOnTop(bool value){Preferences.OnTop=value;foreach(var w in Dolls)w.Topmost=value;SavePreferences();}
        public void SetClickToSpeak(bool value){Preferences.ClickToSpeak=value;SavePreferences();}
        private void LoadPreferences(){try{if(File.Exists(settingsPath))using(var s=File.OpenRead(settingsPath))Preferences=(Preferences)new XmlSerializer(typeof(Preferences)).Deserialize(s);}catch{Preferences=new Preferences();}}
        public void SavePreferences(){if(smoke||Dolls.Count!=4)return;try{
            for(int i=0;i<4;i++){var w=Dolls[i];Preferences.Dolls[i]=new DollPosition {X=w.Left,Y=w.Top,Scale=w.DollScale,Visible=w.IsVisible};}
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));string temp=settingsPath+".tmp";
            using(var s=File.Create(temp))new XmlSerializer(typeof(Preferences)).Serialize(s,Preferences);
            if(File.Exists(settingsPath))File.Replace(temp,settingsPath,null);else File.Move(temp,settingsPath);
        }catch(Exception ex){Debug.WriteLine(ex);}}
        private void CreateTray(){
            tray=new Forms.NotifyIcon {Text="桌面弹簧玩偶 · 双击四个一起摇",Icon=System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location),Visible=true};
            var menu=new Forms.ContextMenuStrip();
            menu.Items.Add("四个一起摇",null,delegate{ShakeAll();});
            menu.Items.Add("显示文字："+DollWindow.SpeechText,null,delegate{foreach(var w in Dolls)if(w.IsVisible)w.Say();});
            var speech=new Forms.ToolStripMenuItem("点击时显示气泡"){CheckOnClick=false};
            speech.Click+=delegate{SetClickToSpeak(!Preferences.ClickToSpeak);};menu.Items.Add(speech);
            menu.Opening+=delegate{speech.Checked=Preferences.ClickToSpeak;};
            menu.Items.Add("显示全部玩偶",null,delegate{foreach(var w in Dolls)w.Show();SavePreferences();});
            menu.Items.Add("隐藏全部玩偶",null,delegate{foreach(var w in Dolls)w.Hide();SavePreferences();});
            menu.Items.Add("恢复默认位置和大小",null,delegate{ResetLayout();});
            menu.Items.Add("操作说明",null,delegate{MessageBox.Show("点击：弹簧摇头\n按住拖动：移动玩偶\n右键“说话”：显示国模完蛋了的文字气泡\n勾选“点击时显示气泡”：点击也显示文字\n右键：大小、置顶、隐藏和退出\n右下角托盘：显示全部、一起摇和退出\n位置和大小会自动记住。","桌面弹簧玩偶");});
            menu.Items.Add("退出全部玩偶",null,delegate{Quit();});tray.ContextMenuStrip=menu;tray.DoubleClick+=delegate{ShakeAll();};
        }
        public void Quit(){if(quitting)return;quitting=true;SavePreferences();if(timer!=null)timer.Stop();if(tray!=null){tray.Visible=false;tray.Dispose();}Shutdown();}
        protected override void OnSessionEnding(SessionEndingCancelEventArgs e){SavePreferences();base.OnSessionEnding(e);}
        private void RunSmoke(){
            Directory.CreateDirectory(output);var results=new List<string>();
            var spring=new Spring();spring.Kick(1);double max=0;bool negative=false;
            for(int i=0;i<1200;i++){spring.Step(1.0/120);max=Math.Max(max,Math.Abs(spring.Angle));if(spring.Angle<-.5)negative=true;}
            if(max<8||max>40||!negative||Math.Abs(spring.Angle)>.02)throw new Exception("Spring dynamics check failed");
            results.Add("PASS: spring oscillates in both directions and settles; peak="+max.ToString("F2"));
            foreach(var w in Dolls){w.VerifyNativeInteraction();w.Spring.Angle=0;w.Spring.Velocity=0;results.Add("PASS: "+w.Spec.Name+" Windows mouse click starts spring; transparent area passes hit testing");}
            ShakeAll();var testTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(450)};
            testTimer.Tick+=delegate{testTimer.Stop();
                foreach(var w in Dolls) {
                    if(!w.IsVisible||new WindowInteropHelper(w).Handle==IntPtr.Zero||Math.Abs(w.Rotation.Angle)<.5)throw new Exception("Window animation check failed");
                    double before=w.DollScale;w.SetScale(before+.15,false);if(w.Width<=DollWindow.SceneWidth*before)throw new Exception("Scale check failed");w.SetScale(before,false);
                    w.ContextMenu.IsOpen=true;w.ContextMenu.IsOpen=false;
                    w.Capture(Path.Combine(output,"doll"+w.Index+"-animated.png"));
                    double currentAngle=w.Rotation.Angle;
                    foreach(double angle in new[]{-18.0,0.0,18.0}){w.Rotation.Angle=angle;w.Capture(Path.Combine(output,"doll"+w.Index+"-neck-"+angle+".png"));}w.Rotation.Angle=currentAngle;
                    w.VerifySpeechBubble(Path.Combine(output,"doll"+w.Index+"-speech.png"));
                    results.Add("PASS: "+w.Spec.Name+" text-only speech bubble contains "+DollWindow.SpeechText);
                    results.Add("PASS: "+w.Spec.Name+" native transparent window, spring animation, resizing, context menu, rendered preview");
                }
                File.WriteAllLines(Path.Combine(output,"smoke-test.txt"),results);Quit();};testTimer.Start();
        }
    }
}
