// Isolated native controls and hash-bound synthetic files only. This never
// executes Program.Main, an updater, a filter, UAC, Telegram or network probes.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using LocalLink;

internal static class UpdateStartupUiTests
{
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    const BindingFlags StaticPrivate=BindingFlags.Static|BindingFlags.NonPublic;
    static int assertions;
    [STAThread]static int Main(string[] args)
    {
        try
        {
            if(args.Length==1&&args[0]=="--isolated")return Run();
            string fixture=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"us",Guid.NewGuid().ToString("N").Substring(0,10));
            Directory.CreateDirectory(fixture);
            var domain=AppDomain.CreateDomain("update-startup-ui",null,new AppDomainSetup{
                ApplicationBase=fixture,ConfigurationFile=Assembly.GetExecutingAssembly().Location+".config"});
            try{return domain.ExecuteAssembly(Assembly.GetExecutingAssembly().Location,new[]{"--isolated"});}
            finally{AppDomain.Unload(domain);}
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    static int Run()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        foreach(bool dark in new[]{true,false})GateBlocksUntilReady(dark);
        Console.WriteLine("Update startup UI: "+assertions+" isolated native assertions passed. Synthetic files only; no updater, UAC, filter, Telegram, network or live preferences.");
        return 0;
    }
    static void GateBlocksUntilReady(bool dark)
    {
        var fixture=new StartupFixture();
        FileStream helper=null;
        using(var form=new MainForm(false,dark,!dark,null,false))
        {
            try
            {
                form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-32000,-32000);
                ((System.Windows.Forms.Timer)Value(form,"timer")).Stop();
                Box(form,"connectOnOpen").Checked=true;
                Box(form,"automaticConnection").Checked=false;
                helper=new FileStream(fixture.HelperLock,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None);
                object gate=fixture.CreateGate();Set(form,"updateStartupGate",gate);Call(form,"RefreshControls");
                form.Show();Application.DoEvents();((System.Windows.Forms.Timer)Value(form,"timer")).Stop();
                Check(!(bool)Value(form,"persistPreferences")&&!(bool)Value(form,"featureEventsAttached"),"fixture cannot save real preferences or observe real system events");
                Check(Value(form,"currentNetwork")==null&&!((Engine)Value(form,"engine")).IsRunning,"no network discovery or filter starts while preparing the fixture");
                Check(!Button(form,"primary").Enabled&&Button(form,"primary").Text=="Завершение…","main action is disabled until the updater finishes its owned transaction");
                Check(((Label)Value(form,"status")).Text=="Завершение обновления","the pending updater has a distinct visible state");
                Check(!(bool)Value(form,"automaticStartupHandled"),"startup consent is not consumed before the update gate opens");

                var manual=(Task)Call(form,"StartAsync");
                Check(manual.IsCompleted,"manual start returns before asynchronous component or domain preparation while gated");
                manual.GetAwaiter().GetResult();
                ((Task)Call(form,"HandleAutomaticOpeningAsync")).GetAwaiter().GetResult();
                Check(!(bool)Value(form,"automaticStartupHandled")&&Value(form,"operation")==null&&Value(form,"automaticLifetime")==null,
                    "manual and automatic starts leave the gate, startup consent and operation state untouched");
                Check(!Directory.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"lists")),"gated manual start creates no list-preparation artifacts");
                Button(form,"primary").PerformClick();Application.DoEvents();
                Check(Value(form,"operation")==null&&!((Engine)Value(form,"engine")).IsRunning,"a disabled main action does not dispatch a hidden connection request");

