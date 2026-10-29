using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using LocalLink;

internal static class PresetExchangeUiTests
{
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    static int assertions;
    [STAThread] static int Main(string[] args)
    {
        try
        {
            if(args.Length==1&&args[0]=="--isolated")return Run();
            string fixture=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"pxu",Guid.NewGuid().ToString("N").Substring(0,12));
            Directory.CreateDirectory(fixture);
            var domain=AppDomain.CreateDomain("preset-exchange-ui",null,new AppDomainSetup{ApplicationBase=fixture,ConfigurationFile=Assembly.GetExecutingAssembly().Location+".config"});
            try{return domain.ExecuteAssembly(Assembly.GetExecutingAssembly().Location,new[]{"--isolated"});}
            finally{AppDomain.Unload(domain);}
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    static int Run()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        using(var form=new MainForm(false,false,false,null,false))
        {
            form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-32000,-32000);
            form.Show();Application.DoEvents();((System.Windows.Forms.Timer)Value(form,"timer")).Stop();
            Call(form,"SelectSettingsSection","profiles");
            Check(!(bool)Value(form,"persistPreferences"),"native fixture never saves real preferences");
            var engine=(Engine)Value(form,"engine");Check(!engine.IsRunning,"native fixture starts no filter");
            var working=(WorkingSettingsStore)Value(form,"workingSettingsStore");
            string path=(string)typeof(WorkingSettingsStore).GetField("path",Private).GetValue(working);
            Check(path.StartsWith(AppDomain.CurrentDomain.BaseDirectory,StringComparison.OrdinalIgnoreCase)&&!path.StartsWith(WorkingSettingsStore.DefaultFolder,StringComparison.OrdinalIgnoreCase),"working snapshots stay inside isolated AppDomain");
            Check(!Button(form,"restoreWorkingPreset").Enabled,"empty snapshot cannot be restored");
            foreach(string name in new[]{"importPreset","exportPreset","rememberWorkingPreset"})Check(Button(form,name).Enabled,"idle action enabled: "+name);
            Check(Button(form,"importPreset").Text=="Импортировать"&&Button(form,"exportPreset").Text=="Экспортировать","file actions have short readable labels");
            var snapshot=(AppSettings)Call(form,"CaptureSettings");snapshot.Strategy="disorder";snapshot.AutomaticSetup=false;
            Call(form,"RememberWorkingSettings",snapshot);
            Check(working.Load().Strategy=="disorder"&&!(bool)Value(form,"automaticConnecting"),"explicit working snapshot saves without starting automatic setup");
            Check(Button(form,"restoreWorkingPreset").Enabled,"saved working snapshot enables explicit restoration");
            var presets=(PresetStore)Value(form,"presetStore");int before=presets.List().Count;
            foreach(string guard in new[]{"busy","featureDialogOpen","updatingApplication","automaticConnecting"})
            {
                Set(form,guard,true);Call(form,"RefreshControls");
                foreach(string name in new[]{"importPreset","exportPreset","rememberWorkingPreset","restoreWorkingPreset"})Check(!Button(form,name).Enabled,"competing "+guard+" disables "+name);
                Call(form,"RestoreWorkingSettings");Check(presets.List().Count==before,"competing "+guard+" blocks programmatic restore");
                Set(form,guard,false);
            }
            Call(form,"RefreshControls");
            ((ComboBox)Value(form,"strategy")).SelectedIndex=0;
            string previous=((AppSettings)Call(form,"CaptureSettings")).Strategy;
            Call(form,"RestoreWorkingSettings");
            Check(((AppSettings)Call(form,"CaptureSettings")).Strategy=="disorder","explicit restoration loads exact saved working method");
            Check(presets.List().Any(x=>x.Name=="До восстановления")&&presets.Load(presets.List().First(x=>x.Name=="До восстановления").Id).Strategy==previous,"restoration keeps previous user choices in a separate preset");
            Check(presets.List().Any(x=>x.Name=="Рабочая конфигурация"),"restored settings become an ordinary visible preset");
            Check(!engine.IsRunning&&Value(form,"operation")==null,"restoration neither starts a filter nor an operation");
            Call(form,"SelectSettingsSection","updates");Call(form,"RefreshControls");
            Check(Button(form,"checkUpdate").Enabled,"idle update is enabled on its own active page before modal guard");
            Call(form,"SelectSettingsSection","profiles");
            Call(form,"BeginPresetFileDialog");Check((bool)Value(form,"featureDialogOpen"),"file dialog owns the modal feature guard");
            Call(form,"SelectSettingsSection","updates");
            Check(!Button(form,"checkUpdate").Enabled&&!Button(form,"troubleshoot").Enabled,"file dialog blocks competing update and troubleshooting modals");
            Call(form,"EndPresetFileDialog");Check(!(bool)Value(form,"featureDialogOpen"),"file dialog releases modal guard");
            Set(form,"exitRequested",true);form.Close();
        }
        using(var preview=new MainForm(true))
        {
            Call(preview,"SelectSettingsSection","profiles");
            foreach(string name in new[]{"importPreset","exportPreset","rememberWorkingPreset","restoreWorkingPreset"})Check(!Button(preview,name).Enabled,"render preview disables "+name);
            var engine=(Engine)Value(preview,"engine");Call(preview,"RememberWorkingSettings",new AppSettings());Check(!engine.IsRunning,"preview cannot save or start via remembered settings");
        }
        Console.WriteLine("Preset exchange UI: "+assertions+" native assertions passed. Isolated forms only; no file dialogs, real settings, filter or network.");return 0;
    }
    static Button Button(MainForm form,string name){return (Button)Value(form,name);}
    static object Value(MainForm form,string name){return typeof(MainForm).GetField(name,Private).GetValue(form);}
    static void Set(MainForm form,string name,object value){typeof(MainForm).GetField(name,Private).SetValue(form,value);}
    static object Call(MainForm form,string name,params object[] args)
    {try{return typeof(MainForm).GetMethod(name,Private).Invoke(form,args);}catch(TargetInvocationException error){if(error.InnerException!=null)throw error.InnerException;throw;}}
    static void Check(bool passed,string name){if(!passed)throw new InvalidOperationException("FAIL: "+name);assertions++;}
}
