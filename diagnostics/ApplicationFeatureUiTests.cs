// Native hidden forms and isolated AppDomain files only. No live network,
// filter start, dependency download, updater installation or real preferences.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using LocalLink;

internal static class ApplicationFeatureUiTests
{
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    static int assertions;
    [STAThread] static int Main(string[] args)
    {
        try
        {
            if(args.Length==1&&args[0]=="--isolated")return Run();
            // Keep the isolated base short enough for the Framework's legacy
            // preset transaction filenames even inside a deep workspace path.
            string fixture=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"f",Guid.NewGuid().ToString("N").Substring(0,12));
            Directory.CreateDirectory(fixture);
            var domain=AppDomain.CreateDomain("application-feature-ui",null,new AppDomainSetup{ApplicationBase=fixture,ConfigurationFile=Assembly.GetExecutingAssembly().Location+".config"});
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
        Preview();
        Console.WriteLine("Application feature UI: "+assertions+" assertions passed. Isolated native forms only; no live network/filter/update/user settings.");
        return 0;
    }
    static void RunForm(bool dark)
    {
        using(var form=new MainForm(false,false,false,null,false))
        {
            form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-32000,-32000);
            form.Show();Application.DoEvents();((System.Windows.Forms.Timer)Value(form,"timer")).Stop();
            Check(!(bool)Value(form,"persistPreferences"),"real preferences disabled");
            Check(!(bool)Value(form,"featureEventsAttached"),"isolated native fixture does not subscribe to system/network events");
            Check(Value(form,"currentNetwork")==null,"fixture does not enumerate live networks");
            Check(!((Engine)Value(form,"engine")).IsRunning,"fixture starts no filter");
            Check(Button(form,"primary").Height>=32,"primary remains usable with hidden network hint");
            var store=(PresetStore)Value(form,"presetStore");
            var binding=(NetworkPresetStore)Value(form,"networkPresetStore");
            string home=store.Save("Дом "+dark,new AppSettings{Strategy="disorder",Appearance=dark?"dark":"light",AutomaticRecovery=false});
            string mobile=store.Save("Телефон "+dark,new AppSettings{Strategy="fake-split",Appearance=dark?"dark":"light",AutomaticRecovery=true});
            Call(form,"RefreshPresets","");Select(form,home);
            CheckUpdatePreferences(form,home,mobile);
            SelectSection(form,"profiles");
            Check(((AppSettings)Call(form,"CaptureSettings")).Strategy=="disorder","native preset loads current method");
            Check(!Box(form,"autoRecovery").Checked,"recovery setting follows saved preset");
            var networkA=NetworkSnapshot.FromNetworkIds(new[]{dark?new Guid("00000000-0000-0000-0000-000000000011"):new Guid("00000000-0000-0000-0000-000000000012")});
            var networkB=NetworkSnapshot.FromNetworkIds(new[]{dark?new Guid("00000000-0000-0000-0000-000000000021"):new Guid("00000000-0000-0000-0000-000000000022")});
            Call(form,"AcceptNetworkObservation",networkA);
            Check(Button(form,"rememberNetwork").Enabled,"saved preset can be associated with known network");
            Check(!Button(form,"applyNetwork").Enabled&&!Button(form,"forgetNetwork").Enabled,"unknown binding offers no apply/forget");
            Call(form,"RememberCurrentNetwork");
            Check(binding.Find(networkA.Key)==home,"remember saves only explicit selected preset association");
            Check(!(bool)((Control)Value(form,"networkHint")).Visible,"matching selected preset does not show redundant hint");
            Select(form,mobile);
            Check(Box(form,"autoRecovery").Checked,"second preset restores its recovery setting");
            Check((string)Value(form,"suggestedNetworkPreset")==home,"network association is independent of current UI selection");
            Check(((Control)Value(form,"networkHint")).Visible,"different stored network preset produces home hint");
            Application.DoEvents();
            Check(Button(form,"primary").Height>=32,"primary remains usable with visible network hint");
            var hint=(Control)Value(form,"networkHint");
            var workflow=Button(form,"primary").Parent.Parent.Parent;
            Check(hint.Bottom<=workflow.Top,"network hint does not overlap the connection card");
            Check(Button(form,"networkHintApply").Enabled,"home hint has explicit apply action");
            Call(form,"ApplyNetworkPreset");
            Check(Chosen(form)==home&&((AppSettings)Call(form,"CaptureSettings")).Strategy=="disorder","explicit network apply loads remembered native preset");
            ((ComboBox)Value(form,"strategy")).SelectedIndex=0;
            Check(!(bool)CallStatic("PresetSettingsEqual",store.Load(home),Call(form,"CaptureSettings")),"network binding detects unsaved method changes instead of binding stale settings silently");
            Call(form,"ApplyNetworkPreset");
            Check(((AppSettings)Call(form,"CaptureSettings")).Strategy=="disorder","applying same selected network preset reloads its saved settings");
            object previousSettings=Value(form,"settings");bool failedSave=false;
            try{Call(form,"SavePreferencesTo",(Action<AppSettings>)delegate{throw new IOException("synthetic locked settings");});}catch(IOException){failedSave=true;}
            Check(failedSave&&Object.ReferenceEquals(previousSettings,Value(form,"settings")),"required update save surfaces write failure and retains previous in-memory settings");
            binding.Remember(networkB.Key,mobile);
            Call(form,"AcceptNetworkObservation",networkB);
            Check(Chosen(form)==home,"network change never silently applies another preset");
            Check((string)Value(form,"suggestedNetworkPreset")==mobile,"new network suggests its own preset");
            Check(((Label)Value(form,"networkHintText")).Text.Contains("Телефон"),"network hint is readable without network identifiers");
            Set(form,"busy",true);Call(form,"RefreshControls");
            foreach(string name in new[]{"rememberNetwork","forgetNetwork","applyNetwork","networkHintApply","checkUpdate","rollbackUpdate","troubleshoot"})
            {
                SelectSection(form,name=="checkUpdate"||name=="rollbackUpdate"?"updates":"profiles");
                Check(!Button(form,name).Enabled,"busy operation disables "+name);
            }
            SelectSection(form,"profiles");
            Call(form,"ApplyNetworkPreset");
            Check(Chosen(form)==home,"busy guard prevents applying network preset");
            Call(form,"RememberCurrentNetwork");
            Check(binding.Find(networkB.Key)==mobile,"busy guard preserves existing network association");
            Set(form,"busy",false);Call(form,"RefreshControls");
            Set(form,"featureDialogOpen",true);Call(form,"RefreshControls");
            SelectSection(form,"updates");
            Check(!Button(form,"troubleshoot").Enabled&&!Button(form,"checkUpdate").Enabled,"diagnostic modal blocks competing feature actions");
            Check(!Box(form,"automaticUpdateCheck").Enabled,"diagnostic modal disables automatic update preference editing");
            SelectSection(form,"basic");
            Check(!Box(form,"autoRecovery").Enabled,"diagnostic modal disables connection recovery editing");
            SelectSection(form,"profiles");
            Call(form,"ApplyNetworkPreset");Check(Chosen(form)==home,"modal guard prevents preset change");
            Set(form,"featureDialogOpen",false);Call(form,"RefreshControls");
            Call(form,"ApplyNetworkPreset");Check(Chosen(form)==mobile,"explicit apply after modal loads new network choice");
            Call(form,"ForgetCurrentNetwork");
            Check(binding.Find(networkB.Key)==""&&store.Load(mobile)!=null,"forget removes binding but preserves saved preset");
            Check(!((Control)Value(form,"networkHint")).Visible&&!Button(form,"forgetNetwork").Enabled,"forget clears obsolete network controls");
            Call(form,"AcceptNetworkObservation",NetworkSnapshot.Unavailable());
            Check(!Button(form,"rememberNetwork").Enabled&&!Button(form,"applyNetwork").Enabled,"unavailable network cannot be guessed/bound");
            var policy=(RecoveryPolicy)Value(form,"recoveryPolicy");
            DateTime now=new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc);
            var original=new ProfileSpec{Strategy="disorder",Domains="example.org",Port=443};
            var clone=(ProfileSpec)CallStatic("CopyProfile",original);original.Strategy="split";original.Domains="different.example.org";
            Check(clone.Strategy=="disorder"&&clone.Domains=="example.org","recovery copy is independent of editable settings");
            SelectSection(form,"basic");Box(form,"automaticConnection").Checked=false;
            SelectSection(form,"advanced");Call(form,"RefreshControls");
            Check(((ComboBox)Value(form,"strategy")).Enabled,"manual strategy is editable before a recovery retry is pending");
            SelectSection(form,"basic");
            policy.UserStarted(now);policy.UnexpectedExit(now);Set(form,"recoverySpec",new ProfileSpec{Strategy="disorder",Domains="example.org"});
            Box(form,"autoRecovery").Checked=false;
            Check(!policy.DesiredRunning&&!policy.Pending&&Value(form,"recoverySpec")==null,"disabling recovery cancels future attempts and retained profile");
            Box(form,"autoRecovery").Checked=true;
            Check(!policy.DesiredRunning,"enabling recovery alone does not arm a stopped filter");
            policy.UserStarted(now);policy.UnexpectedExit(now);Set(form,"recoverySpec",new ProfileSpec{Strategy="disorder",Domains="example.org"});
            Call(form,"RefreshControls");
            SelectSection(form,"profiles");
            Check(!((ComboBox)Value(form,"presetChoice")).Enabled&&!Button(form,"applyNetwork").Enabled,"pending retry cannot change the selected network preset");
            SelectSection(form,"advanced");
            Check(!((ComboBox)Value(form,"strategy")).Enabled,"pending retry cannot diverge editable strategy from immutable recovery profile");
            Check(Button(form,"primary").Text=="Отменить повтор","pending recovery has an explicit cancel action");
            Button(form,"primary").PerformClick();Application.DoEvents();
            Check(!policy.DesiredRunning&&!policy.Pending&&Value(form,"recoverySpec")==null,"manual Stop cancels all scheduled recovery");
            policy.UserStarted(now);policy.UnexpectedExit(now);policy.TryBeginAttempt(now.AddSeconds(5));policy.Suspend();policy.Resume(now.AddSeconds(6));policy.MarkRunning(now.AddSeconds(6));
            Call(form,"CompleteCancelledRecovery");
            Check(policy.Pending&&policy.DesiredRunning,"cancelled recovery continuation after resume keeps retry scheduled");
            Call(form,"StopOperation");Call(form,"CompleteCancelledRecovery");
            Check(!policy.Pending&&!policy.DesiredRunning,"late cancellation continuation respects explicit stop");
            Call(form,"TickApplicationFeatures");
            Check(!((Engine)Value(form,"engine")).IsRunning&&Value(form,"operation")==null,"fixture timer never starts background engine or operation");
            Check(!(bool)Value(form,"automaticUpdateAttempted")&&Value(form,"backgroundUpdate")==null,"isolated native fixture never starts automatic update transport");
            Check(Button(form,"troubleshoot").AccessibleName=="Мастер диагностики проблемы","troubleshooting action has accessible label");
            Check(Button(form,"checkUpdate").Text=="Проверить обновления","update action has clear compact copy");
            Check((string)CallStatic("ApplicationVersion")=="0.9.0","application version used by updater is current build");
            Set(form,"exitRequested",true);form.Close();Application.DoEvents();
            Check(!policy.DesiredRunning,"form disposal disarms recovery");
        }
    }
    static void CheckUpdatePreferences(MainForm form,string home,string mobile)
    {
        SelectSection(form,"updates");
        var checkbox=Box(form,"automaticUpdateCheck");
        var preferences=(UpdatePreferences)Value(form,"updatePreferences");
        var store=(UpdatePreferenceStore)Value(form,"updatePreferenceStore");
        var path=(string)typeof(UpdatePreferenceStore).GetField("path",Private).GetValue(store);
        string fixturePath=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"update-preferences-preview.json");
        Check(Path.GetFullPath(path)==Path.GetFullPath(fixturePath),"update preferences use only the isolated AppDomain file");
        Check(!String.Equals(path,UpdatePreferenceStore.DefaultPath,StringComparison.OrdinalIgnoreCase),"fixture cannot write live update preferences");
        Check(checkbox.Text=="Предлагать новые версии"&&checkbox.Enabled,"automatic update preference has a compact native checkbox");
        Check((DateTime)Value(form,"automaticUpdateAfterUtc")==DateTime.MaxValue,"isolated form does not schedule startup update checks");
        checkbox.Checked=false;
        Check(!preferences.AutoCheck&&!store.Load().AutoCheck,"checkbox persists refusal without touching connection settings");
        DateTime now=new DateTime(2026,10,28,12,0,0,DateTimeKind.Utc);
        preferences.MarkAutomaticCheck(now);
        preferences.ApplyChoice("0.9.1",UpdatePromptChoice.Later,now);
        preferences.IgnoredVersion="0.9.2";
        Check((bool)Call(form,"SaveUpdatePreferences"),"isolated reminder state saves successfully");
        string last=preferences.LastAutomaticCheckUtc,snooze=preferences.SnoozedUntilUtc;
        Select(form,mobile);Select(form,home);
        Check(Object.ReferenceEquals(preferences,Value(form,"updatePreferences")),"connection preset loading retains the independent update preference instance");
        Check(!checkbox.Checked&&!preferences.AutoCheck,"loading a preset cannot unmute automatic update prompts");
        Check(preferences.IgnoredVersion=="0.9.2"&&preferences.LastAutomaticCheckUtc==last&&preferences.SnoozedUntilUtc==snooze,"preset loading preserves ignored version, throttle and reminder deadline");
        var loaded=store.Load();
        Check(!loaded.AutoCheck&&loaded.IgnoredVersion=="0.9.2"&&loaded.LastAutomaticCheckUtc==last&&loaded.SnoozedUntilUtc==snooze,"separate update preference file survives preset changes unchanged");
        Check(!preferences.ShouldPrompt("0.9.2",now)&&preferences.ShouldPrompt("0.9.2",now,true),"manual availability policy can bypass refusal without changing the saved choice");
        checkbox.Checked=true;
        Check(preferences.AutoCheck&&store.Load().AutoCheck,"checkbox can explicitly re-enable automatic offers");
        Check(preferences.IgnoredVersion=="0.9.2"&&preferences.SnoozedUntilUtc==snooze,"re-enabling offers does not silently clear per-version refusal or snooze");
        Check(Value(form,"backgroundUpdate")==null&&!(bool)Value(form,"automaticUpdateAttempted"),"editing automatic update preference never performs a network check itself");
        using(var background=new CancellationTokenSource())
        {
            Set(form,"backgroundUpdate",background);Set(form,"pendingUpdateOffer",new AppUpdateOffer());
            checkbox.Checked=false;
            Check(background.IsCancellationRequested&&Value(form,"pendingUpdateOffer")==null,"turning off offers cancels the owned background request and drops the pending prompt");
            Set(form,"backgroundUpdate",null);
        }
        Check(!preferences.AutoCheck&&!store.Load().AutoCheck,"cancelled background request cannot re-enable the stored preference");
    }
    static void Preview()
    {
        using(var form=new MainForm(true,true,false,null,false))
        {
            var before=Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory,"network-presets-preview.json*",SearchOption.TopDirectoryOnly).Length;
            Call(form,"RefreshControls");
            foreach(string name in new[]{"troubleshoot","checkUpdate","rollbackUpdate","rememberNetwork","applyNetwork","forgetNetwork"})
            {
                SelectSection(form,name=="checkUpdate"||name=="rollbackUpdate"?"updates":"profiles");
                Check(!Button(form,name).Enabled,"preview disables "+name);
            }
            SelectSection(form,"updates");
            Check(!Box(form,"automaticUpdateCheck").Enabled,"preview disables automatic update preference writes");
            Call(form,"RememberCurrentNetwork");Call(form,"ForgetCurrentNetwork");Call(form,"ApplyNetworkPreset");Call(form,"TickApplicationFeatures");
            Check(!((Engine)Value(form,"engine")).IsRunning&&!(bool)Value(form,"featureEventsAttached"),"preview has no filter or global event subscriptions");
            Check(Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory,"network-presets-preview.json*",SearchOption.TopDirectoryOnly).Length==before,"preview does not write bindings");
        }
    }
    static string Chosen(MainForm form){return ((PresetEntry)((ComboBox)Value(form,"presetChoice")).SelectedItem).Id;}
    static void SelectSection(MainForm form,string id)
    {
        Call(form,"SelectSettingsSection",id);
        Check((string)Value(form,"selectedSettingsSection")==id,"feature assertion targets the intended settings section: "+id);
    }
    static void Select(MainForm form,string id)
    {
        var choice=(ComboBox)Value(form,"presetChoice");
        for(int index=0;index<choice.Items.Count;index++)if(((PresetEntry)choice.Items[index]).Id==id){choice.SelectedIndex=index;Application.DoEvents();return;}
        throw new InvalidOperationException("Missing fixture preset");
    }
    static Button Button(MainForm form,string name){return (Button)Value(form,name);}
    static CheckBox Box(MainForm form,string name){return (CheckBox)Value(form,name);}
    static FieldInfo Field(string name){return typeof(MainForm).GetField(name,Private);}
    static object Value(MainForm form,string name){return Field(name).GetValue(form);}
    static void Set(MainForm form,string name,object value){Field(name).SetValue(form,value);}
    static object Call(MainForm form,string name,params object[] args)
    {try{return typeof(MainForm).GetMethod(name,Private).Invoke(form,args);}catch(TargetInvocationException error){throw error.InnerException;}}
    static object CallStatic(string name,params object[] args){return typeof(MainForm).GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,args);}
    static void Check(bool condition,string message){assertions++;if(!condition)throw new Exception(message);}
}
