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
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Xml.Serialization;
using Forms = System.Windows.Forms;

namespace DesktopDolls {
    public class DollPosition { public double X; public double Y; public double Scale = 1; public bool Visible = true; }
    public class Preferences {
        public bool OnTop = true; public bool ClickToSpeak = false;
        public bool Breathing = false; public bool Blinking = false;
        public string BubbleText = DollWindow.SpeechText;
        public List<DollPosition> Dolls = new List<DollPosition>();
    }
    public class SpriteSpec {
        public string Name;
        public double HeadX, HeadY, HeadWidth, NeckX, NeckY, BodyX, BodyY, BodyWidth, FrontCut;
        public int HeadLeft, HeadTop, HeadPixelWidth, HeadPixelHeight, BodyLeft, BodyTop, BodyPixelWidth, BodyPixelHeight;
        public string BlinkLeftMask, BlinkRightMask, BlinkLeftLine, BlinkRightLine;
    }
    public sealed class Spring {
        public double Angle, Velocity;
        public bool Active {get{return Angle!=0||Velocity!=0;}}
        public double VisibleAngle {get{return 18*Math.Tanh(Angle/18);}}
        public void Reset(){Angle=0;Velocity=0;}
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
        private readonly ScaleTransform breathTransform = new ScaleTransform(1,1,176,375);
        private readonly SpriteAtlas atlas;
        private readonly Geometry neckShape;
        private Canvas blinkOverlay;
        private DispatcherTimer blinkTimer;
        private readonly Random blinkRandom;
        private bool blinkClosed;
        private bool breathRunning;
        private Point pressScreen, pressWindow;
        private bool pressing, dragging;
        private double currentScale = 1;
        private IntPtr hwnd;
        private Window speechBubble;
        private DispatcherTimer speechTimer;
        private TextBlock speechLabel;
        public const string SpeechText = "国模完蛋了";