                var shell=(AppShell)Value(form,"shell");shell.OpenSettings();
                Call(form,"SelectSettingsSection","advanced");Application.DoEvents();
                Check(Button(form,"elevate").Visible&&!Button(form,"elevate").Enabled,"elevation cannot be requested through its actual visible settings action during updater startup");
                Button(form,"elevate").PerformClick();Application.DoEvents();
                Check(!(bool)Value(form,"exitRequested")&&!form.IsDisposed,"disabled elevation action does not hand off or exit the application");
                // persist=false is an independent safety guard for this direct
                // method call, even if the visible gate guard later regresses.
                Call(form,"Elevate",true);
                Check(!(bool)Value(form,"exitRequested")&&Value(form,"operation")==null,"isolated direct elevation request returns without an external process or handoff");
                Call(form,"SelectSettingsSection","updates");Application.DoEvents();
                Check(!Button(form,"checkUpdate").Enabled&&!Button(form,"rollbackUpdate").Enabled&&!Box(form,"automaticUpdateCheck").Enabled,
                    "a second update, rollback or automatic-check preference cannot compete with the startup transaction");
                Call(form,"SelectSettingsSection","basic");Application.DoEvents();
                Check(!Box(form,"automaticConnection").Enabled&&!Box(form,"connectOnOpen").Enabled&&!Box(form,"autoRecovery").Enabled,
                    "automatic connection, opening and recovery preferences remain stable while waiting");
                shell.CloseSettings();Application.DoEvents();

                Call(form,"TickAutomaticOpening");
                Check(Object.ReferenceEquals(Value(form,"updateStartupGate"),gate),"missing health and installed markers keep the gate closed");
                Write(fixture.Healthy,new string('b',32));Write(fixture.Installed,"installed");
                Call(form,"TickAutomaticOpening");
                Check(Object.ReferenceEquals(Value(form,"updateStartupGate"),gate),"another transaction's health token cannot open the gate");
                Write(fixture.Healthy,fixture.Token);
                Call(form,"TickAutomaticOpening");
                Check(Object.ReferenceEquals(Value(form,"updateStartupGate"),gate)&&!Button(form,"primary").Enabled,
                    "valid markers cannot release the UI while the helper still owns its lock");
                Check(!(bool)Value(form,"automaticStartupHandled")&&Value(form,"operation")==null,"polling the locked helper starts no connection or setup work");

