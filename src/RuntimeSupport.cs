using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace DesktopDolls {
    // One decoded atlas and one alpha byte per pixel, shared by all dolls.
    public sealed class SpriteAtlas {
        public readonly BitmapSource Bitmap;
        private readonly byte[] alpha;
        public SpriteAtlas(BitmapSource bitmap) {
            Bitmap=bitmap;
            var pixels=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];
            bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0);
            alpha=new byte[bitmap.PixelWidth*bitmap.PixelHeight];
            for(int i=0;i<alpha.Length;i++)alpha[i]=pixels[i*4+3];
        }
        public byte Alpha(double x,double y,int left,int top,int width,int height) {
            if(x<0||y<0||x>=width||y>=height)return 0;
            return alpha[(top+(int)y)*Bitmap.PixelWidth+left+(int)x];
        }
    }

    public static class ScreenLayout {
        public static bool Finite(double n){return !double.IsNaN(n)&&!double.IsInfinity(n);}
        public static List<Rect> WorkAreas(Window window) {
            var source=window==null?null:PresentationSource.FromVisual(window);
            Matrix matrix=source==null?Matrix.Identity:source.CompositionTarget.TransformFromDevice;
            // Before an HWND exists, derive the same system-DPI transform from the primary work area.
            if(source==null) {
                var pixels=Forms.Screen.PrimaryScreen.WorkingArea;var logical=SystemParameters.WorkArea;
                matrix.Scale(logical.Width/pixels.Width,logical.Height/pixels.Height);
            }
            return Forms.Screen.AllScreens.OrderByDescending(s=>s.Primary).Select(s=>{
                var r=s.WorkingArea;Point a=matrix.Transform(new Point(r.Left,r.Top)),b=matrix.Transform(new Point(r.Right,r.Bottom));
                return new Rect(a,b);
            }).ToList();
        }
        public static Rect Choose(Rect target,IList<Rect> areas) {
            if(areas==null||areas.Count==0)throw new ArgumentException("No monitor work areas");
            Rect best=areas[0];double score=-1,distance=double.MaxValue;
            Point center=new Point(target.X+target.Width/2,target.Y+target.Height/2);
            foreach(Rect area in areas) {
                Rect overlap=Rect.Intersect(target,area);double amount=overlap.IsEmpty?0:overlap.Width*overlap.Height;
                double dx=Math.Max(area.Left-center.X,Math.Max(0,center.X-area.Right));
                double dy=Math.Max(area.Top-center.Y,Math.Max(0,center.Y-area.Bottom));double d=dx*dx+dy*dy;
                if(amount>score||(amount==score&&d<distance)){best=area;score=amount;distance=d;}
            }return best;
        }
        public static Rect ForWindow(Window window) {
            var areas=WorkAreas(window);
            if(!Finite(window.Left)||!Finite(window.Top)||!Finite(window.Width)||!Finite(window.Height))return areas[0];
            return Choose(new Rect(window.Left,window.Top,window.Width,window.Height),areas);
        }
        public static Rect AtPointer(Window window) {
            var mouse=Forms.Control.MousePosition;var screen=Forms.Screen.FromPoint(mouse);
            var source=PresentationSource.FromVisual(window);Matrix matrix=source.CompositionTarget.TransformFromDevice;
            var r=screen.WorkingArea;return new Rect(matrix.Transform(new Point(r.Left,r.Top)),matrix.Transform(new Point(r.Right,r.Bottom)));
        }
        public static Rect Clamp(Rect target,Rect area) {
            double width=Math.Min(target.Width,area.Width),height=Math.Min(target.Height,area.Height);
            return new Rect(Math.Max(area.Left,Math.Min(area.Right-width,target.Left)),
                Math.Max(area.Top,Math.Min(area.Bottom-height,target.Top)),width,height);
        }
        public static Rect Bubble(Size size,Point headTop,Rect doll,Rect area) {
            double x=headTop.X-size.Width/2,y=headTop.Y-size.Height-8;
            if(y<area.Top) {
                y=headTop.Y+12;
                if(doll.Right+8+size.Width<=area.Right)x=doll.Right+8;
                else if(doll.Left-8-size.Width>=area.Left)x=doll.Left-8-size.Width;
            }
            return Clamp(new Rect(x,y,size.Width,size.Height),area);
        }
    }

    public static class BubbleEditor {
        public static string Show(Window owner,string current) {
            string result=null;var dialog=new Window {Title="自定义气泡文字",Owner=owner,ShowInTaskbar=false,
                Width=380,Height=235,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,
                Background=new SolidColorBrush(Color.FromRgb(255,250,253)),Topmost=owner.Topmost};
            var grid=new Grid {Margin=new Thickness(18)};
            grid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            grid.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
            grid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            grid.Children.Add(new TextBlock {Text="输入要显示的文字（最多 40 字，无声音）",Margin=new Thickness(0,0,0,12),FontSize=14});
            var input=new TextBox {Text=current,MaxLength=40,TextWrapping=TextWrapping.Wrap,AcceptsReturn=false,
                FontSize=18,Padding=new Thickness(8),VerticalContentAlignment=VerticalAlignment.Center};Grid.SetRow(input,1);grid.Children.Add(input);
            var buttons=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,14,0,0)};
            var reset=new Button {Content="恢复默认",Padding=new Thickness(9,5,9,5),Margin=new Thickness(0,0,8,0)};
            var cancel=new Button {Content="取消",IsCancel=true,Padding=new Thickness(12,5,12,5),Margin=new Thickness(0,0,8,0)};
            var save=new Button {Content="保存",IsDefault=true,Padding=new Thickness(12,5,12,5)};
            reset.Click+=delegate{input.Text=DollWindow.SpeechText;};
            save.Click+=delegate{result=App.CleanBubbleText(input.Text);dialog.DialogResult=true;};
            buttons.Children.Add(reset);buttons.Children.Add(cancel);buttons.Children.Add(save);Grid.SetRow(buttons,2);grid.Children.Add(buttons);
            dialog.Content=grid;dialog.Loaded+=delegate{
                Rect p=ScreenLayout.Clamp(new Rect(dialog.Left,dialog.Top,dialog.Width,dialog.Height),ScreenLayout.ForWindow(owner));
                dialog.Left=p.Left;dialog.Top=p.Top;input.Focus();input.SelectAll();
            };dialog.ShowDialog();return result;
        }
    }

    public static class RuntimeChecks {
        private static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
        public static void Run(List<string> results) {
            var areas=new List<Rect>{new Rect(0,0,1920,1040),new Rect(-1280,0,1280,720),new Rect(2240,80,1280,944)};
            Check(ScreenLayout.Choose(new Rect(-900,50,360,400),areas)==areas[1],"Negative-coordinate monitor selection failed");
            Check(ScreenLayout.Choose(new Rect(2050,200,80,100),areas)==areas[2],"Monitor-gap recovery failed");
            foreach(Rect area in areas) {
                Rect doll=ScreenLayout.Clamp(new Rect(area.Right-40,area.Bottom-30,360,400),area);
                Check(area.Contains(doll),"Doll clamp failed");
                foreach(Point p in new[]{new Point(area.Left+10,area.Top+2),new Point(area.Right-10,area.Bottom-2)}) {
                    Rect bubble=ScreenLayout.Bubble(new Size(230,140),p,doll,area);Check(area.Contains(bubble),"Bubble boundary placement failed");
                }
            }results.Add("PASS: simulated negative-coordinate monitors, monitor gaps, doll resizing bounds and bubble edges");
            var spring=new Spring();double last=0;
            for(int i=0;i<2400;i++){if(i%10==0)spring.Kick(1);spring.Step(1.0/120);double next=spring.VisibleAngle;
                Check(Math.Abs(next)<18,"Soft spring limit exceeded");Check(Math.Abs(next-last)<5,"Repeated-click motion jumped");last=next;}
            spring.Angle=18;double a=spring.VisibleAngle;spring.Angle=18.001;double b=spring.VisibleAngle;
            Check(b>a&&b-a<.001,"Spring limit has a hard flat cutoff");results.Add("PASS: repeated clicks stay inside a smooth spring limit");
            Check(App.CleanBubbleText("  ")==DollWindow.SpeechText,"Empty bubble fallback failed");
            Check(App.CleanBubbleText(new string('好',60)).Length==40,"Bubble length limit failed");
            Check(App.CleanBubbleText("一\n二")=="一 二","Bubble newline normalization failed");
            var old="<Preferences><OnTop>false</OnTop><ClickToSpeak>true</ClickToSpeak><Dolls /></Preferences>";
            using(var reader=new System.IO.StringReader(old)) {
                var prefs=(Preferences)new System.Xml.Serialization.XmlSerializer(typeof(Preferences)).Deserialize(reader);
                Check(!prefs.Breathing&&!prefs.Blinking&&prefs.BubbleText==DollWindow.SpeechText,"Old settings defaults changed");
                Check(prefs.ClickToSpeak&&!prefs.OnTop,"Old preferences lost");
            }results.Add("PASS: custom bubble validation and old settings compatibility");
        }
    }
}
