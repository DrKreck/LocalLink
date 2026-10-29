// Isolated native forms only. No dependency downloads, public probes, UAC,
// filter processes, Telegram launch, live settings or system event hooks.
using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LocalLink;

internal static class AutomaticConnectionUiTests
{
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    static int assertions;
    [STAThread]static int Main(string[] args)
    {
        try
        {
            if(args.Length==1&&args[0]=="--isolated")return Run();
            string fixture=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ac",Guid.NewGuid().ToString("N").Substring(0,10));
            Directory.CreateDirectory(fixture);
            var domain=AppDomain.CreateDomain("automatic-connection-ui",null,new AppDomainSetup{ApplicationBase=fixture,ConfigurationFile=Assembly.GetExecutingAssembly().Location+".config"});
            try{return domain.ExecuteAssembly(Assembly.GetExecutingAssembly().Location,new[]{"--isolated"});}
            finally{AppDomain.Unload(domain);}
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    static int Run()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        foreach(bool dark in new[]{true,false})RunForm(dark);
        LinkedPhaseCancellation();
        ShutdownDoesNotWait();
        Preview();
        Console.WriteLine("Automatic connection UI: "+assertions+" isolated native assertions passed. No network, filter, UAC, Telegram or real preference changes.");
        return 0;
    }
    static void RunForm(bool dark)
    {
        using(var form=new MainForm(false,false,false,null,false))
        using(var lifetime=new CancellationTokenSource())
        {
            try
            {
                form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-32000,-32000);
                form.Show();Application.DoEvents();((System.Windows.Forms.Timer)Value(form,"timer")).Stop();
                Check(!(bool)Value(form,"persistPreferences"),"fixture cannot persist real preferences");
                Check(!(bool)Value(form,"featureEventsAttached"),"fixture subscribes to no live network/system events");
                Check(Value(form,"currentNetwork")==null&&!((Engine)Value(form,"engine")).IsRunning,"fixture enumerates no networks and starts no filter");
                var automatic=Box(form,"automaticConnection");var opening=Box(form,"connectOnOpen");
                Check(automatic.Checked,"automatic method selection is the default connection mode");
                Check(!opening.Checked,"opening/startup connection is explicit opt-in, never migration default");
                var captured=(AppSettings)Call(form,"CaptureSettings");
                Check(captured.AutomaticSetup&&!captured.ConnectOnOpen,"native settings capture includes automatic mode and startup opt-in");
                ((Task)Call(form,"HandleAutomaticOpeningAsync")).GetAwaiter().GetResult();
                Call(form,"TickAutomaticOpening");
                Check(!(bool)Value(form,"automaticStartupHandled")&&!(bool)Value(form,"automaticConnecting")&&Value(form,"automaticLifetime")==null,
                    "isolated opening/timer guards do not start setup, dependency work or elevation");

                automatic.Checked=false;Call(form,"SelectSettingsSection","advanced");Call(form,"RefreshControls");
                Check(((ComboBox)Value(form,"strategy")).Enabled&&((ComboBox)Value(form,"discordStrategy")).Enabled,"manual mode exposes both supported method controls");
                automatic.Checked=true;Call(form,"RefreshControls");
                Check(!((ComboBox)Value(form,"strategy")).Enabled&&!((ComboBox)Value(form,"discordStrategy")).Enabled,"automatic mode prevents competing manual strategy edits");

                var presets=(PresetStore)Value(form,"presetStore");
                string preset=presets.Save("Авто "+dark,new AppSettings{AutomaticSetup=false,ConnectOnOpen=false,Appearance=dark?"dark":"light"});
                opening.Checked=true;
                Call(form,"RefreshPresets",preset);Call(form,"LoadChosenPreset");
                captured=(AppSettings)Call(form,"CaptureSettings");
                Check(opening.Checked&&captured.ConnectOnOpen,"loading a connection preset cannot disable the global startup consent");
                Check(!automatic.Checked&&!captured.AutomaticSetup,"automatic/manual mode follows the chosen connection preset");
                automatic.Checked=true;

                Set(form,"automaticConnecting",true);Set(form,"automaticLifetime",lifetime);Set(form,"busy",false);
                Check(Value(form,"operation")==null,"synthetic automatic gap has no nested operation token");
                Call(form,"RefreshControls");
                Check(Button(form,"primary").Text=="Отменить","automatic gap still presents a working cancel action");
                Call(form,"SelectSettingsSection","advanced");
                Check(!((ComboBox)Value(form,"mode")).Enabled,"automatic gap keeps routing edits locked on their active page");
                Call(form,"SelectSettingsSection","profiles");
                Check(!((ComboBox)Value(form,"presetChoice")).Enabled,"automatic gap keeps preset edits locked on their active page");
                Call(form,"SelectSettingsSection","advanced");
                Check(!Button(form,"tune").Enabled&&!Button(form,"tuneDiscord").Enabled&&!Button(form,"check").Enabled,"automatic gap cannot start a competing check/tune");
                Call(form,"SelectSettingsSection","updates");
                Check(!Button(form,"checkUpdate").Enabled&&!Button(form,"rollbackUpdate").Enabled&&!Button(form,"troubleshoot").Enabled,"automatic gap cannot open a competing feature workflow");
                Call(form,"SelectSettingsSection","basic");
                Check(!automatic.Enabled&&!opening.Enabled,"automatic/startup preferences remain stable until setup finishes");
                Check(((ToolStripMenuItem)Value(form,"trayStop")).Enabled,"tray stop remains available between nested automatic phases");
                Call(form,"End");
                Check((bool)Value(form,"automaticConnecting")&&!Button(form,"check").Enabled,"nested End does not release the outer automatic workflow guard");
                Call(form,"StopOperation");
                Check(lifetime.IsCancellationRequested,"stop cancels the outer automatic lifetime even with operation=null");
                Check(!((Engine)Value(form,"engine")).IsRunning&&!((RecoveryPolicy)Value(form,"recoveryPolicy")).DesiredRunning,"cancel never starts a filter or leaves recovery armed");

                Set(form,"exitRequested",true);
                var closing=new FormClosingEventArgs(CloseReason.UserClosing,false);
                Call(form,"OnClosing",form,closing);
                Check(closing.Cancel&&(bool)Value(form,"exitPending")&&!form.IsDisposed,"explicit exit waits for the outer workflow even during an idle gap");
                Check(!(bool)Value(form,"closing"),"pending exit does not dispose controls before async setup unwinds");
                Set(form,"automaticConnecting",false);Set(form,"automaticLifetime",null);Call(form,"End");
                Check(form.IsDisposed,"final outer completion finishes the already-requested exit");
            }
            finally
            {
                if(!form.IsDisposed)
                {
                    Set(form,"automaticConnecting",false);Set(form,"automaticLifetime",null);Set(form,"busy",false);Set(form,"exitRequested",true);form.Close();
                }
            }
        }
    }
    static void Preview()
    {
        using(var form=new MainForm(true,true))
        {
            form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-32000,-32000);form.Show();Application.DoEvents();
            ((System.Windows.Forms.Timer)Value(form,"timer")).Stop();
            Check(!Box(form,"automaticConnection").Enabled&&!Box(form,"connectOnOpen").Enabled,"preview cannot change automatic startup preferences");
            ((Task)Call(form,"HandleAutomaticOpeningAsync")).GetAwaiter().GetResult();Call(form,"TickAutomaticOpening");
            Check(!(bool)Value(form,"automaticConnecting")&&Value(form,"automaticLifetime")==null,"preview opening never initiates live setup");
            form.Close();
        }
    }
    static void LinkedPhaseCancellation()
    {
        using(var form=new MainForm(false,false,false,null,false))
        using(var lifetime=new CancellationTokenSource())
        {
            form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-32000,-32000);form.Show();Application.DoEvents();
            ((System.Windows.Forms.Timer)Value(form,"timer")).Stop();
            Set(form,"automaticConnecting",true);Set(form,"automaticLifetime",lifetime);
            Call(form,"Begin","Автонастройка");
            var nested=(CancellationTokenSource)Value(form,"operation");var token=nested.Token;
            Check((bool)Value(form,"busy")&&!token.IsCancellationRequested,"owned automatic phase has a live nested operation");
            Call(form,"StopOperation");
            Check(lifetime.IsCancellationRequested&&token.IsCancellationRequested,"one stop cancels both the outer setup and its nested phase");
            Call(form,"End");
            Check(!(bool)Value(form,"busy")&&(bool)Value(form,"automaticConnecting"),"ending a cancelled nested phase retains the outer guard until unwind");
            Set(form,"automaticConnecting",false);Set(form,"automaticLifetime",null);Set(form,"exitRequested",true);form.Close();
            Check(form.IsDisposed,"fully unwound cancelled workflow releases its form");
        }
    }
    static void ShutdownDoesNotWait()
    {
        using(var form=new MainForm(false,false,false,null,false))
        using(var lifetime=new CancellationTokenSource())
        {
            form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-32000,-32000);form.Show();Application.DoEvents();
            ((System.Windows.Forms.Timer)Value(form,"timer")).Stop();
            Set(form,"automaticConnecting",true);Set(form,"automaticLifetime",lifetime);
            var args=new FormClosingEventArgs(CloseReason.WindowsShutDown,false);
            Call(form,"OnClosing",form,args);
            Check(!args.Cancel&&(bool)Value(form,"closing"),"Windows shutdown never waits for an automatic idle gap");
            Check(lifetime.IsCancellationRequested,"Windows shutdown cancels the owned automatic lifetime");
            Set(form,"automaticConnecting",false);Set(form,"automaticLifetime",null);form.Close();
            Check(form.IsDisposed,"shutdown path disposes only the owned fixture resources");
        }
    }
    static object Value(MainForm form,string name){var field=typeof(MainForm).GetField(name,Private);if(field==null)throw new Exception("Missing field "+name);return field.GetValue(form);}
    static void Set(MainForm form,string name,object value){typeof(MainForm).GetField(name,Private).SetValue(form,value);}
    static object Call(MainForm form,string name,params object[] args)
    {
        try{return typeof(MainForm).GetMethod(name,Private|BindingFlags.DeclaredOnly).Invoke(form,args);}
        catch(TargetInvocationException error){throw error.InnerException??error;}
    }
    static Button Button(MainForm form,string name){return (Button)Value(form,name);}
    static CheckBox Box(MainForm form,string name){return (CheckBox)Value(form,name);}
    static void Check(bool condition,string message){if(!condition)throw new Exception("FAIL: "+message);assertions++;}
}