        public DollWindow(App owner, int index, SpriteSpec spec) {
            app=owner; Index=index; Spec=spec;
            Title="桌面弹簧玩偶 · "+spec.Name;
            WindowStyle=WindowStyle.None; ResizeMode=ResizeMode.NoResize; AllowsTransparency=true;
            Background=Brushes.Transparent; ShowInTaskbar=false; Topmost=app.Preferences.OnTop;
            atlas=app.Atlas;blinkRandom=new Random(Environment.TickCount+index*7919);
            HeadBitmap=new CroppedBitmap(atlas.Bitmap,new Int32Rect(spec.HeadLeft,spec.HeadTop,spec.HeadPixelWidth,spec.HeadPixelHeight));
            BodyBitmap=new CroppedBitmap(atlas.Bitmap,new Int32Rect(spec.BodyLeft,spec.BodyTop,spec.BodyPixelWidth,spec.BodyPixelHeight));
            HeadBitmap.Freeze();BodyBitmap.Freeze();
            var sceneTransform=new TransformGroup();sceneTransform.Children.Add(breathTransform);sceneTransform.Children.Add(sizeTransform);
            Scene=new Canvas {Width=SceneWidth,Height=SceneHeight, Background=null, RenderTransform=sceneTransform};
            Rotation=new RotateTransform(0,spec.NeckX,spec.NeckY);
            var headOutline=new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(0,0,SceneWidth,SceneHeight)),
                new RectangleGeometry(new Rect(spec.NeckX-22,spec.NeckY+.6,44,34)));
            var back=new Canvas {Width=SceneWidth,Height=SceneHeight,RenderTransform=Rotation,Clip=headOutline};
            back.Children.Add(MakeImage(HeadBitmap,spec.HeadX,spec.HeadY,spec.HeadWidth)); Scene.Children.Add(back);
            var neckGeometry=new StreamGeometry();double nx=spec.NeckX,ny=spec.NeckY;
            using(var c=neckGeometry.Open()) {
                c.BeginFigure(new Point(nx-12,ny-5),true,true);c.LineTo(new Point(nx+12,ny-5),true,false);
                c.BezierTo(new Point(nx+12,ny+5),new Point(nx+15,ny+19),new Point(nx+13.5,ny+32),true,false);
                c.LineTo(new Point(nx-13.5,ny+32),true,false);
                c.BezierTo(new Point(nx-15,ny+19),new Point(nx-12,ny+5),new Point(nx-12,ny-5),true,false);
            }neckGeometry.Freeze();neckShape=neckGeometry;
            var neck=new System.Windows.Shapes.Path {Data=neckShape,
                Fill=new LinearGradientBrush(Color.FromRgb(254,226,214),Color.FromRgb(253,242,231),90),
                Stroke=new SolidColorBrush(Color.FromRgb(87,57,54)),StrokeThickness=1.0};Scene.Children.Add(neck);
            var body=new Canvas {Width=SceneWidth,Height=SceneHeight,
                Clip=new RectangleGeometry(new Rect(0,spec.BodyY+9,SceneWidth,SceneHeight-spec.BodyY-9))};
            body.Children.Add(MakeImage(BodyBitmap,spec.BodyX,spec.BodyY,spec.BodyWidth));Scene.Children.Add(body);
            var front=new Canvas {Width=SceneWidth,Height=SceneHeight,RenderTransform=Rotation,
                Clip=new CombinedGeometry(GeometryCombineMode.Intersect,headOutline,new RectangleGeometry(new Rect(0,0,SceneWidth,spec.FrontCut)))};
            front.Children.Add(MakeImage(HeadBitmap,spec.HeadX,spec.HeadY,spec.HeadWidth)); Scene.Children.Add(front);
            if(!string.IsNullOrEmpty(spec.BlinkLeftMask)) {
                blinkOverlay=new Canvas {Visibility=Visibility.Collapsed,IsHitTestVisible=false};
                double hs=spec.HeadWidth/spec.HeadPixelWidth;
                var eyeTransform=new TransformGroup();eyeTransform.Children.Add(new ScaleTransform(hs,hs));
                eyeTransform.Children.Add(new TranslateTransform(spec.HeadX-spec.HeadLeft*hs,spec.HeadY-spec.HeadTop*hs));blinkOverlay.RenderTransform=eyeTransform;
                var skin=new LinearGradientBrush(Color.FromRgb(255,249,242),Color.FromRgb(255,239,232),90);
                foreach(string mask in new[]{spec.BlinkLeftMask,spec.BlinkRightMask})blinkOverlay.Children.Add(new System.Windows.Shapes.Path {Data=Geometry.Parse(mask),Fill=skin});
                foreach(string line in new[]{spec.BlinkLeftLine,spec.BlinkRightLine})blinkOverlay.Children.Add(new System.Windows.Shapes.Path {Data=Geometry.Parse(line),Stroke=new SolidColorBrush(Color.FromRgb(46,30,49)),StrokeThickness=3.5,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round});
                front.Children.Add(blinkOverlay);
            }
            Content=Scene; SetScale(app.Preferences.Dolls[index].Scale,false);
            Left=app.Preferences.Dolls[index].X; Top=app.Preferences.Dolls[index].Y;
            MouseLeftButtonDown += OnPress; MouseMove += OnMove; MouseLeftButtonUp += OnRelease;
            LostMouseCapture += delegate { pressing=false; dragging=false; };
            ContextMenu=CreateMenu();
            LocationChanged+=delegate {PositionSpeechBubble();};SizeChanged+=delegate {PositionSpeechBubble();};
            IsVisibleChanged+=delegate {if(!IsVisible){CloseSpeechBubble();ResetMotion();}ConfigureIdle();app.RefreshMotion();};
            Closed+=delegate {CloseSpeechBubble();if(blinkTimer!=null)blinkTimer.Stop();breathTransform.BeginAnimation(ScaleTransform.ScaleYProperty,null);};
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
            if(dragging) {
                Rect area=ScreenLayout.AtPointer(this);FitScaleTo(area);
                Rect p=ScreenLayout.Clamp(new Rect(pressWindow.X+d.X,pressWindow.Y+d.Y,Width,Height),area);Left=p.Left;Top=p.Top;
            }
        }
        private void OnRelease(object sender,MouseButtonEventArgs e) {
            if(!pressing) return; bool wasDrag=dragging; pressing=false; dragging=false; ReleaseMouseCapture();
            if(wasDrag) app.SavePreferences(); else {
                Point p=e.GetPosition(Scene); Nudge(p.X<Spec.NeckX ? -1 : 1);
                if(app.Preferences.ClickToSpeak)Say();
            } e.Handled=true;
        }
        private MenuItem Item(string title,Action action) { var i=new MenuItem {Header=title}; i.Click += delegate {action();};return i; }
        private ContextMenu CreateMenu() {
            var m=new ContextMenu(); m.Items.Add(new MenuItem {Header=Spec.Name,IsEnabled=false}); m.Items.Add(new Separator());
            m.Items.Add(Item("摇一摇",delegate {Nudge(1);}));
            m.Items.Add(Item("四个一起摇",app.ShakeAll));
            var say=Item("说话："+app.ActiveBubbleText,Say);m.Items.Add(say);
            m.Opened+=delegate {say.Header="说话："+app.ActiveBubbleText;};
            m.Items.Add(Item("自定义气泡文字…",delegate {app.EditBubbleText(this);}));
            var speech=Item("点击时显示气泡",delegate {app.SetClickToSpeak(!app.Preferences.ClickToSpeak);});speech.IsCheckable=true;
            m.Items.Add(speech);m.Opened+=delegate {speech.IsChecked=app.Preferences.ClickToSpeak;};
            var breathing=Item("轻微呼吸",delegate {app.SetBreathing(!app.Preferences.Breathing);});breathing.IsCheckable=true;m.Items.Add(breathing);
            var blinking=Item("偶尔眨眼（睁眼角色）",delegate {app.SetBlinking(!app.Preferences.Blinking);});blinking.IsCheckable=true;m.Items.Add(blinking);
            m.Opened+=delegate {breathing.IsChecked=app.Preferences.Breathing;blinking.IsChecked=app.Preferences.Blinking;};
            m.Items.Add(Item("放大",delegate {SetScale(currentScale+.15,true);}));
            m.Items.Add(Item("缩小",delegate {SetScale(currentScale-.15,true);}));
            m.Items.Add(Item("恢复默认位置和大小",app.ResetLayout));
            var top=Item("置于最前",delegate {app.SetOnTop(!app.Preferences.OnTop);}); top.IsCheckable=true;
            m.Items.Add(top); m.Opened += delegate { top.IsChecked=app.Preferences.OnTop; };
            m.Items.Add(Item("隐藏这个玩偶（托盘可恢复）",delegate {Hide();app.SavePreferences();}));
            m.Items.Add(new Separator());m.Items.Add(Item("退出全部玩偶",app.Quit));return m;
        }
        public void SetScale(double scale,bool save) {
            Rect area=ScreenLayout.ForWindow(this);double oldHeight=Height;
            double max=Math.Min(1.8,Math.Min(area.Width/SceneWidth,area.Height/SceneHeight));
            if(!ScreenLayout.Finite(scale))scale=.85;
            currentScale=Math.Max(Math.Min(.45,max),Math.Min(max,scale));
            sizeTransform.ScaleX=currentScale; sizeTransform.ScaleY=currentScale;
            Width=SceneWidth*currentScale;Height=SceneHeight*currentScale;
            if(save) {if(ScreenLayout.Finite(oldHeight)) Top+=oldHeight-Height;ClampToWorkArea(area);app.SavePreferences();}
        }
        private void FitScaleTo(Rect area) {
            double max=Math.Min(area.Width/SceneWidth,area.Height/SceneHeight);
            if(currentScale<=max)return;currentScale=max;sizeTransform.ScaleX=max;sizeTransform.ScaleY=max;Width=SceneWidth*max;Height=SceneHeight*max;
        }
        public void ClampToWorkArea(){ClampToWorkArea(ScreenLayout.ForWindow(this));}
        private void ClampToWorkArea(Rect area) {
            FitScaleTo(area);double x=ScreenLayout.Finite(Left)?Left:area.Left,y=ScreenLayout.Finite(Top)?Top:area.Bottom-Height;
            Rect p=ScreenLayout.Clamp(new Rect(x,y,Width,Height),area);Left=p.Left;Top=p.Top;PositionSpeechBubble();
        }
        public double DollScale { get {return currentScale;} }
        public void Nudge(double direction){if(!IsVisible)return;Spring.Kick(direction);app.RefreshMotion();}
        public void ResetMotion(){Spring.Reset();Rotation.Angle=0;}
        public void Tick(double dt) {Spring.Step(dt);Rotation.Angle=Spring.VisibleAngle;}
        public void ConfigureIdle() {
            bool breathe=IsVisible&&app.Preferences.Breathing;
            if(breathe!=breathRunning) {
                breathRunning=breathe;
                if(breathe) {
                    var animation=new DoubleAnimation(1,1.006,new Duration(TimeSpan.FromSeconds(2.5+Index*.15))){AutoReverse=true,RepeatBehavior=RepeatBehavior.Forever};
                    Timeline.SetDesiredFrameRate(animation,20);breathTransform.BeginAnimation(ScaleTransform.ScaleYProperty,animation);
                }else {breathTransform.BeginAnimation(ScaleTransform.ScaleYProperty,null);breathTransform.ScaleY=1;}
            }
            bool blink=IsVisible&&app.Preferences.Blinking&&blinkOverlay!=null;
            if(!blink){if(blinkTimer!=null)blinkTimer.Stop();blinkClosed=false;if(blinkOverlay!=null)blinkOverlay.Visibility=Visibility.Collapsed;return;}
            if(blinkTimer==null) {
                blinkTimer=new DispatcherTimer();blinkTimer.Tick+=delegate {
                    if(!IsVisible||!app.Preferences.Blinking){ConfigureIdle();return;}
                    blinkClosed=!blinkClosed;blinkOverlay.Visibility=blinkClosed?Visibility.Visible:Visibility.Collapsed;
                    blinkTimer.Interval=blinkClosed?TimeSpan.FromMilliseconds(135):TimeSpan.FromSeconds(4.5+blinkRandom.NextDouble()*3);
                };
            }
            if(!blinkTimer.IsEnabled){blinkTimer.Interval=TimeSpan.FromSeconds(4.5+blinkRandom.NextDouble()*3);blinkTimer.Start();}
        }
        public void CaptureBlink(string path) {
            if(blinkOverlay==null)return;blinkOverlay.Visibility=Visibility.Visible;Capture(path);blinkOverlay.Visibility=Visibility.Collapsed;
        }
        public void VerifyOptionalIdle() {
            if(!breathTransform.HasAnimatedProperties||(blinkOverlay!=null&&(blinkTimer==null||!blinkTimer.IsEnabled)))
                throw new Exception("Optional idle animation did not start: "+Spec.Name);
            Hide();
            if(breathTransform.HasAnimatedProperties||(blinkTimer!=null&&blinkTimer.IsEnabled))
                throw new Exception("Hidden doll kept idle animation alive: "+Spec.Name);
            Show();
            if(!breathTransform.HasAnimatedProperties||(blinkOverlay!=null&&!blinkTimer.IsEnabled))
                throw new Exception("Shown doll did not resume optional idle animation: "+Spec.Name);
        }
        public void VerifyEdgePlacement(string path) {
            double x=Left,y=Top,scale=currentScale;Rect area=ScreenLayout.ForWindow(this);
            try {
                Left=area.Right-20;Top=area.Bottom-20;SetScale(1.8,true);
                if(!area.Contains(new Rect(Left,Top,Width,Height)))throw new Exception("Enlarged doll outside work area");
                Left=area.Left;Top=area.Top;VerifySpeechBubble(path);
            }finally{SetScale(scale,false);Left=x;Top=y;}
        }
        public void Say() {
            if(!IsVisible)return;
            CloseSpeechBubble();
            Rect area=ScreenLayout.ForWindow(this);double width=Math.Min(230,area.Width-12);
            speechLabel=new TextBlock {Text=app.ActiveBubbleText,FontFamily=new FontFamily("Microsoft YaHei UI"),FontSize=22,TextWrapping=TextWrapping.Wrap,
                FontWeight=FontWeights.SemiBold,Foreground=new SolidColorBrush(Color.FromRgb(74,43,79)),TextAlignment=TextAlignment.Center};
            speechLabel.Measure(new Size(width-28,double.PositiveInfinity));double bodyHeight=speechLabel.DesiredSize.Height+18;
            var content=new Canvas {Width=width,Height=bodyHeight+14};
            var border=new Border {Width=width,Height=bodyHeight,Background=new SolidColorBrush(Color.FromRgb(255,250,253)),
                BorderBrush=new SolidColorBrush(Color.FromRgb(181,139,181)),BorderThickness=new Thickness(1.5),CornerRadius=new CornerRadius(13),Padding=new Thickness(12,8,12,8)};
            border.Child=speechLabel;
            content.Children.Add(border);
            var tail=new System.Windows.Shapes.Polygon {Points=new PointCollection(new[]{new Point(width/2-12,bodyHeight-2),new Point(width/2+4,bodyHeight+12),new Point(width/2+11,bodyHeight-2)}),
                Fill=border.Background,Stroke=border.BorderBrush,StrokeThickness=1.5};content.Children.Insert(0,tail);
            speechBubble=new Window {Owner=this,Width=width,Height=bodyHeight+14,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,
                AllowsTransparency=true,Background=Brushes.Transparent,ShowInTaskbar=false,ShowActivated=false,Focusable=false,
                Topmost=Topmost,Content=content,IsHitTestVisible=false,Title="玩偶文字气泡"};
            speechBubble.SourceInitialized+=delegate {var h=new WindowInteropHelper(speechBubble).Handle;SetWindowLong(h,-20,GetWindowLong(h,-20)|0x20|0x80|0x8000000);};
            PositionSpeechBubble();speechBubble.Show();
            speechTimer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(4)};
            speechTimer.Tick+=delegate {CloseSpeechBubble();};speechTimer.Start();
        }
        private void PositionSpeechBubble() {
            if(speechBubble==null)return;
            Rect area=ScreenLayout.ForWindow(this);
            Rect p=ScreenLayout.Bubble(new Size(speechBubble.Width,speechBubble.Height),new Point(Left+Spec.NeckX*currentScale,Top+Spec.HeadY*currentScale),new Rect(Left,Top,Width,Height),area);
            speechBubble.Left=p.Left;speechBubble.Top=p.Top;
        }
        public void ApplyTopmost(bool value){Topmost=value;if(speechBubble!=null)speechBubble.Topmost=value;}
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
            var canvas=(Canvas)speechBubble.Content;
            if(speechLabel.Text!=app.ActiveBubbleText)throw new Exception("Unexpected speech text");
            Rect area=ScreenLayout.ForWindow(this),bounds=new Rect(speechBubble.Left,speechBubble.Top,speechBubble.Width,speechBubble.Height);
            if(!area.Contains(bounds))throw new Exception("Speech bubble outside monitor work area");
            canvas.UpdateLayout();var bitmap=new RenderTargetBitmap((int)Math.Ceiling(speechBubble.Width),(int)Math.Ceiling(speechBubble.Height),96,96,PixelFormats.Pbgra32);bitmap.Render(canvas);
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
        private bool Solid(Point p) {
            if(neckShape.FillContains(p))return true;
            double bw=Spec.BodyWidth/BodyBitmap.PixelWidth;
            if(p.Y>=Spec.BodyY+9&&atlas.Alpha((p.X-Spec.BodyX)/bw,(p.Y-Spec.BodyY)/bw,Spec.BodyLeft,Spec.BodyTop,Spec.BodyPixelWidth,Spec.BodyPixelHeight)>24)return true;
            Matrix inverse=Rotation.Value;inverse.Invert();Point q=inverse.Transform(p);double hw=Spec.HeadWidth/HeadBitmap.PixelWidth;
            if(q.X>=Spec.NeckX-22&&q.X<=Spec.NeckX+22&&q.Y>Spec.NeckY+.6&&q.Y<Spec.NeckY+34.6)return false;
            return atlas.Alpha((q.X-Spec.HeadX)/hw,(q.Y-Spec.HeadY)/hw,Spec.HeadLeft,Spec.HeadTop,Spec.HeadPixelWidth,Spec.HeadPixelHeight)>24;
        }
        private IntPtr WindowProc(IntPtr handle,int message,IntPtr wParam,IntPtr lParam,ref bool handled) {
            if(message==0x84 && !pressing && (ContextMenu==null||!ContextMenu.IsOpen)) {
                long lp=lParam.ToInt64();Point p=PointFromScreen(new Point((short)(lp&65535),(short)((lp>>16)&65535)));
                Matrix inverse=Scene.RenderTransform.Value;inverse.Invert();p=inverse.Transform(p);
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
        public SpriteAtlas Atlas;
        public static int BitmapLoads;
        public long MotionTickCount;
        public bool MotionRunning {get{return timer!=null&&timer.IsEnabled;}}
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
                var converted=new FormatConvertedBitmap(b,PixelFormats.Bgra32,null,0);converted.Freeze();BitmapLoads++;return converted;
            }
        }
        private SpriteSpec[] LoadSpecs() {
            using(Stream s=Assembly.GetExecutingAssembly().GetManifestResourceStream("sprites.xml"))
                return (SpriteSpec[])new XmlSerializer(typeof(SpriteSpec[])).Deserialize(s);
        }
        protected override void OnStartup(StartupEventArgs e) {
            base.OnStartup(e);if(!smoke)LoadPreferences();
            Atlas=new SpriteAtlas(LoadBitmap("sprite-parts.png"));
            while(Preferences.Dolls.Count<4)Preferences.Dolls.Add(new DollPosition());
            if(smoke||Preferences.Dolls.All(p=>p.X==0&&p.Y==0))SetDefaultPositions();
            SpriteSpec[] specs=LoadSpecs();for(int i=0;i<4;i++){var w=new DollWindow(this,i,specs[i]);Dolls.Add(w);w.Show();if(!Preferences.Dolls[i].Visible)w.Hide();}
            EnsureVisiblePositions();if(!smoke)CreateTray();
            timer=new DispatcherTimer(DispatcherPriority.Render){Interval=TimeSpan.FromMilliseconds(16)};
            timer.Tick+=delegate {
                MotionTickCount++;double now=clock.Elapsed.TotalSeconds,dt=Math.Min(.05,now-lastTime);lastTime=now;
                foreach(var w in Dolls)if(w.IsVisible&&w.Spring.Active)w.Tick(dt);RefreshMotion();
            };RefreshMotion();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged+=OnDisplaySettingsChanged;
            if(smoke) RunSmoke();
        }
        public void RefreshMotion() {
            if(timer==null)return;bool active=Dolls.Any(w=>w.IsVisible&&w.Spring.Active);
            if(active&&!timer.IsEnabled){lastTime=clock.Elapsed.TotalSeconds;timer.Start();}
            else if(!active&&timer.IsEnabled)timer.Stop();
        }
        private void OnDisplaySettingsChanged(object sender,EventArgs e) {
            if(Dispatcher.HasShutdownStarted)return;
            Dispatcher.BeginInvoke(new Action(delegate{EnsureVisiblePositions();SavePreferences();}));
        }
        private void SetDefaultPositions() {
            Rect r=SystemParameters.WorkArea;double scale=Math.Min(.85,Math.Max(.45,(r.Width-40)/(4*DollWindow.SceneWidth)));
            double total=4*DollWindow.SceneWidth*scale,start=r.Left+(r.Width-total)/2;
            for(int i=0;i<4;i++)Preferences.Dolls[i]=new DollPosition {X=start+i*DollWindow.SceneWidth*scale,Y=r.Bottom-DollWindow.SceneHeight*scale,Scale=scale};
        }
        private void EnsureVisiblePositions() {
            foreach(var w in Dolls)w.ClampToWorkArea();
        }
        public void ResetLayout() {SetDefaultPositions();foreach(var w in Dolls){var p=Preferences.Dolls[w.Index];w.SetScale(p.Scale,false);w.Left=p.X;w.Top=p.Y;w.Show();w.ClampToWorkArea();}SavePreferences();}
        public void ShakeAll(){foreach(var w in Dolls)w.Nudge(w.Index%2==0?1:-1);}
        public void SetOnTop(bool value){Preferences.OnTop=value;foreach(var w in Dolls)w.ApplyTopmost(value);SavePreferences();}
        public void SetClickToSpeak(bool value){Preferences.ClickToSpeak=value;SavePreferences();}
        public void SetBreathing(bool value){Preferences.Breathing=value;foreach(var w in Dolls)w.ConfigureIdle();SavePreferences();}
        public void SetBlinking(bool value){Preferences.Blinking=value;foreach(var w in Dolls)w.ConfigureIdle();SavePreferences();}
        public string ActiveBubbleText {get{return CleanBubbleText(Preferences.BubbleText);}}
        public static string CleanBubbleText(string text) {
            if(string.IsNullOrWhiteSpace(text))return DollWindow.SpeechText;
            text=text.Trim().Replace('\r',' ').Replace('\n',' ');
            if(text.Length>40)text=text.Substring(0,40);
            if(text.Length>0&&char.IsHighSurrogate(text[text.Length-1]))text=text.Substring(0,text.Length-1);
            return text;
        }
        public void EditBubbleText(Window owner){string text=BubbleEditor.Show(owner,ActiveBubbleText);if(text!=null){Preferences.BubbleText=text;SavePreferences();((DollWindow)owner).Say();}}
        private void LoadPreferences(){try{
            if(File.Exists(settingsPath))using(var s=File.OpenRead(settingsPath))Preferences=(Preferences)new XmlSerializer(typeof(Preferences)).Deserialize(s);
            if(Preferences.Dolls==null)Preferences.Dolls=new List<DollPosition>();
            for(int i=0;i<Preferences.Dolls.Count;i++){
                if(Preferences.Dolls[i]==null)Preferences.Dolls[i]=new DollPosition();var p=Preferences.Dolls[i];
                if(!ScreenLayout.Finite(p.X))p.X=0;if(!ScreenLayout.Finite(p.Y))p.Y=0;if(!ScreenLayout.Finite(p.Scale)||p.Scale<=0)p.Scale=.85;
            }Preferences.BubbleText=ActiveBubbleText;
        }catch{Preferences=new Preferences();}}
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
            menu.Items.Add("显示文字气泡",null,delegate{foreach(var w in Dolls)if(w.IsVisible)w.Say();});
            menu.Items.Add("自定义气泡文字…",null,delegate{var owner=Dolls.FirstOrDefault(w=>w.IsVisible);if(owner==null){owner=Dolls[0];owner.Show();}EditBubbleText(owner);});
            var speech=new Forms.ToolStripMenuItem("点击时显示气泡"){CheckOnClick=false};
            speech.Click+=delegate{SetClickToSpeak(!Preferences.ClickToSpeak);};menu.Items.Add(speech);
            var breathing=new Forms.ToolStripMenuItem("轻微呼吸");breathing.Click+=delegate{SetBreathing(!Preferences.Breathing);};menu.Items.Add(breathing);
            var blinking=new Forms.ToolStripMenuItem("偶尔眨眼（睁眼角色）");blinking.Click+=delegate{SetBlinking(!Preferences.Blinking);};menu.Items.Add(blinking);
            menu.Opening+=delegate{speech.Checked=Preferences.ClickToSpeak;breathing.Checked=Preferences.Breathing;blinking.Checked=Preferences.Blinking;};
            menu.Items.Add("显示全部玩偶",null,delegate{foreach(var w in Dolls)w.Show();SavePreferences();});
            menu.Items.Add("隐藏全部玩偶",null,delegate{foreach(var w in Dolls)w.Hide();SavePreferences();});
            menu.Items.Add("恢复默认位置和大小",null,delegate{ResetLayout();});
            menu.Items.Add("操作说明",null,delegate{MessageBox.Show("点击：弹簧摇头；按住拖动：移动玩偶\n右键“说话”：显示文字气泡（无声音）\n自定义气泡文字：修改所有玩偶的文字\n轻微呼吸 / 偶尔眨眼：可选，默认关闭\n右下角托盘：显示全部、一起摇和退出\n位置、大小和各选项会自动记住。","桌面弹簧玩偶");});
            menu.Items.Add("退出全部玩偶",null,delegate{Quit();});tray.ContextMenuStrip=menu;tray.DoubleClick+=delegate{ShakeAll();};
        }
        public void Quit(){if(quitting)return;quitting=true;SavePreferences();if(timer!=null)timer.Stop();if(tray!=null){tray.Visible=false;tray.Dispose();}Shutdown();}
        protected override void OnSessionEnding(SessionEndingCancelEventArgs e){SavePreferences();base.OnSessionEnding(e);}
        protected override void OnExit(ExitEventArgs e){Microsoft.Win32.SystemEvents.DisplaySettingsChanged-=OnDisplaySettingsChanged;base.OnExit(e);}
        private void RunSmoke(){
            Directory.CreateDirectory(output);var results=new List<string>();
            RuntimeChecks.Run(results);
            if(BitmapLoads!=1)throw new Exception("Sprite atlas decoded more than once");
            results.Add("PASS: all dolls share one atlas decode and cached alpha lookup");
            if(MotionRunning)throw new Exception("Animation timer running at idle startup");
            var spring=new Spring();spring.Kick(1);double max=0;bool negative=false;
            for(int i=0;i<1200;i++){spring.Step(1.0/120);max=Math.Max(max,Math.Abs(spring.Angle));if(spring.Angle<-.5)negative=true;}
            if(max<8||max>40||!negative||Math.Abs(spring.Angle)>.02)throw new Exception("Spring dynamics check failed");
            results.Add("PASS: spring oscillates in both directions and settles; peak="+max.ToString("F2"));
            foreach(var w in Dolls){w.VerifyNativeInteraction();if(!MotionRunning)throw new Exception("Native click did not wake animation timer");w.ResetMotion();results.Add("PASS: "+w.Spec.Name+" Windows mouse click wakes spring; transparent area passes hit testing");}
            ShakeAll();var testTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(450)};
            testTimer.Tick+=delegate{testTimer.Stop();
                foreach(var w in Dolls) {
                    if(!w.IsVisible||new WindowInteropHelper(w).Handle==IntPtr.Zero||Math.Abs(w.Rotation.Angle)<.5)throw new Exception("Window animation check failed");
                    double before=w.DollScale;w.SetScale(before+.15,false);if(w.Width<=DollWindow.SceneWidth*before)throw new Exception("Scale check failed");w.SetScale(before,false);
                    w.ContextMenu.IsOpen=true;w.ContextMenu.IsOpen=false;
                    w.Capture(Path.Combine(output,"doll"+w.Index+"-animated.png"));
                    w.CaptureBlink(Path.Combine(output,"doll"+w.Index+"-blink.png"));
                    double currentAngle=w.Rotation.Angle;
                    foreach(double angle in new[]{-18.0,0.0,18.0}){w.Rotation.Angle=angle;w.Capture(Path.Combine(output,"doll"+w.Index+"-neck-"+angle+".png"));}w.Rotation.Angle=currentAngle;
                    w.VerifySpeechBubble(Path.Combine(output,"doll"+w.Index+"-speech.png"));
                    string original=Preferences.BubbleText;Preferences.BubbleText="这是自定义文字，用来检查较长的气泡能否正确换行并保留在当前屏幕内。";
                    w.VerifySpeechBubble(Path.Combine(output,"doll"+w.Index+"-custom-speech.png"));Preferences.BubbleText=original;
                    w.VerifyEdgePlacement(Path.Combine(output,"doll"+w.Index+"-edge-speech.png"));
                    results.Add("PASS: "+w.Spec.Name+" text-only speech bubble contains "+DollWindow.SpeechText);
                    results.Add("PASS: "+w.Spec.Name+" native transparent window, spring animation, resizing, context menu, rendered preview");
                }
                SetBreathing(true);SetBlinking(true);foreach(var w in Dolls)w.VerifyOptionalIdle();SetBreathing(false);SetBlinking(false);
                results.Add("PASS: optional breathing and blinking stop when hidden, resume when shown, and can be disabled");
                VerifyBubbleEditor(Path.Combine(output,"bubble-editor.png"));
                results.Add("PASS: custom bubble editor opens, accepts text through Save and updates the bubble");
                ShakeAll();
                var idleProbe=new DispatcherTimer {Interval=TimeSpan.FromSeconds(6)};bool waiting=false;long tickCount=0;
                idleProbe.Tick+=delegate {
                    if(!waiting){if(MotionRunning)throw new Exception("Animation timer did not stop after springs settled");tickCount=MotionTickCount;waiting=true;idleProbe.Interval=TimeSpan.FromMilliseconds(180);return;}
                    idleProbe.Stop();if(MotionTickCount!=tickCount)throw new Exception("Animation ticked while idle");
                    results.Add("PASS: motion timer sleeps after settling; idle tick count stays unchanged");
                    Dolls[0].Nudge(1);if(!MotionRunning)throw new Exception("Settled timer failed to wake");Dolls[0].ResetMotion();RefreshMotion();
                    results.Add("PASS: subsequent click restarts a sleeping animation timer");
                    File.WriteAllLines(Path.Combine(output,"smoke-test.txt"),results);Quit();
                };idleProbe.Start();};testTimer.Start();
        }
        private void VerifyBubbleEditor(string path) {
            string original=Preferences.BubbleText;var editorTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(180)};
            editorTimer.Tick+=delegate {
                editorTimer.Stop();var dialog=Windows.Cast<Window>().FirstOrDefault(w=>w.Title=="自定义气泡文字");
                if(dialog==null)throw new Exception("Bubble editor did not open");
                var grid=(Grid)dialog.Content;grid.Children.OfType<TextBox>().Single().Text="优化完成，无语音";
                dialog.UpdateLayout();var bitmap=new RenderTargetBitmap((int)dialog.ActualWidth,(int)dialog.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(dialog);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var f=File.Create(path))encoder.Save(f);
                var buttons=grid.Children.OfType<StackPanel>().Single();var save=buttons.Children.OfType<Button>().Single(b=>(string)b.Content=="保存");
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };editorTimer.Start();EditBubbleText(Dolls[0]);
            if(Preferences.BubbleText!="优化完成，无语音")throw new Exception("Custom editor did not save text");
            Dolls[0].VerifySpeechBubble(Path.Combine(output,"bubble-editor-result.png"));Preferences.BubbleText=original;
        }
    }
}
