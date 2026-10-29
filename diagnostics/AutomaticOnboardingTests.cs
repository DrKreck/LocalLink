// Owned native confirmation forms and isolated preference files only.
// No downloader, installed-app detection, proxy handoff, network or UAC.
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using LocalLink;

internal static class AutomaticOnboardingTests
{
    const BindingFlags PrivateStatic=BindingFlags.NonPublic|BindingFlags.Static;
    static int checks;
    [STAThread]static int Main()
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            foreach(string appearance in new[]{"dark","light"})
                foreach(bool retry in new[]{false,true})
                    foreach(float scale in new[]{1f,1.25f,1.5f,2f})CheckPrompt(appearance,retry,scale);
            CheckPersistence();
            Console.WriteLine("Automatic onboarding: "+checks+" assertions passed. Owned dialogs and isolated files only; no downloads, network, apps, proxy or UAC.");
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    static void CheckPrompt(string appearance,bool retry,float scale)
    {
        var theme=AppTheme.Read(appearance);
        using(var prompt=new SetupPrompt(theme,retry,true))
        {
            Position(prompt,scale);prompt.Show();Application.DoEvents();
            var option=prompt.Controls.OfType<CheckBox>().Single();
            Check(prompt.AutomaticStart&&option.Checked,"automatic offer starts with an explicit visible option");
            Check(option.Visible&&option.Enabled&&option.AccessibleName==option.Text,"automatic choice is accessible, visible and editable");
            foreach(Control control in prompt.Controls)
            {
                Check(control.Left>=0&&control.Top>=0&&control.Right<=prompt.ClientSize.Width+1&&control.Bottom<=prompt.ClientSize.Height+1,"automatic dialog controls fit scaled client");
                if(control is Label||control is CheckBox)
                    foreach(Control other in prompt.Controls)
                        if(!Object.ReferenceEquals(control,other))Check(!control.Bounds.IntersectsWith(other.Bounds),"automatic dialog copy does not overlap other controls");
            }
            option.Checked=false;Check(!prompt.AutomaticStart,"unchecked startup choice is respected");
            prompt.Close();
        }
        foreach(bool selected in new[]{false,true})
            using(var prompt=new SetupPrompt(theme,retry,true))
            {
                Position(prompt,scale);prompt.Controls.OfType<CheckBox>().Single().Checked=selected;
                prompt.Shown+=delegate{prompt.BeginInvoke((MethodInvoker)delegate{prompt.DialogResult=DialogResult.Yes;});};
                Check(prompt.ShowDialog()==DialogResult.Yes,"only explicit Yes accepts component setup");
                Check(!prompt.Visible&&prompt.AutomaticStart==selected,"startup choice survives hiding a completed modal dialog");
            }
        using(var prompt=new SetupPrompt(theme,retry))
        {
            Position(prompt,scale);Check(!prompt.AutomaticStart,"legacy/manual prompt does not imply automatic startup permission");
        }
    }
    static void Position(Form form,float scale)
    {form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-32000,-32000);form.ShowInTaskbar=false;if(scale!=1f)form.Scale(new SizeF(scale,scale));}
    static void CheckPersistence()
    {
        string folder=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ob",Guid.NewGuid().ToString("N").Substring(0,8));Directory.CreateDirectory(folder);
        string path=Path.Combine(folder,"a.json");
        var settings=new AppSettings{AutomaticSetup=false,AutomaticRecovery=false,ConnectOnOpen=true};
        var save=typeof(AppSettings).GetMethod("SaveToPath",BindingFlags.Instance|BindingFlags.NonPublic);
        var load=typeof(AppSettings).GetMethod("LoadFromPath",PrivateStatic);
        save.Invoke(settings,new object[]{path,null});
        var restored=(AppSettings)load.Invoke(null,new object[]{path,null});
        Check(!restored.AutomaticSetup&&!restored.AutomaticRecovery&&restored.ConnectOnOpen,"all explicit automatic choices survive native settings round trip");
        string old=Path.Combine(folder,"old.json");File.WriteAllText(old,"{\"Strategy\":\"disorder\"}");
        restored=(AppSettings)load.Invoke(null,new object[]{old,null});
        Check(restored.AutomaticSetup&&!restored.ConnectOnOpen,"legacy settings migrate to automatic method selection without enabling startup connection");
    }
    static void Check(bool passed,string name){if(!passed)throw new InvalidOperationException("FAIL: "+name);checks++;}
}