                helper.Dispose();helper=null;
                // Test each predicate independently after the helper is no longer
                // locked; a lock failure must not hide marker-validation defects.
                Write(fixture.Healthy,new string('b',32));Call(form,"TickAutomaticOpening");
                Check(Object.ReferenceEquals(Value(form,"updateStartupGate"),gate),"a released helper cannot legitimize a wrong health token");
                Write(fixture.Healthy,fixture.Token);File.Delete(fixture.Installed);Call(form,"TickAutomaticOpening");
                Check(Object.ReferenceEquals(Value(form,"updateStartupGate"),gate),"health alone is insufficient without installed status");
                Write(fixture.Installed,"installed");Call(form,"TickAutomaticOpening");Application.DoEvents();
                Check(Value(form,"updateStartupGate")==null,"the exact healthy installed transaction resumes after helper lock release");
                Check(Button(form,"primary").Enabled,"the normal main action returns after a successful gate release");
                Check(!(bool)Value(form,"automaticStartupHandled")&&!((Engine)Value(form,"engine")).IsRunning&&Value(form,"operation")==null,
                    "persist=false remains an independent no-network guard even after the updater gate releases");
                Check(Value(form,"automaticLifetime")==null&&Value(form,"currentNetwork")==null,"gate release launches neither an automatic lifetime nor network observation in an isolated fixture");
                Check(!Directory.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"lists")),"gate completion creates no connection-preparation artifacts");
            }
            finally
            {
                if(helper!=null)helper.Dispose();
                if(!form.IsDisposed){Set(form,"updateStartupGate",null);Set(form,"exitRequested",true);form.Close();Application.DoEvents();}
            }
        }
    }

    sealed class StartupFixture
    {
        internal readonly string Root,DirectoryPath,Target,PlanPath,PlanHash,Token,HelperLock,Healthy,Installed;
        readonly object plan;
        readonly Assembly application=typeof(MainForm).Assembly;
        internal StartupFixture()
        {
            Root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"g",Guid.NewGuid().ToString("N").Substring(0,10));
            string updates=Path.Combine(Root,"updates");DirectoryPath=Path.Combine(updates,Guid.NewGuid().ToString("N"));
            Target=Path.Combine(Root,"installed");Directory.CreateDirectory(DirectoryPath);Directory.CreateDirectory(Target);
            Token=Guid.NewGuid().ToString("N");HelperLock=Path.Combine(DirectoryPath,"helper.lock");Healthy=Path.Combine(DirectoryPath,"healthy");Installed=Path.Combine(DirectoryPath,"status.installed");
            byte[] bytes=Encoding.UTF8.GetBytes("LocalLink synthetic startup guard application");
            File.WriteAllBytes(Path.Combine(Target,"LocalLink.exe"),bytes);string hash=Hash(bytes);
            Type planType=application.GetType("LocalLink.AppUpdatePlan",true);
            Type stateType=application.GetType("LocalLink.AppUpdateFileState",true);
            plan=Activator.CreateInstance(planType,true);Array states=Array.CreateInstance(stateType,4);
            string[] names={"LocalLink.exe","LocalLink.exe.config","LICENSE","THIRD-PARTY.md"};
            for(int index=0;index<names.Length;index++)
            {
                object state=Activator.CreateInstance(stateType,true);
                Property(state,"Name",names[index]);Property(state,"Exists",index==0);Property(state,"Bytes",index==0?(long)bytes.Length:0L);
                Property(state,"Sha256",index==0?hash:"");states.SetValue(state,index);
            }
            Property(plan,"Schema",1);Property(plan,"Directory",DirectoryPath);Property(plan,"Target",Target);
            Property(plan,"Version","0.9.0");Property(plan,"PreviousVersion","0.8.5");Property(plan,"ParentSha256",hash);
            Property(plan,"ParentId",Process.GetCurrentProcess().Id);Property(plan,"ParentStartedUtcTicks",DateTime.UtcNow.AddSeconds(-1).Ticks);
            Property(plan,"HealthToken",Token);Property(plan,"Rollback",false);Property(plan,"Incoming",states);Property(plan,"Previous",states);
            Type files=application.GetType("LocalLink.AppUpdateFiles",true);
            byte[] json=(byte[])files.GetMethod("Json",StaticPrivate).Invoke(null,new[]{plan});
            PlanPath=Path.Combine(DirectoryPath,"plan.json");File.WriteAllBytes(PlanPath,json);PlanHash=Hash(json);
        }
        internal object CreateGate()
        {
            Type gateType=application.GetType("LocalLink.AppUpdateStartupGate",true);
            var constructor=gateType.GetConstructor(Private,null,new[]{plan.GetType(),typeof(string),typeof(string),typeof(string)},null);
            if(constructor==null)throw new Exception("Missing isolated startup-gate constructor");
            return constructor.Invoke(new[]{plan,PlanPath,PlanHash,Path.GetDirectoryName(DirectoryPath)});
        }
    }
    static void Write(string file,string text){File.WriteAllText(file,text,new UTF8Encoding(false));}
    static string Hash(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
    static void Property(object target,string name,object value){target.GetType().GetProperty(name).SetValue(target,value,null);}
    static object Value(MainForm form,string name){var field=typeof(MainForm).GetField(name,Private);if(field==null)throw new Exception("Missing field "+name);return field.GetValue(form);}
    static void Set(MainForm form,string name,object value){typeof(MainForm).GetField(name,Private).SetValue(form,value);}
    static object Call(MainForm form,string name,params object[] args)
    {
        try{return typeof(MainForm).GetMethod(name,Private).Invoke(form,args);}
        catch(TargetInvocationException error){throw error.InnerException??error;}
    }
    static Button Button(MainForm form,string name){return (Button)Value(form,name);}
    static CheckBox Box(MainForm form,string name){return (CheckBox)Value(form,name);}
    static void Check(bool value,string message){if(!value)throw new Exception("FAIL: "+message);assertions++;}
}
