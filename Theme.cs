using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LocalLink
{
    // Reads only Windows appearance preferences. No theme changes or device data.
    public sealed class AppTheme
    {
        public Color Background, Surface, Card, Input, Hover, Selected, Pressed, Text, Muted, Border, ControlBorder, Accent, AccentText, Success, Error;
        public bool HighContrast,Dark;
        public Color PrimaryFill { get { return HighContrast?Accent:Dark?Mix(Card,Accent,.32):Text; } }
        public Color PrimaryText { get { return HighContrast?AccentText:Dark?Text:Surface; } }
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
        public static Icon CreateApplicationIcon()
        {
            using(var bitmap=new Bitmap(32,32))
            using(var g=Graphics.FromImage(bitmap))
            {
                g.Clear(Color.FromArgb(30,30,30));g.SmoothingMode=SmoothingMode.AntiAlias;
                using(var pen=new Pen(Color.FromArgb(242,242,242),2.8f))
                {
                    g.DrawEllipse(pen,4,10,15,11);g.DrawEllipse(pen,13,10,15,11);
                }
                IntPtr handle=bitmap.GetHicon();
                try{using(var icon=Icon.FromHandle(handle))return (Icon)icon.Clone();}
                finally{DestroyIcon(handle);}
            }
        }
        public static Font Font(float size, FontStyle style)
        {
            // Static Segoe UI has consistent metrics in native WinForms/GDI controls;
            // Variable can resolve to different faces in GDI and GDI+ on Windows.
            return new Font("Segoe UI",size,style,GraphicsUnit.Point);
        }
        [DllImport("dwmapi.dll")] static extern int DwmGetColorizationColor(out uint color, out bool opaque);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
        public static AppTheme Read(bool forceDark)
        {return Read(forceDark,false);}
        public static AppTheme Read(bool forceDark,bool forceLight)
        {return Read("system",forceDark,forceLight);}
        public static AppTheme Read(string preference,bool forceDark=false,bool forceLight=false)
        {
            var t = new AppTheme(); t.HighContrast = SystemInformation.HighContrast;
            if (t.HighContrast)
            {
                t.Background=t.Surface=t.Card=t.Input=SystemColors.Window;t.Hover=t.Selected=t.Pressed=SystemColors.Highlight;t.Text=SystemColors.WindowText;
                t.Muted=SystemColors.WindowText;t.Border=t.ControlBorder=SystemColors.WindowText;t.Accent=SystemColors.Highlight;
                t.AccentText=SystemColors.HighlightText; t.Success=t.Error=SystemColors.WindowText; return t;
            }
            string selection=(preference??"system").Trim().ToLowerInvariant();
            bool dark=forceDark||(!forceLight&&selection=="dark");
            if(!forceDark&&!forceLight&&selection!="dark"&&selection!="light") try
            {
                using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    dark=key!=null&&Convert.ToInt32(key.GetValue("AppsUseLightTheme",1))==0;
            } catch { }
            t.Dark=dark;
            // Four calm surface levels: sidebar, canvas, card, and inset control.
            // Separators are decorative; control outlines retain >=3:1 contrast.
            t.Background=dark?Color.FromArgb(22,23,23):Color.FromArgb(245,245,242);
            t.Surface=dark?Color.FromArgb(26,27,27):Color.FromArgb(250,250,247);
            t.Card=dark?Color.FromArgb(32,33,33):Color.FromArgb(255,255,253);
            t.Input=dark?Color.FromArgb(26,27,27):Color.FromArgb(249,249,246);
            t.Hover=dark?Color.FromArgb(43,44,44):Color.FromArgb(239,240,236);
            t.Selected=dark?Color.FromArgb(47,48,48):Color.FromArgb(231,233,228);
            t.Pressed=dark?Color.FromArgb(56,57,57):Color.FromArgb(219,223,215);
            t.Text=dark?Color.FromArgb(239,238,234):Color.FromArgb(34,36,34);
            t.Muted=dark?Color.FromArgb(178,180,177):Color.FromArgb(91,96,91);
            t.Border=dark?Color.FromArgb(59,61,60):Color.FromArgb(218,221,215);
            t.ControlBorder=dark?Color.FromArgb(132,138,134):Color.FromArgb(121,128,118);
            t.Accent=dark?Color.FromArgb(153,184,218):Color.FromArgb(64,99,139);
            try {uint raw;bool opaque;if(DwmGetColorizationColor(out raw,out opaque)==0)t.Accent=Color.FromArgb((int)(raw>>16&255),(int)(raw>>8&255),(int)(raw&255));} catch { }
            // Windows accent stays recognizable but cannot become an invisible focus cue.
            t.Accent=EnsureContrast(t.Accent,t.Card,3.0,dark?Color.White:Color.Black);
            t.Accent=EnsureContrast(t.Accent,t.Selected,3.0,dark?Color.White:Color.Black);
            t.Accent=EnsureContrast(t.Accent,t.Pressed,3.0,dark?Color.White:Color.Black);
            t.AccentText=Luminance(t.Accent)>.179?Color.Black:Color.White;
            t.Success=dark?Color.FromArgb(137,204,171):Color.FromArgb(36,113,78);
            t.Error=dark?Color.FromArgb(237,169,167):Color.FromArgb(163,59,55);
            return t;
        }
        public static double ContrastRatio(Color a,Color b)
        {double x=Luminance(a),y=Luminance(b);return (Math.Max(x,y)+.05)/(Math.Min(x,y)+.05);}
        internal static Color Mix(Color first,Color second,double amount)
        {return Color.FromArgb((int)Math.Round(first.R+(second.R-first.R)*amount),(int)Math.Round(first.G+(second.G-first.G)*amount),(int)Math.Round(first.B+(second.B-first.B)*amount));}
        static Color EnsureContrast(Color value,Color background,double minimum,Color destination)
        {
            Color original=value;
            for(int step=1;step<=20&&ContrastRatio(value,background)<minimum;step++)
            {
                double amount=step/20.0;
                value=Color.FromArgb((int)(original.R+(destination.R-original.R)*amount),(int)(original.G+(destination.G-original.G)*amount),(int)(original.B+(destination.B-original.B)*amount));
            }
            return value;
        }
        static double Luminance(Color color)
        {return .2126*Linear(color.R)+.7152*Linear(color.G)+.0722*Linear(color.B);}
        static double Linear(byte channel){double c=channel/255.0;return c<=.04045?c/12.92:Math.Pow((c+.055)/1.055,2.4);}
        void ApplyWindowChrome(Form form)
        {
            if(form==null||form.IsDisposed||!form.IsHandleCreated)return;
            try
            {
                // This affects only our existing HWND. Unsupported attributes are harmless;
                // the fallback keeps earlier Windows builds on their normal caption style.
                int dark=Dark&&!HighContrast?1:0;
                if(DwmSetWindowAttribute(form.Handle,20,ref dark,4)!=0)
                    DwmSetWindowAttribute(form.Handle,19,ref dark,4);
                int border=HighContrast?unchecked((int)0xffffffff):ColorRef(Border);
                int caption=HighContrast?unchecked((int)0xffffffff):ColorRef(Background);
                int text=HighContrast?unchecked((int)0xffffffff):ColorRef(Text);
                // Windows 11: border, caption fill, caption text. DEFAULT restores the
                // system-controlled colors when Windows High Contrast is selected.
                DwmSetWindowAttribute(form.Handle,34,ref border,4);
                DwmSetWindowAttribute(form.Handle,35,ref caption,4);
                DwmSetWindowAttribute(form.Handle,36,ref text,4);
            }
            catch(DllNotFoundException){}
            catch(EntryPointNotFoundException){}
            catch(ExternalException){}
        }
        static int ColorRef(Color color){return color.R|(color.G<<8)|(color.B<<16);}
        public void ApplyMenu(ContextMenuStrip menu)
        {
            if(menu==null||menu.IsDisposed)return;
            menu.BackColor=Surface;menu.ForeColor=Text;
            menu.Renderer=HighContrast?(ToolStripRenderer)new ToolStripSystemRenderer():new ThemeMenuRenderer(this);
            ApplyMenuItems(menu.Items);
        }
        void ApplyMenuItems(ToolStripItemCollection items)
        {
            foreach(ToolStripItem item in items)
            {
                item.BackColor=Surface;item.ForeColor=item.Enabled?Text:Muted;
                var nested=item as ToolStripDropDownItem;
                if(nested!=null&&nested.HasDropDownItems)
                {nested.DropDown.BackColor=Surface;nested.DropDown.ForeColor=Text;ApplyMenuItems(nested.DropDownItems);}
            }
        }
        public void Apply(Control c)
        {
            ApplyWindowChrome(c as Form);
            string role=c.Tag as string;
            bool sidebar=role=="sidebar"||role=="sidebar_muted";
            bool inCard=role=="card";
            if(!inCard&&role!="root"&&!sidebar)
                for(Control parent=c.Parent;parent!=null;parent=parent.Parent)
                    if(parent is ThemedPanel||(parent.Tag as string)=="card"){inCard=true;break;}
            c.BackColor=role=="root"||sidebar?Background:inCard?Card:Surface;c.ForeColor=role=="muted"||role=="sidebar_muted"?Muted:Text;
            var button=c as Button;
            if(button!=null)
            {
                bool primary=role=="primary";
                button.BackColor=primary?PrimaryFill:Card;button.ForeColor=primary?PrimaryText:Text;
                button.FlatAppearance.BorderColor=primary?button.BackColor:ControlBorder;
                button.FlatAppearance.MouseOverBackColor=primary?Mix(button.BackColor,button.ForeColor,.08):Hover;
                button.FlatAppearance.MouseDownBackColor=primary?Mix(button.BackColor,button.ForeColor,.16):Pressed;
            }
            var themedButton=c as ThemedButton;if(themedButton!=null){themedButton.Palette=this;themedButton.Invalidate();}
            var themedCheck=c as ThemedCheckBox;if(themedCheck!=null){themedCheck.Palette=this;themedCheck.Invalidate();}
            var themedCombo=c as ThemedComboBox;if(themedCombo!=null){themedCombo.Palette=this;themedCombo.Invalidate();}
            var themedText=c as ThemedTextBox;if(themedText!=null){themedText.Palette=this;themedText.Invalidate();}
            var themedNumber=c as ThemedNumericUpDown;if(themedNumber!=null){themedNumber.Palette=this;themedNumber.Invalidate();}
            var themedPanel=c as ThemedPanel;if(themedPanel!=null){themedPanel.Palette=this;themedPanel.Invalidate();}
            var themedTable=c as ThemedTableLayoutPanel;if(themedTable!=null){themedTable.Palette=this;themedTable.Invalidate();}
            if(c is TextBoxBase||c is ComboBox||c is NumericUpDown||c is ListView)c.BackColor=Input;
            ThemeScrollChrome.Apply(c,this);
            foreach(Control child in c.Controls)Apply(child);
        }
    }
    // Repaint only our native scrollbar faces. Windows keeps their geometry,
    // hit testing, scrolling, keyboard behavior and accessibility implementation.
    sealed class ThemeScrollChrome:NativeWindow,IDisposable
    {
        static readonly Dictionary<Control,ThemeScrollChrome> instances=new Dictionary<Control,ThemeScrollChrome>();
        readonly Control owner;AppTheme palette;HeaderChrome header;bool painting;
        [StructLayout(LayoutKind.Sequential)] struct NativeRect{public int Left,Top,Right,Bottom;}
        [StructLayout(LayoutKind.Sequential)] struct ScrollInfo
        {
            public int Size;public NativeRect Bounds;public int ArrowSize,ThumbTop,ThumbBottom,Reserved;
            [MarshalAs(UnmanagedType.ByValArray,SizeConst=6)]public uint[] States;
        }
        [DllImport("user32.dll")]static extern bool GetScrollBarInfo(IntPtr window,int id,ref ScrollInfo info);
        [DllImport("user32.dll")]static extern bool GetWindowRect(IntPtr window,out NativeRect rectangle);
        [DllImport("user32.dll")]static extern IntPtr GetWindowDC(IntPtr window);
        [DllImport("user32.dll")]static extern int ReleaseDC(IntPtr window,IntPtr dc);
        [DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wparam,IntPtr lparam);
        [DllImport("user32.dll",EntryPoint="SendMessageW")]static extern IntPtr SendRect(IntPtr window,int message,IntPtr wparam,ref NativeRect rectangle);
        [DllImport("gdi32.dll")]static extern int SaveDC(IntPtr dc);
        [DllImport("gdi32.dll")]static extern bool RestoreDC(IntPtr dc,int state);
        public static void Apply(Control control,AppTheme value)
        {
            var scrollable=control as ScrollableControl;
            if(!(control is ListView)&&!(control is TextBoxBase)&&(scrollable==null||!scrollable.AutoScroll))return;
            ThemeScrollChrome chrome;
            if(!instances.TryGetValue(control,out chrome))
            {chrome=new ThemeScrollChrome(control);instances.Add(control,chrome);}
            chrome.palette=value;chrome.Attach();chrome.PaintWindow();
        }
        ThemeScrollChrome(Control control)
        {owner=control;owner.HandleCreated+=Created;owner.HandleDestroyed+=Destroyed;owner.Disposed+=Disposed;}
        void Created(object sender,EventArgs e){Attach();}
        void Destroyed(object sender,EventArgs e)
        {if(header!=null){header.Dispose();header=null;}if(Handle!=IntPtr.Zero)ReleaseHandle();}
        void Disposed(object sender,EventArgs e){Dispose();}
        void Attach()
        {
            if(owner.IsDisposed||!owner.IsHandleCreated)return;
            if(Handle==IntPtr.Zero)AssignHandle(owner.Handle);
            if(owner is ListView)
            {
                IntPtr current=SendMessage(owner.Handle,0x101f,IntPtr.Zero,IntPtr.Zero); // LVM_GETHEADER
                if(header!=null&&header.Handle!=current){header.Dispose();header=null;}
                if(header==null&&current!=IntPtr.Zero)header=new HeaderChrome(this,current);
            }
        }
        public void Dispose()
        {
            owner.HandleCreated-=Created;owner.HandleDestroyed-=Destroyed;owner.Disposed-=Disposed;
            Destroyed(this,EventArgs.Empty);instances.Remove(owner);
        }
        protected override void WndProc(ref Message message)
        {
            int kind=message.Msg;bool printing=(kind==0x0317||kind==0x0318)&&message.WParam!=IntPtr.Zero;
            int saved=printing?SaveDC(message.WParam):0;
            try
            {
                base.WndProc(ref message);
                if(saved!=0)
                {
                    RestoreDC(message.WParam,saved);saved=SaveDC(message.WParam);
                    if(kind==0x0317&&IsDark&&((long)message.LParam&2)!=0)
                        try{using(var graphics=Graphics.FromHdc(message.WParam))PaintBars(graphics);}
                        catch(ExternalException){}
                        catch(ArgumentException){}
                }
                else if(kind==0x0085||kind==0x000f||kind==0x0005||kind==0x0047||kind==0x0114||kind==0x0115||kind==0x0113||kind==0x00a0||kind==0x00a1||kind==0x00a2||kind==0x02a2||kind==0x020a||kind==0x000a)
                {Attach();PaintWindow();}
            }
            finally{if(saved!=0)RestoreDC(message.WParam,saved);}
        }
        bool IsDark {get{return palette!=null&&palette.Dark&&!palette.HighContrast&&!owner.IsDisposed&&owner.IsHandleCreated;}}
        void PaintWindow()
        {
            if(painting||!IsDark||!owner.Visible)return;
            painting=true;IntPtr dc=IntPtr.Zero;int saved=0;
            try
            {
                dc=GetWindowDC(owner.Handle);if(dc==IntPtr.Zero)return;saved=SaveDC(dc);
                using(var graphics=Graphics.FromHdc(dc))PaintBars(graphics);
                if(header!=null)header.PaintWindow();
            }
            catch(ExternalException){}
            catch(ArgumentException){}
            finally{if(saved!=0)RestoreDC(dc,saved);if(dc!=IntPtr.Zero)ReleaseDC(owner.Handle,dc);painting=false;}
        }
        void PaintBars(Graphics graphics)
        {
            NativeRect window;if(!GetWindowRect(owner.Handle,out window))return;
            var vertical=ReadBar(-5,window);var horizontal=ReadBar(-6,window);
            PaintBar(graphics,vertical,true);PaintBar(graphics,horizontal,false);
            if(vertical.HasValue&&horizontal.HasValue)
            {
                Rectangle corner=new Rectangle(vertical.Value.Bounds.Left,horizontal.Value.Bounds.Top,vertical.Value.Bounds.Right-vertical.Value.Bounds.Left,horizontal.Value.Bounds.Bottom-horizontal.Value.Bounds.Top);
                using(var fill=new SolidBrush(palette.Input))graphics.FillRectangle(fill,corner);
            }
        }
        ScrollInfo? ReadBar(int id,NativeRect window)
        {
            var info=new ScrollInfo{Size=Marshal.SizeOf(typeof(ScrollInfo)),States=new uint[6]};
            if(!GetScrollBarInfo(owner.Handle,id,ref info)||(info.States[0]&(0x8000u|0x800u))!=0)return null;
            info.Bounds.Left-=window.Left;info.Bounds.Right-=window.Left;info.Bounds.Top-=window.Top;info.Bounds.Bottom-=window.Top;
            if(info.Bounds.Right<=info.Bounds.Left||info.Bounds.Bottom<=info.Bounds.Top)return null;
            return info;
        }
        void PaintBar(Graphics graphics,ScrollInfo? value,bool vertical)
        {
            if(!value.HasValue)return;var info=value.Value;
            var bounds=Rectangle.FromLTRB(info.Bounds.Left,info.Bounds.Top,info.Bounds.Right,info.Bounds.Bottom);
            using(var fill=new SolidBrush(palette.Input))graphics.FillRectangle(fill,bounds);
            int length=vertical?bounds.Height:bounds.Width,arrow=Math.Min(info.ArrowSize,length/2);
            Rectangle first=vertical?new Rectangle(bounds.Left,bounds.Top,bounds.Width,arrow):new Rectangle(bounds.Left,bounds.Top,arrow,bounds.Height);
            Rectangle last=vertical?new Rectangle(bounds.Left,bounds.Bottom-arrow,bounds.Width,arrow):new Rectangle(bounds.Right-arrow,bounds.Top,arrow,bounds.Height);
            DrawArrow(graphics,first,vertical,false,info.States[1]);DrawArrow(graphics,last,vertical,true,info.States[5]);
            if(info.ThumbBottom<=info.ThumbTop||(info.States[3]&0x8000u)!=0)return;
            int from=Math.Max(arrow,info.ThumbTop),to=Math.Min(length-arrow,info.ThumbBottom);
            if(to<=from)return;
            Rectangle thumb=vertical?new Rectangle(bounds.Left+3,bounds.Top+from,Math.Max(1,bounds.Width-6),to-from):new Rectangle(bounds.Left+from,bounds.Top+3,to-from,Math.Max(1,bounds.Height-6));
            using(var fill=new SolidBrush(!owner.Enabled||(info.States[3]&1)!=0?palette.Border:(info.States[3]&8)!=0?palette.Accent:palette.ControlBorder))graphics.FillRectangle(fill,thumb);
        }
        void DrawArrow(Graphics graphics,Rectangle bounds,bool vertical,bool forward,uint state)
        {
            if(bounds.Width<3||bounds.Height<3)return;
            if((state&8)!=0)using(var fill=new SolidBrush(palette.Pressed))graphics.FillRectangle(fill,bounds);
            float x=bounds.Left+bounds.Width/2f,y=bounds.Top+bounds.Height/2f,half=Math.Max(2,Math.Min(bounds.Width,bounds.Height)*.18f),direction=forward?1:-1;
            PointF[] points=vertical?new[]{new PointF(x-half,y-direction*half/2),new PointF(x,y+direction*half/2),new PointF(x+half,y-direction*half/2)}:new[]{new PointF(x-direction*half/2,y-half),new PointF(x+direction*half/2,y),new PointF(x-direction*half/2,y+half)};
            using(var pen=new Pen(!owner.Enabled||(state&1)!=0?palette.Muted:palette.Text,1.3f))graphics.DrawLines(pen,points);
        }
        sealed class HeaderChrome:NativeWindow,IDisposable
        {
            readonly ThemeScrollChrome parent;bool painting;
            public HeaderChrome(ThemeScrollChrome owner,IntPtr handle){parent=owner;AssignHandle(handle);}
            public void Dispose(){if(Handle!=IntPtr.Zero)ReleaseHandle();}
            protected override void WndProc(ref Message message)
            {
                int kind=message.Msg;bool printing=(kind==0x0317||kind==0x0318)&&message.WParam!=IntPtr.Zero;
                int saved=printing?SaveDC(message.WParam):0;
                try
                {
                    base.WndProc(ref message);
                    if(saved!=0)
                    {
                        RestoreDC(message.WParam,saved);saved=SaveDC(message.WParam);
                        if(parent.IsDark)
                            try{using(var graphics=Graphics.FromHdc(message.WParam))Paint(graphics);}
                            catch(ExternalException){}
                            catch(ArgumentException){}
                    }
                    else if(kind==0x000f||kind==0x0005||kind==0x0085)PaintWindow();
                }
                finally{if(saved!=0)RestoreDC(message.WParam,saved);}
            }
            public void PaintWindow()
            {
                if(painting||Handle==IntPtr.Zero||!parent.IsDark)return;painting=true;
                try
                {
                    using(var graphics=Graphics.FromHwnd(Handle))Paint(graphics);
                }
                catch(ExternalException){}
                catch(ArgumentException){}
                finally{painting=false;}
            }
            void Paint(Graphics graphics)
            {
                int count=(int)SendMessage(Handle,0x1200,IntPtr.Zero,IntPtr.Zero); // HDM_GETITEMCOUNT
                int right=0;
                for(int index=0;index<count;index++)
                {NativeRect item=new NativeRect();if(SendRect(Handle,0x1207,(IntPtr)index,ref item)!=IntPtr.Zero)right=Math.Max(right,item.Right);}
                NativeRect window;if(!GetWindowRect(Handle,out window))return;
                int width=window.Right-window.Left,height=window.Bottom-window.Top;
                if(right>=width||height<1)return;
                using(var fill=new SolidBrush(parent.palette.Input))graphics.FillRectangle(fill,right,0,width-right,height);
                using(var line=new Pen(parent.palette.Border))graphics.DrawLine(line,right,height-1,width,height-1);
            }
        }
    }
    // Paint only the one-pixel native frame, not the editor's client area.
    // Native editing, selection, IME, scrolling and accessibility remain unchanged.
    static class ThemeInputFrame
    {
        [DllImport("user32.dll")] static extern IntPtr GetWindowDC(IntPtr window);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window,IntPtr context);
        [DllImport("gdi32.dll")] static extern int SaveDC(IntPtr context);
        [DllImport("gdi32.dll")] static extern bool RestoreDC(IntPtr context,int state);
        public static int SavePrint(Message message)
        {return message.Msg==0x0317&&message.WParam!=IntPtr.Zero?SaveDC(message.WParam):0;}
        public static void RestorePrint(Message message,int saved)
        {if(saved!=0)RestoreDC(message.WParam,saved);}
        public static void Finish(Control control,AppTheme palette,bool hover,Message message,ref int saved)
        {
            if(saved!=0)
            {
                // WM_PRINT's DC belongs to the caller; undo native clip/origin changes
                // before our overlay and leave the original state intact on return.
                RestoreDC(message.WParam,saved);saved=SaveDC(message.WParam);
                if(palette!=null&&!palette.HighContrast)
                    try{using(var graphics=Graphics.FromHdc(message.WParam))Paint(control,palette,hover,graphics);}
                    catch(ExternalException){}
                    catch(ArgumentException){}
            }
            else if(message.Msg==0x0085||message.Msg==0x000F||message.Msg==0x0007||message.Msg==0x0008)
                PaintWindow(control,palette,hover);
        }
        public static void PaintWindow(Control control,AppTheme palette,bool hover)
        {
            if(palette==null||palette.HighContrast||control.IsDisposed||!control.IsHandleCreated)return;
            IntPtr dc=GetWindowDC(control.Handle);if(dc==IntPtr.Zero)return;int saved=SaveDC(dc);
            try{using(var graphics=Graphics.FromHdc(dc))Paint(control,palette,hover,graphics);}
            catch(ExternalException){}
            catch(ArgumentException){}
            finally{if(saved!=0)RestoreDC(dc,saved);ReleaseDC(control.Handle,dc);}
        }
        static void Paint(Control control,AppTheme palette,bool hover,Graphics graphics)
        {
            if(control.Width<2||control.Height<2)return;
            Color outline=!control.Enabled?palette.Border:control.ContainsFocus?palette.Accent:hover?AppTheme.Mix(palette.ControlBorder,palette.Text,.15):palette.ControlBorder;
            using(var pen=new Pen(outline))graphics.DrawRectangle(pen,0,0,control.Width-1,control.Height-1);
        }
    }
    public sealed class ThemedTextBox:TextBox
    {
        public AppTheme Palette;bool hover;
        public ThemedTextBox(){BorderStyle=BorderStyle.FixedSingle;}
        protected override void OnMouseEnter(EventArgs e){hover=true;base.OnMouseEnter(e);ThemeInputFrame.PaintWindow(this,Palette,hover);}
        protected override void OnMouseLeave(EventArgs e){hover=false;base.OnMouseLeave(e);ThemeInputFrame.PaintWindow(this,Palette,hover);}
        protected override void OnEnabledChanged(EventArgs e){base.OnEnabledChanged(e);Invalidate();}
        protected override void WndProc(ref Message message)
        {
            int saved=ThemeInputFrame.SavePrint(message);
            try{base.WndProc(ref message);ThemeInputFrame.Finish(this,Palette,hover,message,ref saved);}
            finally{ThemeInputFrame.RestorePrint(message,saved);}
        }
    }
    public sealed class ThemedNumericUpDown:NumericUpDown
    {
        public AppTheme Palette;bool hover;ThemeSpinChrome spin;
        public ThemedNumericUpDown(){BorderStyle=BorderStyle.FixedSingle;}
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // This wrapper is limited to our own two native spin-button faces.
            // Values, hit testing, repeat-click behavior and accessibility stay native.
            foreach(Control child in Controls)
                if(child.GetType().Name=="UpDownButtons")
                {
                    if(spin==null)spin=new ThemeSpinChrome(this,child);
                    spin.Attach();break;
                }
        }
        protected override void OnMouseEnter(EventArgs e){hover=true;base.OnMouseEnter(e);ThemeInputFrame.PaintWindow(this,Palette,hover);}
        protected override void OnMouseLeave(EventArgs e){hover=false;base.OnMouseLeave(e);ThemeInputFrame.PaintWindow(this,Palette,hover);}
        protected override void OnGotFocus(EventArgs e){base.OnGotFocus(e);ThemeInputFrame.PaintWindow(this,Palette,hover);}
        protected override void OnLostFocus(EventArgs e){base.OnLostFocus(e);ThemeInputFrame.PaintWindow(this,Palette,hover);}
        protected override void OnEnabledChanged(EventArgs e){base.OnEnabledChanged(e);Invalidate();}
        protected override void WndProc(ref Message message)
        {
            int saved=ThemeInputFrame.SavePrint(message);
            try{base.WndProc(ref message);ThemeInputFrame.Finish(this,Palette,hover,message,ref saved);}
            finally{ThemeInputFrame.RestorePrint(message,saved);}
        }
        protected override void Dispose(bool disposing)
        {if(disposing&&spin!=null){spin.Dispose();spin=null;}base.Dispose(disposing);}
    }
    sealed class ThemeSpinChrome:NativeWindow,IDisposable
    {
        readonly ThemedNumericUpDown owner;readonly Control button;bool pressed;int pressedHalf;
        [DllImport("gdi32.dll")] static extern int SaveDC(IntPtr context);
        [DllImport("gdi32.dll")] static extern bool RestoreDC(IntPtr context,int state);
        public ThemeSpinChrome(ThemedNumericUpDown parent,Control child)
        {owner=parent;button=child;button.HandleCreated+=Created;button.HandleDestroyed+=Destroyed;}
        void Created(object sender,EventArgs e){Attach();}
        void Destroyed(object sender,EventArgs e){if(Handle!=IntPtr.Zero)ReleaseHandle();pressed=false;}
        public void Attach(){if(button.IsHandleCreated&&Handle==IntPtr.Zero)AssignHandle(button.Handle);}
        public void Dispose(){button.HandleCreated-=Created;button.HandleDestroyed-=Destroyed;if(Handle!=IntPtr.Zero)ReleaseHandle();}
        protected override void WndProc(ref Message message)
        {
            int kind=message.Msg;bool printing=(kind==0x0317||kind==0x0318)&&message.WParam!=IntPtr.Zero;
            int saved=printing?SaveDC(message.WParam):0;
            if(kind==0x0201){pressed=true;pressedHalf=((int)(long)message.LParam>>16)<button.Height/2?0:1;}
            if(kind==0x0202||kind==0x0215||kind==0x0008)pressed=false;
            try
            {
                base.WndProc(ref message);
                if(printing&&saved!=0){RestoreDC(message.WParam,saved);saved=SaveDC(message.WParam);}
                var palette=owner.Palette;
                if(palette==null||palette.HighContrast||button.IsDisposed||!button.IsHandleCreated)return;
                try
                {
                    if(kind==0x000F)
                        using(var graphics=Graphics.FromHwnd(button.Handle))Paint(graphics,palette);
                    else if(printing&&saved!=0)
                        using(var graphics=Graphics.FromHdc(message.WParam))Paint(graphics,palette);
                }
                catch(ExternalException){}
                catch(ArgumentException){}
                if(kind==0x0201||kind==0x0202||kind==0x0215)button.Invalidate();
            }
            finally{if(saved!=0)RestoreDC(message.WParam,saved);}
        }
        void Paint(Graphics graphics,AppTheme palette)
        {
            var bounds=button.ClientRectangle;if(bounds.Width<2||bounds.Height<2)return;
            int split=bounds.Height/2;
            using(var fill=new SolidBrush(palette.Input))graphics.FillRectangle(fill,bounds);
            if(pressed&&owner.Enabled)
                using(var fill=new SolidBrush(palette.Pressed))graphics.FillRectangle(fill,0,pressedHalf==0?0:split,bounds.Width,pressedHalf==0?split:bounds.Height-split);
            using(var line=new Pen(palette.Border))
            {graphics.DrawLine(line,0,0,0,bounds.Height);graphics.DrawLine(line,0,split,bounds.Width,split);}
            var state=graphics.Save();graphics.SmoothingMode=SmoothingMode.AntiAlias;
            float x=bounds.Width/2f,half=Math.Max(2.5f,2.5f*graphics.DpiX/96f);
            using(var pen=new Pen(owner.Enabled?palette.Text:palette.Muted,Math.Max(1.2f,1.2f*graphics.DpiX/96f)))
            {
                float y=split/2f;
                graphics.DrawLines(pen,new[]{new PointF(x-half,y+half/2),new PointF(x,y-half/2),new PointF(x+half,y+half/2)});
                y=split+(bounds.Height-split)/2f;
                graphics.DrawLines(pen,new[]{new PointF(x-half,y-half/2),new PointF(x,y+half/2),new PointF(x+half,y-half/2)});
            }
            graphics.Restore(state);
        }
    }
    sealed class ThemeMenuColors:ProfessionalColorTable
    {
        readonly AppTheme palette;
        public ThemeMenuColors(AppTheme value){palette=value;UseSystemColors=false;}
        public override Color ToolStripDropDownBackground{get{return palette.Surface;}}
        public override Color MenuBorder{get{return palette.Border;}}
        public override Color MenuItemBorder{get{return palette.ControlBorder;}}
        public override Color MenuItemSelected{get{return palette.Hover;}}
        public override Color MenuItemSelectedGradientBegin{get{return palette.Hover;}}
        public override Color MenuItemSelectedGradientEnd{get{return palette.Hover;}}
        public override Color MenuItemPressedGradientBegin{get{return palette.Pressed;}}
        public override Color MenuItemPressedGradientMiddle{get{return palette.Pressed;}}
        public override Color MenuItemPressedGradientEnd{get{return palette.Pressed;}}
        public override Color ImageMarginGradientBegin{get{return palette.Surface;}}
        public override Color ImageMarginGradientMiddle{get{return palette.Surface;}}
        public override Color ImageMarginGradientEnd{get{return palette.Surface;}}
        public override Color SeparatorDark{get{return palette.Border;}}
        public override Color SeparatorLight{get{return palette.Surface;}}
        public override Color CheckBackground{get{return palette.Selected;}}
        public override Color CheckSelectedBackground{get{return palette.Selected;}}
        public override Color CheckPressedBackground{get{return palette.Pressed;}}
    }
    sealed class ThemeMenuRenderer:ToolStripProfessionalRenderer
    {
        readonly AppTheme palette;
        public ThemeMenuRenderer(AppTheme value):base(new ThemeMenuColors(value)){palette=value;RoundedEdges=false;}
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {e.TextColor=e.Item.Enabled?palette.Text:palette.Muted;base.OnRenderItemText(e);}
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {e.ArrowColor=e.Item.Enabled?palette.Text:palette.Muted;base.OnRenderArrow(e);}
    }
    public sealed class ThemedButton:Button
    {
        public AppTheme Palette;bool hover,pressed;
        public ThemedButton(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);}
        protected override void OnMouseEnter(EventArgs e){hover=true;Invalidate();base.OnMouseEnter(e);}
        protected override void OnMouseLeave(EventArgs e){hover=false;pressed=false;Invalidate();base.OnMouseLeave(e);}
        protected override void OnMouseDown(MouseEventArgs e){if(e.Button==MouseButtons.Left&&Enabled){pressed=true;Invalidate();}base.OnMouseDown(e);}
        protected override void OnMouseUp(MouseEventArgs e){pressed=false;Invalidate();base.OnMouseUp(e);}
        protected override void OnKeyDown(KeyEventArgs e){if(e.KeyCode==Keys.Space||e.KeyCode==Keys.Enter){pressed=true;Invalidate();}base.OnKeyDown(e);}
        protected override void OnKeyUp(KeyEventArgs e){pressed=false;Invalidate();base.OnKeyUp(e);}
        protected override void OnGotFocus(EventArgs e){Invalidate();base.OnGotFocus(e);}
        protected override void OnLostFocus(EventArgs e){pressed=false;Invalidate();base.OnLostFocus(e);}
        protected override void OnChangeUICues(UICuesEventArgs e){Invalidate();base.OnChangeUICues(e);}
        protected override void OnEnabledChanged(EventArgs e){if(!Enabled)pressed=false;Invalidate();base.OnEnabledChanged(e);}
        protected override void OnPaint(PaintEventArgs e)
        {
            if(Palette==null){base.OnPaint(e);return;}
            bool primary=(Tag as string)=="primary",selected=(Tag as string)=="nav_selected",nav=selected||(Tag as string)=="nav";
            Color primaryBase=Palette.PrimaryFill,primaryInk=Palette.PrimaryText;
            Color background=!Enabled?Palette.Input:primary?(Palette.HighContrast?primaryBase:AppTheme.Mix(primaryBase,primaryInk,pressed?.16:hover?.08:0)):pressed?Palette.Pressed:hover?Palette.Hover:selected?Palette.Selected:nav?Palette.Background:Palette.Card;
            Color text=!Enabled?Palette.Muted:primary?Palette.PrimaryText:Palette.HighContrast&&(selected||hover||pressed)?Palette.AccentText:Palette.Text;
            using(var b=new SolidBrush(nav?Palette.Background:Parent!=null?Parent.BackColor:Palette.Surface))e.Graphics.FillRectangle(b,ClientRectangle);
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            using(var outline=Rounded(new RectangleF(.5f,.5f,Math.Max(1,Width-1),Math.Max(1,Height-1)),7f*e.Graphics.DpiX/96f))
            {
                using(var b=new SolidBrush(background))e.Graphics.FillPath(b,outline);
                if(!nav||selected)using(var p=new Pen(!Enabled?Palette.Border:primary?background:nav?Palette.Border:Palette.ControlBorder))e.Graphics.DrawPath(p,outline);
            }
            int indicator=ThemeText.Pixels(this,12);
            if(selected)using(var b=new SolidBrush(Palette.Accent))e.Graphics.FillRectangle(b,ThemeText.Pixels(this,7),(Height-indicator)/2,ThemeText.Pixels(this,3),indicator);
            int left=Padding.Left+ThemeText.Pixels(this,nav?20:12),right=Padding.Right+ThemeText.Pixels(this,12);
            int top=Padding.Top+ThemeText.Pixels(this,2),bottom=Padding.Bottom+ThemeText.Pixels(this,2);
            var textBounds=new Rectangle(left,top,Math.Max(0,Width-left-right),Math.Max(0,Height-top-bottom));
            TextRenderer.DrawText(e.Graphics,Text,Font,textBounds,text,ThemeText.LineFlags|(nav?TextFormatFlags.Left:TextFormatFlags.HorizontalCenter)|TextFormatFlags.VerticalCenter);
            int focus=ThemeText.Pixels(this,4);
            if(Enabled&&Focused&&ShowFocusCues)
            {
                using(var outline=Rounded(new RectangleF(focus,focus,Math.Max(1,Width-focus*2-1),Math.Max(1,Height-focus*2-1)),4f*e.Graphics.DpiX/96f))
                using(var p=new Pen(primary||Palette.HighContrast?text:Palette.Accent,Math.Max(1f,e.Graphics.DpiX/96f)))e.Graphics.DrawPath(p,outline);
            }
        }
        static GraphicsPath Rounded(RectangleF r,float radius)
        {
            float d=Math.Min(radius*2,Math.Min(r.Width,r.Height));var path=new GraphicsPath();
            path.AddArc(r.Left,r.Top,d,d,180,90);path.AddArc(r.Right-d,r.Top,d,d,270,90);
            path.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);path.AddArc(r.Left,r.Bottom-d,d,d,90,90);path.CloseFigure();return path;
        }
    }
    public sealed class ThemedCheckBox:CheckBox
    {
        public AppTheme Palette;bool hover,pressed;
        public ThemedCheckBox(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);}
        public override Size GetPreferredSize(Size proposedSize)
        {
            int glyph=ThemeText.Pixels(this,15),gap=ThemeText.Pixels(this,9),edge=ThemeText.Pixels(this,2);
            int horizontal=Padding.Horizontal+glyph+gap+edge*2,vertical=Padding.Vertical+ThemeText.Pixels(this,8);
            int constrained=proposedSize.Width>0?proposedSize.Width:Int32.MaxValue;
            if(MaximumSize.Width>0)constrained=Math.Min(constrained,MaximumSize.Width);
            int width=constrained<Int32.MaxValue?Math.Max(1,constrained-horizontal):Int32.MaxValue;
            var measured=TextRenderer.MeasureText(Text??String.Empty,Font,new Size(width,Int32.MaxValue),ThemeText.WrapFlags);
            return new Size(measured.Width+horizontal,Math.Max(glyph,measured.Height)+vertical);
        }
        protected override void OnCheckedChanged(EventArgs e){Invalidate();base.OnCheckedChanged(e);}
        protected override void OnMouseEnter(EventArgs e){hover=true;Invalidate();base.OnMouseEnter(e);}
        protected override void OnMouseLeave(EventArgs e){hover=false;pressed=false;Invalidate();base.OnMouseLeave(e);}
        protected override void OnMouseDown(MouseEventArgs e){if(e.Button==MouseButtons.Left&&Enabled){pressed=true;Invalidate();}base.OnMouseDown(e);}
        protected override void OnMouseUp(MouseEventArgs e){pressed=false;Invalidate();base.OnMouseUp(e);}
        protected override void OnKeyDown(KeyEventArgs e){if(e.KeyCode==Keys.Space){pressed=true;Invalidate();}base.OnKeyDown(e);}
        protected override void OnKeyUp(KeyEventArgs e){pressed=false;Invalidate();base.OnKeyUp(e);}
        protected override void OnGotFocus(EventArgs e){Invalidate();base.OnGotFocus(e);}
        protected override void OnLostFocus(EventArgs e){pressed=false;Invalidate();base.OnLostFocus(e);}
        protected override void OnChangeUICues(UICuesEventArgs e){Invalidate();base.OnChangeUICues(e);}
        protected override void OnEnabledChanged(EventArgs e){if(!Enabled)pressed=false;Invalidate();base.OnEnabledChanged(e);}
        protected override void OnPaint(PaintEventArgs e)
        {
            if(Palette==null){base.OnPaint(e);return;}
            using(var b=new SolidBrush(BackColor))e.Graphics.FillRectangle(b,ClientRectangle);
            int size=ThemeText.Pixels(this,15),left=Padding.Left+ThemeText.Pixels(this,2);
            int availableHeight=Math.Max(1,Height-Padding.Vertical),top=Padding.Top+Math.Max(0,(availableHeight-size)/2);
            var box=new Rectangle(left,top,size,size);
            Color fill=Checked&&Enabled?Palette.Accent:Enabled&&pressed?Palette.Pressed:Enabled&&hover?Palette.Hover:Palette.Input,ink=Enabled?Palette.Text:Palette.Muted;
            using(var b=new SolidBrush(fill))e.Graphics.FillRectangle(b,box);
            using(var p=new Pen(Checked&&Enabled?Palette.Accent:Palette.ControlBorder))e.Graphics.DrawRectangle(p,box);
            if(Enabled&&Focused&&ShowFocusCues)
            {
                int edge=ThemeText.Pixels(this,1);var focusBox=Rectangle.Inflate(box,edge,edge);
                using(var p=new Pen(Palette.Accent))e.Graphics.DrawRectangle(p,focusBox);
            }
            if(Checked)using(var p=new Pen(Enabled?Palette.AccentText:Palette.Muted,ThemeText.Pixels(this,2)))
                e.Graphics.DrawLines(p,new[]{new Point(left+ThemeText.Pixels(this,3),top+size/2),new Point(left+size/2-ThemeText.Pixels(this,1),top+size-ThemeText.Pixels(this,4)),new Point(left+size-ThemeText.Pixels(this,3),top+ThemeText.Pixels(this,3))});
            int textLeft=left+size+ThemeText.Pixels(this,9),textWidth=Math.Max(1,Width-textLeft-Padding.Right-ThemeText.Pixels(this,2));
            var measured=TextRenderer.MeasureText(e.Graphics,Text??String.Empty,Font,new Size(textWidth,Int32.MaxValue),ThemeText.WrapFlags);
            int textHeight=Math.Min(availableHeight,measured.Height),textTop=Padding.Top+Math.Max(0,(availableHeight-textHeight)/2);
            var text=new Rectangle(textLeft,textTop,textWidth,textHeight);
            // DT_VCENTER is single-line only; centre a measured multiline block ourselves.
            TextRenderer.DrawText(e.Graphics,Text,Font,text,ink,ThemeText.WrapFlags|TextFormatFlags.Left|TextFormatFlags.Top);
            if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,text,ink,BackColor);
        }
    }
    public sealed class ThemedComboBox:ComboBox
    {
        public AppTheme Palette;
        [StructLayout(LayoutKind.Sequential)] struct NativeRect{public int Left,Top,Right,Bottom;}
        [StructLayout(LayoutKind.Sequential)] struct ComboInfo
        {public int Size;public NativeRect Item,Button;public int ButtonState;public IntPtr ComboWindow,ItemWindow,ListWindow;}
        [DllImport("user32.dll")] static extern bool GetComboBoxInfo(IntPtr window,ref ComboInfo info);
        [DllImport("gdi32.dll")] static extern int SaveDC(IntPtr context);
        [DllImport("gdi32.dll")] static extern bool RestoreDC(IntPtr context,int state);
        public ThemedComboBox(){DrawMode=DrawMode.OwnerDrawFixed;}
        void UpdateItemHeight()
        {
            int value=Math.Max(Font.Height+ThemeText.Pixels(this,8),ThemeText.Pixels(this,24));
            if(ItemHeight!=value)ItemHeight=value;
        }
        protected override void OnFontChanged(EventArgs e){base.OnFontChanged(e);UpdateItemHeight();}
        protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);UpdateItemHeight();}
        protected override void OnEnabledChanged(EventArgs e){base.OnEnabledChanged(e);Invalidate();}
        protected override void OnGotFocus(EventArgs e){base.OnGotFocus(e);Invalidate();}
        protected override void OnLostFocus(EventArgs e){base.OnLostFocus(e);Invalidate();}
        protected override void OnMouseEnter(EventArgs e){base.OnMouseEnter(e);Invalidate();}
        protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);Invalidate();}
        protected override void OnDropDownClosed(EventArgs e){base.OnDropDownClosed(e);Invalidate();}
        protected override void WndProc(ref Message message)
        {
            int kind=message.Msg;
            bool printing=(kind==0x0317||kind==0x0318)&&message.WParam!=IntPtr.Zero;
            IntPtr context=printing?message.WParam:IntPtr.Zero;
            int saved=printing?SaveDC(context):0;
            try
            {
                base.WndProc(ref message);
                if(kind==0x02E3)UpdateItemHeight(); // WM_DPICHANGED_AFTERPARENT
                // WM_PRINT reuses the caller's HDC across controls. Native drawing may
                // leave a local clip/origin behind; restore the incoming state before
                // the overlay, then restore it again before returning to the caller.
                if(printing&&saved!=0)
                {
                    RestoreDC(context,saved);saved=SaveDC(context);
                }
                // Native ComboBox still owns focus, keyboard navigation, accessibility,
                // selection, and the real popup. Only its face is repainted afterward.
                if(Palette==null||Palette.HighContrast||IsDisposed||!IsHandleCreated||Width<5||Height<5)return;
                try
                {
                    if(kind==0x000F)
                    {using(var graphics=Graphics.FromHwnd(Handle))PaintChrome(graphics);}
                    else if(printing&&saved!=0)
                    {using(var graphics=Graphics.FromHdc(context))PaintChrome(graphics);}
                }
                catch(ExternalException){}
                catch(ArgumentException){}
            }
            finally{if(saved!=0)RestoreDC(context,saved);}
        }
        void PaintChrome(Graphics graphics)
        {
            float scale=graphics.DpiX/96f;int inset=Math.Max(2,(int)Math.Ceiling(2*scale));
            var bounds=ClientRectangle;
            using(var fill=new SolidBrush(Palette.Input))
            {
                // Cover the native bevel without covering the owner-drawn item text.
                graphics.FillRectangle(fill,0,0,bounds.Width,inset);
                graphics.FillRectangle(fill,0,bounds.Height-inset,bounds.Width,inset);
                graphics.FillRectangle(fill,0,0,inset,bounds.Height);
                graphics.FillRectangle(fill,bounds.Width-inset,0,inset,bounds.Height);
                if(DropDownStyle!=ComboBoxStyle.Simple)
                {
                    int fallbackWidth=Math.Max(SystemInformation.VerticalScrollBarWidth,(int)(18*scale));
                    Rectangle button=RightToLeft==RightToLeft.Yes?new Rectangle(1,1,fallbackWidth,bounds.Height-2):new Rectangle(bounds.Width-fallbackWidth-1,1,fallbackWidth,bounds.Height-2);
                    var info=new ComboInfo{Size=Marshal.SizeOf(typeof(ComboInfo))};
                    if(GetComboBoxInfo(Handle,ref info))
                    {
                        var actual=Rectangle.FromLTRB(info.Button.Left,info.Button.Top,info.Button.Right,info.Button.Bottom);
                        if(actual.Width>0&&actual.Height>0&&bounds.Contains(actual))button=actual;
                    }
                    button=Rectangle.Intersect(button,bounds);
                    // GetComboBoxInfo excludes part of the native separator/bevel.
                    // Cover that adjacent strip too, while preserving the arrow center.
                    Rectangle cover=button;
                    if(RightToLeft==RightToLeft.Yes)cover.Width+=inset+1;
                    else{cover.X-=inset+1;cover.Width+=inset+1;}
                    graphics.FillRectangle(fill,Rectangle.Intersect(cover,bounds));
                    float centerX=button.Left+button.Width/2f,centerY=button.Top+button.Height/2f;
                    float half=Math.Max(3,3*scale);
                    var state=graphics.Save();graphics.SmoothingMode=SmoothingMode.AntiAlias;
                    using(var pen=new Pen(Enabled?Palette.Text:Palette.Muted,Math.Max(1.3f,1.3f*scale)))
                        graphics.DrawLines(pen,new[]{new PointF(centerX-half,centerY-half/2),new PointF(centerX,centerY+half/2),new PointF(centerX+half,centerY-half/2)});
                    graphics.Restore(state);
                }
            }
            using(var pen=new Pen(Enabled&&Focused?Palette.Accent:Palette.ControlBorder))graphics.DrawRectangle(pen,0,0,bounds.Width-1,bounds.Height-1);
        }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if(Palette==null){base.OnDrawItem(e);return;}
            bool selected=(e.State&DrawItemState.Selected)!=0;
            Color back=selected?Palette.Selected:Palette.Input,ink=!Enabled?Palette.Muted:selected&&Palette.HighContrast?Palette.AccentText:Palette.Text;
            using(var b=new SolidBrush(back))e.Graphics.FillRectangle(b,e.Bounds);
            string text=e.Index>=0&&e.Index<Items.Count?GetItemText(Items[e.Index]):Text;
            int horizontal=ThemeText.Pixels(this,7);
            var textBounds=new Rectangle(e.Bounds.X+horizontal,e.Bounds.Y,Math.Max(0,e.Bounds.Width-horizontal*2),e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics,text,Font,textBounds,ink,ThemeText.LineFlags|TextFormatFlags.Left|TextFormatFlags.VerticalCenter);
            e.DrawFocusRectangle();
        }
    }
    [Flags]
    public enum PanelEdges{None=0,Left=1,Top=2,Right=4,Bottom=8,All=15}
    public sealed class ThemedPanel:Panel
    {
        public AppTheme Palette;public bool Rounded{get;set;}public PanelEdges BorderSides{get;set;}
        public ThemedPanel()
        {
            Tag="card";Padding=new Padding(12);Rounded=true;BorderSides=PanelEdges.All;
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if(Palette==null||!Rounded||BorderSides!=PanelEdges.All||Width<3||Height<3){base.OnPaintBackground(e);return;}
            e.Graphics.Clear(Parent!=null?Parent.BackColor:Palette.Surface);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            using(var path=ThemeDrawing.Rounded(ClientRectangle,9f*e.Graphics.DpiX/96f))
            using(var fill=new SolidBrush(BackColor))e.Graphics.FillPath(fill,path);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);if(Palette==null||Width<3||Height<3)return;
            if(Rounded&&BorderSides==PanelEdges.All)
            {
                e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
                using(var path=ThemeDrawing.Rounded(ClientRectangle,9f*e.Graphics.DpiX/96f))
                using(var pen=new Pen(Palette.Border))e.Graphics.DrawPath(pen,path);
            }
            else ThemeDrawing.Borders(e.Graphics,ClientRectangle,BorderSides,Palette.Border);
        }
    }
    public sealed class ThemedTableLayoutPanel:TableLayoutPanel
    {
        public AppTheme Palette;public PanelEdges BorderSides{get;set;}
        public ThemedTableLayoutPanel(){SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);}
        protected override void OnPaint(PaintEventArgs e)
        {base.OnPaint(e);if(Palette!=null)ThemeDrawing.Borders(e.Graphics,ClientRectangle,BorderSides,Palette.Border);}
    }
    static class ThemeText
    {
        public const TextFormatFlags BaseFlags=TextFormatFlags.NoPadding|TextFormatFlags.NoPrefix|TextFormatFlags.PreserveGraphicsClipping|TextFormatFlags.PreserveGraphicsTranslateTransform;
        public const TextFormatFlags LineFlags=BaseFlags|TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis;
        public const TextFormatFlags WrapFlags=BaseFlags|TextFormatFlags.WordBreak;
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
        static readonly float DefaultDpi=ReadDefaultDpi();
        static float ReadDefaultDpi()
        {
            try{using(var graphics=Graphics.FromHwnd(IntPtr.Zero))return graphics.DpiX;}
            catch(ExternalException){return 96f;}
        }
        public static int Pixels(Control control,int logical)
        {
            float dpi=DefaultDpi;
            if(control.IsHandleCreated)
            {
                try{uint value=GetDpiForWindow(control.Handle);if(value>=96&&value<=768)dpi=value;}
                catch(EntryPointNotFoundException){}
            }
            return Math.Max(1,(int)Math.Round(logical*dpi/96f));
        }
    }
    static class ThemeDrawing
    {
        public static GraphicsPath Rounded(Rectangle bounds,float radius)
        {
            var r=new RectangleF(bounds.Left+.5f,bounds.Top+.5f,Math.Max(1,bounds.Width-1),Math.Max(1,bounds.Height-1));
            float d=Math.Min(radius*2,Math.Min(r.Width,r.Height));var path=new GraphicsPath();
            path.AddArc(r.Left,r.Top,d,d,180,90);path.AddArc(r.Right-d,r.Top,d,d,270,90);
            path.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);path.AddArc(r.Left,r.Bottom-d,d,d,90,90);path.CloseFigure();return path;
        }
        public static void Borders(Graphics graphics,Rectangle bounds,PanelEdges sides,Color color)
        {
            if(bounds.Width<1||bounds.Height<1||sides==PanelEdges.None)return;
            using(var pen=new Pen(color))
            {
                int left=bounds.Left,top=bounds.Top,right=bounds.Right-1,bottom=bounds.Bottom-1;
                if((sides&PanelEdges.Left)!=0)graphics.DrawLine(pen,left,top,left,bottom);
                if((sides&PanelEdges.Top)!=0)graphics.DrawLine(pen,left,top,right,top);
                if((sides&PanelEdges.Right)!=0)graphics.DrawLine(pen,right,top,right,bottom);
                if((sides&PanelEdges.Bottom)!=0)graphics.DrawLine(pen,left,bottom,right,bottom);
            }
        }
    }
    public sealed class AppShell : Panel
    {
        readonly TableLayoutPanel layout = new TableLayoutPanel();
        readonly Panel body = new Panel(), mainContent = new Panel(), settingsContent = new Panel();
        readonly Panel settingsPage = new Panel();
        readonly Button settingsAction = new ThemedButton(), trayAction = new ThemedButton(), closeAction = new ThemedButton();
        readonly ComboBox appearance = new ThemedComboBox();
        bool settingsOpen, mainWasEnabled, layingOut;
        public Panel MainContent { get { return mainContent; } }
        public Panel SettingsContent { get { return settingsContent; } }
        public Button SettingsAction { get { return settingsAction; } }
        public Button TrayAction { get { return trayAction; } }
        public ComboBox Appearance { get { return appearance; } }
        public bool SettingsOpen { get { return settingsOpen; } }
        public event EventHandler SettingsOpenChanged;

        public AppShell()
        {
            Font = AppTheme.Font(10.5f, FontStyle.Regular);
            Dock = DockStyle.Fill; Margin = Padding.Empty; TabStop = false;
            layout.Dock = DockStyle.Fill; layout.Margin = Padding.Empty;
            layout.ColumnCount = 1; layout.RowCount = 2;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);
            var header = new ThemedTableLayoutPanel {
                Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(24, 14, 24, 14),
                ColumnCount = 3, RowCount = 1, BorderSides = PanelEdges.Bottom
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            header.Controls.Add(new Label {
                Text = "LocalLink", Dock = DockStyle.Fill, Margin = Padding.Empty,
                TextAlign = ContentAlignment.MiddleLeft, Font = AppTheme.Font(16, FontStyle.Bold), AutoEllipsis = true
            }, 0, 0);
            ConfigureButton(trayAction, "В трей", "Свернуть LocalLink в трей", 0);
            trayAction.Margin = new Padding(0, 0, 10, 0);
            ConfigureButton(settingsAction, "\uE713", "Открыть настройки", 1);
            settingsAction.Name = "SettingsAction";
            settingsAction.Font = new Font("Segoe MDL2 Assets", 14, FontStyle.Regular, GraphicsUnit.Point);
            settingsAction.Click += delegate { ToggleSettings(); };
            header.Controls.Add(trayAction, 1, 0); header.Controls.Add(settingsAction, 2, 0);
            layout.Controls.Add(header, 0, 0);

            body.Dock = DockStyle.Fill; body.Margin = Padding.Empty; body.TabStop = false;
            layout.Controls.Add(body, 0, 1);
            mainContent.Name = "MainContent"; mainContent.Dock = DockStyle.Fill; mainContent.Margin = Padding.Empty; mainContent.TabStop = false;
            body.Controls.Add(mainContent);
            settingsPage.Visible = false; settingsPage.Margin = Padding.Empty; settingsPage.TabStop = false;
            settingsPage.Name = "SettingsPage"; settingsPage.AccessibleName = "Настройки";
            body.Controls.Add(settingsPage);
            var settingsLayout = new TableLayoutPanel {
                Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 1, RowCount = 3
            };
            settingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            settingsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
            settingsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
            settingsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            settingsPage.Controls.Add(settingsLayout);
            var settingsHeader = new TableLayoutPanel {
                Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(24, 14, 24, 10),
                ColumnCount = 2, RowCount = 1
            };
            settingsHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            settingsHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
            settingsHeader.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            settingsHeader.Controls.Add(new Label {
                Text = "Настройки", Dock = DockStyle.Fill, Margin = Padding.Empty,
                TextAlign = ContentAlignment.MiddleLeft, Font = AppTheme.Font(16, FontStyle.Bold), AutoEllipsis = true
            }, 0, 0);
            ConfigureButton(closeAction, "\uE72B", "Назад к подключению", 0);
            closeAction.Name = "CloseSettings";
            closeAction.Font = new Font("Segoe MDL2 Assets", 14, FontStyle.Regular, GraphicsUnit.Point);
            closeAction.Click += delegate { CloseSettings(); };
            settingsHeader.Controls.Add(closeAction, 1, 0);
            settingsLayout.Controls.Add(settingsHeader, 0, 0);
            var appearancePanel = new TableLayoutPanel {
                Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(24, 0, 24, 12),
                ColumnCount = 1, RowCount = 2
            };
            appearancePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            appearancePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            appearancePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            appearancePanel.Controls.Add(new Label {
                Text = "Оформление", Dock = DockStyle.Fill, Margin = Padding.Empty,
                Font = AppTheme.Font(9.5f, FontStyle.Regular), Tag = "muted", TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);
            appearance.Name = "Appearance"; appearance.DropDownStyle = ComboBoxStyle.DropDownList; appearance.FlatStyle = FlatStyle.Flat;
            appearance.Dock = DockStyle.Top; appearance.Margin = Padding.Empty;
            appearance.Font = AppTheme.Font(10.5f, FontStyle.Regular);
            appearance.Items.AddRange(new object[] { "Как в Windows", "Светлое", "Тёмное" });
            appearance.SelectedIndex = 0; appearance.TabIndex = 0;
            appearance.AccessibleName = "Оформление: как в Windows, светлое или тёмное";
            appearancePanel.Controls.Add(appearance, 0, 1);
            settingsLayout.Controls.Add(appearancePanel, 0, 1);
            settingsContent.Name = "SettingsContent"; settingsContent.Dock = DockStyle.Fill; settingsContent.Margin = Padding.Empty; settingsContent.TabStop = false;
            settingsLayout.Controls.Add(settingsContent, 0, 2);
            body.Layout += delegate { LayoutSettings(); };
        }

        static void ConfigureButton(Button button, string text, string accessibleName, int tabIndex)
        {
            button.Text = text; button.AccessibleName = accessibleName;
            button.Dock = DockStyle.Fill; button.Margin = Padding.Empty; button.FlatStyle = FlatStyle.Flat;
            button.Font = AppTheme.Font(10.5f, FontStyle.Regular); button.Cursor = Cursors.Hand; button.TabIndex = tabIndex;
        }
        void LayoutSettings()
        {
            if (layingOut || IsDisposed) return;
            layingOut = true;
            try
            {
                mainContent.Bounds = body.ClientRectangle;
                settingsPage.Bounds = body.ClientRectangle;
                if (settingsOpen) settingsPage.BringToFront();
            }
            finally { layingOut = false; }
        }
        public void OpenSettings()
        {
            if (settingsOpen || IsDisposed) return;
            mainWasEnabled = mainContent.Enabled;
            settingsOpen = true;
            mainContent.Visible = false; mainContent.Enabled = false;
            LayoutSettings(); settingsPage.Visible = true; settingsPage.BringToFront();
            settingsAction.Text = "\uE72B"; settingsAction.AccessibleName = "Назад";
            closeAction.Select(); closeAction.Focus();
            OnSettingsOpenChanged();
        }
        public void CloseSettings()
        {
            if (!settingsOpen || IsDisposed) return;
            appearance.DroppedDown = false;
            settingsOpen = false; settingsPage.Visible = false;
            mainContent.Enabled = mainWasEnabled; mainContent.Visible = true;
            settingsAction.Text = "\uE713"; settingsAction.AccessibleName = "Открыть настройки";
            if (settingsAction.CanFocus) { settingsAction.Select(); settingsAction.Focus(); }
            OnSettingsOpenChanged();
        }
        public void ToggleSettings() { if (settingsOpen) CloseSettings(); else OpenSettings(); }
        void OnSettingsOpenChanged()
        {
            var handler = SettingsOpenChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }
        bool HandleSettingsKey(Keys keyData)
        {
            if (keyData == (Keys.Alt | Keys.Oemcomma)) { ToggleSettings(); return true; }
            if (!settingsOpen) return false;
            if (keyData == Keys.Escape) { CloseSettings(); return true; }
            Keys modifiers = keyData & Keys.Modifiers;
            if ((keyData & Keys.KeyCode) != Keys.Tab || (modifiers != Keys.None && modifiers != Keys.Shift)) return false;
            appearance.DroppedDown = false;
            bool forward = modifiers != Keys.Shift;
            Control focused = FindFocusedControl(settingsPage);
            if (!settingsPage.SelectNextControl(focused, forward, true, true, true)) closeAction.Focus();
            return true;
        }
        static Control FindFocusedControl(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                if (child.Focused) return child;
                if (child.ContainsFocus) return FindFocusedControl(child) ?? child;
            }
            return null;
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        { return HandleSettingsKey(keyData) || base.ProcessCmdKey(ref msg, keyData); }
        protected override bool ProcessDialogKey(Keys keyData)
        { return HandleSettingsKey(keyData) || base.ProcessDialogKey(keyData); }
    }
}
