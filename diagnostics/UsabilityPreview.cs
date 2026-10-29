// Synthetic previews and an isolated report-acceptance window; never user preferences or a real filter.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using LocalLink;

public static class UsabilityPreview
{
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    [STAThread] public static int Main(string[] args)
    {
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        if(args.Length==1 && args[0]=="--interactive-report")
        {
            string fixture=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"bug-report-ui-fixtures",Guid.NewGuid().ToString("N"));
            using(var history=new DiagnosticHistory(fixture,true))
            using(var form=new MainForm(false,true,false,null,false,false,history))
            {
                // Only this synthetic window: closing ends the fixture instead of hiding it.
                typeof(MainForm).GetField("exitRequested",Private).SetValue(form,true);
                form.Text="LocalLink — проверка баг-репорта";Application.Run(form);
            }
            return 0;
        }
        if(args.Length==1 && (args[0]=="--interactive-dark" || args[0]=="--interactive-light"))
        {
            using(var form=new MainForm(true,args[0]=="--interactive-dark",args[0]=="--interactive-light",null,false))
            {form.Text="LocalLink — проверка интерфейса";Application.Run(form);}
            return 0;
        }
        if(args.Length!=1)return 2;
        string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);
        foreach(string view in new[]{"home-dark","home-light","settings-dark","settings-light","advanced-dark","checks-dark","vpn-light","small-dark"})
        using(var form=new MainForm(true,view.EndsWith("dark"),view.EndsWith("light"),null,false))
        {
            form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-32000,-32000);
            if(view.StartsWith("small"))form.Size=form.MinimumSize;
            form.Show();((Timer)typeof(MainForm).GetField("timer",Private).GetValue(form)).Stop();
            var shell=(AppShell)typeof(MainForm).GetField("shell",Private).GetValue(form);
            if(view.StartsWith("settings")||view.StartsWith("advanced")||view.StartsWith("vpn"))shell.OpenSettings();
            if(view.StartsWith("advanced"))typeof(MainForm).GetMethod("SetAdvancedSettings",Private).Invoke(form,new object[]{true});
            if(view.StartsWith("vpn"))((ComboBox)typeof(MainForm).GetField("mode",Private).GetValue(form)).SelectedIndex=1;
            if(view.StartsWith("checks"))form.PreviewChecks();
            form.PerformLayout();Application.DoEvents();
            using(var bitmap=new Bitmap(form.ClientSize.Width,form.ClientSize.Height))
            {
                form.Controls[0].DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.ClientSize));
                // Render the existing settings page at its actual client rectangle.
                if(shell.SettingsOpen)
                    foreach(Control page in shell.Controls.Find("SettingsPage",true))
                        if(page.Visible)page.DrawToBitmap(bitmap,new Rectangle(form.PointToClient(page.PointToScreen(Point.Empty)),page.Size));
                bitmap.Save(Path.Combine(output,view+".png"));
            }
            form.Close();Application.DoEvents();
        }
        RenderSettingsSections(output);
        Console.WriteLine("88 own preview images: main views plus all five settings sections, 980/820 windows, dark/light, geometric 100–200% scales. No filter, network or preference writes.");return 0;
    }
    static void RenderSettingsSections(string output)
    {
        foreach(bool dark in new[]{true,false})foreach(bool minimum in new[]{false,true})foreach(float scale in new[]{1f,1.25f,1.5f,2f})
        {
            var fonts=new List<Font>();
            try
            {
                using(var form=new MainForm(true,dark,!dark,null,false))
                {
                    form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-32000,-32000);
                    if(minimum)form.Size=form.MinimumSize;
                    form.Show();((Timer)typeof(MainForm).GetField("timer",Private).GetValue(form)).Stop();
                    form.AutoScaleMode=AutoScaleMode.None;
                    var controls=Flatten(form);var originals=new Dictionary<Control,Font>();
                    foreach(var control in controls)originals.Add(control,control.Font);
                    form.SuspendLayout();form.Scale(new SizeF(scale,scale));
                    foreach(var control in controls)
                    {
                        var original=originals[control];var font=new Font(original.FontFamily,original.SizeInPoints*scale,original.Style,GraphicsUnit.Point);
                        fonts.Add(font);control.Font=font;
                    }
                    form.ResumeLayout(true);
                    var shell=(AppShell)typeof(MainForm).GetField("shell",Private).GetValue(form);shell.OpenSettings();
                    foreach(string section in new[]{"basic","applications","profiles","updates","advanced"})
                    {
                        typeof(MainForm).GetMethod("SelectSettingsSection",Private).Invoke(form,new object[]{section});form.PerformLayout();Application.DoEvents();
                        using(var bitmap=new Bitmap(form.ClientSize.Width,form.ClientSize.Height))
                        {
                            form.Controls[0].DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.ClientSize));
                            foreach(Control page in shell.Controls.Find("SettingsPage",true))
                                if(page.Visible)page.DrawToBitmap(bitmap,new Rectangle(form.PointToClient(page.PointToScreen(Point.Empty)),page.Size));
                            string name="settings-"+section+"-"+(dark?"dark":"light")+"-"+(minimum?"820":"980")+"-"+(int)(scale*100)+".png";
                            bitmap.Save(Path.Combine(output,name));
                        }
                    }
                    form.Close();Application.DoEvents();
                }
            }
            finally{foreach(var font in fonts)font.Dispose();}
        }
    }
    static List<Control> Flatten(Control root)
    {
        var result=new List<Control>{root};foreach(Control child in root.Controls)result.AddRange(Flatten(child));return result;
    }
}
