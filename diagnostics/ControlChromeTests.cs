using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LocalLink
{
    // Only synthetic controls in this test's own off-screen form. No filter,
    // settings, network, user applications, screen capture or input injection.
    static class ControlChromeTests
    {
        static int checks;
        [StructLayout(LayoutKind.Sequential)] struct PointNative{public int X,Y;}
        [StructLayout(LayoutKind.Sequential)] struct RectNative{public int Left,Top,Right,Bottom;}
        [DllImport("gdi32.dll")] static extern bool SetViewportOrgEx(IntPtr context,int x,int y,out PointNative previous);
        [DllImport("gdi32.dll")] static extern bool GetViewportOrgEx(IntPtr context,out PointNative value);
        [DllImport("gdi32.dll")] static extern int IntersectClipRect(IntPtr context,int left,int top,int right,int bottom);
        [DllImport("gdi32.dll")] static extern int GetClipBox(IntPtr context,out RectNative value);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window,out RectNative rectangle);
        [StructLayout(LayoutKind.Sequential)] struct ScrollBarInfo
        {
            public int Size;public RectNative Rectangle;public int LineButton,ThumbTop,ThumbBottom,Reserved;
            [MarshalAs(UnmanagedType.ByValArray,SizeConst=6)] public int[] State;
        }
        [DllImport("user32.dll")] static extern bool GetScrollBarInfo(IntPtr window,int objectId,ref ScrollBarInfo info);

        [STAThread] static int Main()
        {
            try
            {
                Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
                foreach(string mode in new[]{"light","dark"}){Run(mode);RunScrollChrome(mode);}
                Console.WriteLine("PASS: "+checks+" control chrome assertions");return 0;
            }
            catch(Exception error){Console.Error.WriteLine(error.ToString());return 1;}
        }
        static void Check(bool condition,string message)
        {if(!condition)throw new InvalidOperationException(message);checks++;Console.WriteLine("PASS: "+message);}
        static void Run(string mode)
        {
            var palette=AppTheme.Read(mode);
            if(!palette.HighContrast)
            {
                Check(AppTheme.ContrastRatio(palette.PrimaryFill,palette.PrimaryText)>=4.5,mode+": primary action text contrast");
                if(mode=="dark")
                    Check(palette.PrimaryFill.R<128&&palette.PrimaryFill.G<128&&palette.PrimaryFill.B<128,"dark: primary fill is not a bright white surface");
            }
            using(var form=new Form{StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000),ClientSize=new Size(560,200),ShowInTaskbar=false})
            {
                var text=new ThemedTextBox{Location=new Point(20,20),Width=240,Text="Synthetic editor",Font=AppTheme.Font(10.5f,FontStyle.Regular)};
                var number=new ThemedNumericUpDown{Location=new Point(20,70),Width=120,Minimum=1,Maximum=100,Increment=2,Value=10,Font=AppTheme.Font(10.5f,FontStyle.Regular)};
                var sink=new Button{Location=new Point(330,20),Size=new Size(140,30),Text="Synthetic focus"};
                form.Controls.Add(text);form.Controls.Add(number);form.Controls.Add(sink);palette.Apply(form);
                Check(text is TextBox&&number is NumericUpDown,mode+": original native control contracts");
                Check(text.BorderStyle==BorderStyle.FixedSingle&&number.BorderStyle==BorderStyle.FixedSingle,mode+": no three-dimensional native bevel");
                Check(text.Palette==palette&&number.Palette==palette,mode+": palette assigned to input controls");
                text.Select(1,3);Check(text.SelectionStart==1&&text.SelectionLength==3,mode+": native text selection retained");
                text.SelectedText="XYZ";Check(text.Text=="SXYZhetic editor",mode+": native selected-text replacement retained");
                number.UpButton();Check(number.Value==12,mode+": native numeric increment retained");
                number.DownButton();Check(number.Value==10,mode+": native numeric decrement retained");
                form.Show();form.ActiveControl=sink;Application.DoEvents();
                Check(!text.ContainsFocus&&!number.ContainsFocus,mode+": deterministic inactive inputs");
                CheckPrintState(text,mode+": text WM_PRINT DC");CheckPrintState(number,mode+": numeric WM_PRINT DC");
                if(!palette.HighContrast)
                {
                    CheckFrame(text,palette.ControlBorder,mode+": inactive text outline");
                    CheckFrame(number,palette.ControlBorder,mode+": inactive numeric outline");
                    CheckSpinFace(number,palette.Input,mode+": native spin face has no white bevel");
                    form.ActiveControl=text;Application.DoEvents();Check(text.ContainsFocus,mode+": own text focus");
                    CheckFrame(text,palette.Accent,mode+": focused text outline");
                    form.ActiveControl=number;Application.DoEvents();Check(number.ContainsFocus,mode+": own numeric focus");
                    CheckFrame(number,palette.Accent,mode+": focused numeric outline");
                    // Force the rendering bypass on these synthetic controls only.
                    palette.HighContrast=true;palette.Apply(form);Application.DoEvents();
                    CheckPrintState(text,mode+": High Contrast text DC bypass");
                    CheckPrintState(number,mode+": High Contrast numeric DC bypass");
                    using(var bitmap=new Bitmap(number.Width,number.Height))number.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));
                    number.UpButton();Check(number.Value==12,mode+": High Contrast keeps native numeric behavior");
                }
                else Console.WriteLine("SKIP: custom pixels because Windows High Contrast is active");
                form.Close();
            }
        }
        static void CheckFrame(Control control,Color expected,string name)
        {
            using(var bitmap=new Bitmap(control.Width,control.Height))
            {
                control.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));
                Color actual=bitmap.GetPixel(0,control.Height/2);
                Check(actual.ToArgb()==expected.ToArgb(),name+" ("+actual+", expected "+expected+")");
            }
        }
        static void RunScrollChrome(string mode)
        {
            var palette=AppTheme.Read(mode);
            var chromeType=typeof(AppTheme).Assembly.GetType("LocalLink.ThemeScrollChrome",true);
            var instances=(System.Collections.IDictionary)chromeType.GetField("instances",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
            int before=instances.Count;
            using(var form=new Form{StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000),ClientSize=new Size(720,230),ShowInTaskbar=false})
            {
                var panel=new Panel{Location=new Point(10,10),Size=new Size(210,180),AutoScroll=true,AutoScrollMinSize=new Size(420,540)};
                var text=new ThemedTextBox{Location=new Point(240,10),Size=new Size(210,180),Multiline=true,WordWrap=false,ScrollBars=ScrollBars.Both};
                text.Text=new string('x',100)+Environment.NewLine+string.Join(Environment.NewLine,new string('y',50).PadRight(70).Split(' '));
                var list=new ListView{Location=new Point(470,10),Size=new Size(230,180),View=View.Details};
                list.Columns.Add("Synthetic result",130);for(int i=0;i<40;i++)list.Items.Add("Row "+i);
                form.Controls.AddRange(new Control[]{panel,text,list});palette.Apply(form);form.Show();Application.DoEvents();
                typeof(Control).GetMethod("RecreateHandle",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(text,null);Application.DoEvents();
                Check(text.IsHandleCreated,mode+": scrolling editor handle recreation retained");
                panel.AutoScrollPosition=new Point(12,80);Application.DoEvents();
                Check(panel.AutoScrollPosition.Y==-80,mode+": native panel scroll position retained");
                text.SelectionStart=4;text.SelectionLength=3;Check(text.SelectionLength==3,mode+": scrolling editor selection retained");
                list.EnsureVisible(35);Application.DoEvents();Check(list.TopItem!=null&&list.TopItem.Index>0,mode+": native results scrolling retained");
                foreach(Control control in new Control[]{panel,text,list})
                {
                    if(mode=="dark"&&!palette.HighContrast)
                    {
                        CheckNativePrintState(control,mode+": scrollbar "+control.GetType().Name+" print DC");
                        CheckDarkScrollTrack(control);
                    }
                }
                palette.HighContrast=true;palette.Apply(form);Application.DoEvents();
                panel.AutoScrollPosition=new Point(0,120);Check(panel.AutoScrollPosition.Y==-120,mode+": High Contrast native scroll fallback");
                form.Close();
            }
            Check(instances.Count==before,mode+": scrollbar hooks released with own controls");
        }
        static void CheckDarkScrollTrack(Control control)
        {
            var info=new ScrollBarInfo{Size=Marshal.SizeOf(typeof(ScrollBarInfo)),State=new int[6]};RectNative window;
            Check(GetScrollBarInfo(control.Handle,-5,ref info)&&GetWindowRect(control.Handle,out window),"dark: scrollbar geometry available for "+control.GetType().Name);
            GetWindowRect(control.Handle,out window);
            int x=info.Rectangle.Left-window.Left+2;
            int y=info.Rectangle.Bottom-window.Top-Math.Max(2,info.LineButton/2);
            using(var bitmap=new Bitmap(control.Width,control.Height))
            {
                control.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));
                Check(x>=0&&x<bitmap.Width&&y>=0&&y<bitmap.Height,"dark: native scrollbar sample bounds");
                Color sample=bitmap.GetPixel(x,y);
                Check(sample.R<150&&sample.G<150&&sample.B<150,"dark: "+control.GetType().Name+" scrollbar has no bright white face");
            }
        }
        static void CheckNativePrintState(Control control,string name)
        {
            using(var bitmap=new Bitmap(control.Width+48,control.Height+48))
            using(var graphics=Graphics.FromImage(bitmap))
            {
                IntPtr context=graphics.GetHdc();
                try
                {
                    PointNative previous,before,after;RectNative clipBefore,clipAfter;
                    SetViewportOrgEx(context,17,13,out previous);IntersectClipRect(context,2,2,control.Width+20,control.Height+20);
                    GetViewportOrgEx(context,out before);GetClipBox(context,out clipBefore);
                    SendMessage(control.Handle,0x0317,context,new IntPtr(0x0002|0x0004|0x0010));
                    GetViewportOrgEx(context,out after);GetClipBox(context,out clipAfter);
                    Check(before.X==after.X&&before.Y==after.Y,name+": viewport restored");
                    Check(clipBefore.Left==clipAfter.Left&&clipBefore.Top==clipAfter.Top&&clipBefore.Right==clipAfter.Right&&clipBefore.Bottom==clipAfter.Bottom,name+": caller clip restored");
                }
                finally{graphics.ReleaseHdc(context);}
            }
        }
        static void CheckSpinFace(ThemedNumericUpDown control,Color expected,string name)
        {
            foreach(Control child in control.Controls)
                if(child.GetType().Name=="UpDownButtons")
                {
                    using(var bitmap=new Bitmap(child.Width,child.Height))
                    {
                        child.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));
                        Check(bitmap.GetPixel(child.Width-2,1).ToArgb()==expected.ToArgb(),name);return;
                    }
                }
            Check(false,name+": native spin child missing");
        }
        static void CheckPrintState(Control control,string name)
        {
            using(var bitmap=new Bitmap(control.Width+48,control.Height+48))
            using(var graphics=Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Magenta);IntPtr context=graphics.GetHdc();
                try
                {
                    PointNative previous,before,after;RectNative clipBefore,clipAfter;
                    SetViewportOrgEx(context,17,13,out previous);IntersectClipRect(context,2,2,control.Width+20,control.Height+20);
                    GetViewportOrgEx(context,out before);GetClipBox(context,out clipBefore);
                    var message=Message.Create(control.Handle,0x0317,context,new IntPtr(0x0002|0x0004|0x0010));
                    var method=control.GetType().GetMethod("WndProc",BindingFlags.Instance|BindingFlags.NonPublic);
                    method.Invoke(control,new object[]{message});
                    GetViewportOrgEx(context,out after);GetClipBox(context,out clipAfter);
                    Check(before.X==after.X&&before.Y==after.Y,name+": viewport restored");
                    Check(clipBefore.Left==clipAfter.Left&&clipBefore.Top==clipAfter.Top&&clipBefore.Right==clipAfter.Right&&clipBefore.Bottom==clipAfter.Bottom,name+": caller clip restored");
                }
                finally{graphics.ReleaseHdc(context);}
            }
        }
    }
}
