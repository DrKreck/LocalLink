using System;
using System.IO;
using System.Linq;
using System.Text;
using LocalLink;

internal static class UpdatePreferenceTests
{
    static int checks;
    static readonly DateTime Now=new DateTime(2026,10,28,10,0,0,DateTimeKind.Utc);
    static int Main()
    {
        string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"pref-"+Guid.NewGuid().ToString("N").Substring(0,12));
        try
        {
            Policy();Directory.CreateDirectory(root);Storage(root);
            Console.WriteLine("Update preferences: "+checks+" offline assertions passed. No network, real preferences or updater launch.");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);Console.Error.WriteLine("Fixture retained: "+root);return 1;}
    }
    static void Policy()
    {
        var value=new UpdatePreferences();
        Check(value.AutoCheck&&value.ShouldCheck(Now),"fresh preferences permit an automatic check");
        Check(value.ShouldPrompt("0.9.1",Now),"fresh verified offer can be prompted");
        value.MarkAutomaticCheck(Now);
        Check(!value.ShouldCheck(Now)&&!value.ShouldCheck(Now.AddHours(23.99)),"automatic checks are throttled for 24 hours");
        Check(value.ShouldCheck(Now.AddHours(24)),"automatic check becomes due after 24 hours");
        Check(!value.ShouldCheck(Now.AddHours(-5)),"backward clock cannot cause repeated checks");
        value.ApplyChoice("0.9.1",UpdatePromptChoice.Later,Now);
        Check(!value.ShouldPrompt("0.9.1",Now.AddHours(23.99))&&!value.ShouldCheck(Now.AddHours(23.99)),"Later snoozes automatic checks and prompts");
        Check(value.ShouldPrompt("0.9.1",Now.AddHours(24)),"Later permits next-day reminder");
        Check(value.ShouldPrompt("0.9.1",Now,true),"manual check bypasses a snooze");
        value.ApplyChoice("0.9.1",UpdatePromptChoice.IgnoreVersion,Now);
        Check(!value.ShouldPrompt("0.9.1",Now.AddDays(365)),"ignored version never automatically prompts again");
        Check(value.ShouldPrompt("0.9.2",Now),"newer versions are independent of one-version refusal");
        Check(value.ShouldPrompt("0.9.1",Now,true),"manual check may offer an ignored version");
        value.AutoCheck=false;
        Check(!value.ShouldCheck(Now.AddYears(1))&&!value.ShouldPrompt("0.9.2",Now.AddYears(1)),"global switch suppresses all automatic checks and offers");
        Check(value.ShouldPrompt("0.9.1",Now,true),"manual check remains available with global checks disabled");
        value.ApplyChoice("0.9.1",UpdatePromptChoice.Update,Now);
        Check(value.IgnoredVersion==""&&value.SnoozedUntilUtc==""&&!value.AutoCheck,"explicit installation clears matching refusal but does not silently re-enable auto checks");
        value.AutoCheck=true;value.MarkAutomaticCheck(DateTime.SpecifyKind(DateTime.MaxValue,DateTimeKind.Utc));
        Check(!value.ShouldCheck(Now),"future clock state stays quiet rather than looping");
        value.ApplyChoice("1.0.0",UpdatePromptChoice.Later,DateTime.SpecifyKind(DateTime.MaxValue.AddHours(-1),DateTimeKind.Utc));
        Check(value.SnoozedUntilUtc==DateTime.SpecifyKind(DateTime.MaxValue,DateTimeKind.Utc).ToString("o"),"snooze cannot overflow clock bounds");
        foreach(string bad in new[]{"","v0.9.1","0.9","01.0.0","0.9.1.0","0.9.1-beta","0.9.1\n","0.9.1/../x",new string('1',80)})
        {
            Throws<ArgumentException>(()=>value.ShouldPrompt(bad,Now),"invalid offered version rejected");
            Throws<ArgumentException>(()=>value.ApplyChoice(bad,UpdatePromptChoice.IgnoreVersion,Now),"invalid ignored version rejected");
        }
        Throws<ArgumentException>(()=>value.ShouldCheck(DateTime.SpecifyKind(Now,DateTimeKind.Unspecified)),"unspecified check clock rejected");
        Throws<ArgumentException>(()=>value.MarkAutomaticCheck(Now.ToLocalTime()),"local check clock rejected");
        Throws<ArgumentOutOfRangeException>(()=>value.ApplyChoice("1.0.0",(UpdatePromptChoice)99,Now),"unknown UI decision rejected");
    }
    static void Storage(string root)
    {
        string path=Path.Combine(root,"p.json");var store=new UpdatePreferenceStore(path);
        Check(store.Load().AutoCheck&&!File.Exists(path),"missing preferences use defaults without creating files");
        var preferences=new UpdatePreferences{AutoCheck=false,IgnoredVersion="0.9.1"};preferences.MarkAutomaticCheck(Now);
        store.Save(preferences);var loaded=store.Load();
        Check(!loaded.AutoCheck&&loaded.IgnoredVersion=="0.9.1"&&loaded.LastAutomaticCheckUtc==Now.ToString("o"),"all values persist separately from connection presets");
        byte[] first=File.ReadAllBytes(path);store.Save(preferences);
        Check(Directory.GetFiles(root,"p.json.backup-*").Length==0&&Directory.GetFiles(root,"p.json.staged-*").Length==0,"unchanged save produces no temporary or backup files");
        preferences.AutoCheck=true;preferences.ApplyChoice("0.9.2",UpdatePromptChoice.Later,Now);store.Save(preferences);
        string[] backups=Directory.GetFiles(root,"p.json.backup-*");
        Check(backups.Length==1&&File.ReadAllBytes(backups[0]).SequenceEqual(first),"atomic save retains exact preceding preferences");
        loaded=store.Load();Check(loaded.AutoCheck&&loaded.SnoozedUntilUtc==Now.AddHours(24).ToString("o"),"snooze round-trips");
        loaded.IgnoredVersion="99.0.0";Check(store.Load().IgnoredVersion=="0.9.1","loaded state is detached from stored preferences");
        byte[] current=File.ReadAllBytes(path);
        using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            Throws<IOException>(()=>store.Save(new UpdatePreferences{AutoCheck=false,IgnoredVersion="1.0.0"}),"locked preference file surfaces failure");
        Check(File.ReadAllBytes(path).SequenceEqual(current),"failed save preserves current preferences exactly");
        File.WriteAllText(path,"{invalid",new UTF8Encoding(false));
        var recovered=store.Load();
        Check(recovered.RecoveredFromBackup&&!recovered.AutoCheck&&recovered.IgnoredVersion=="0.9.1","damaged primary recovers preserved refusal from backup");
        Check(File.ReadAllText(path)=="{invalid","backup recovery does not rewrite damaged primary");
        string badPath=Path.Combine(root,"bad.json");var badStore=new UpdatePreferenceStore(badPath);
        foreach(string invalid in new[]{"{}","null","[]","{\"Schema\":1,\"AutoCheck\":true}","{\"Schema\":2,\"AutoCheck\":true,\"IgnoredVersion\":\"\",\"LastAutomaticCheckUtc\":\"\",\"SnoozedUntilUtc\":\"\"}",
            "{\"Schema\":1,\"AutoCheck\":true,\"IgnoredVersion\":\"0.9.1\\n\",\"LastAutomaticCheckUtc\":\"\",\"SnoozedUntilUtc\":\"\"}",
            "{\"Schema\":1,\"AutoCheck\":true,\"IgnoredVersion\":\"\",\"LastAutomaticCheckUtc\":\"garbage\",\"SnoozedUntilUtc\":\"\"}"})
        {
            File.WriteAllText(badPath,invalid,new UTF8Encoding(false));var quiet=badStore.Load();
            Check(!quiet.AutoCheck&&quiet.ReadProblem&&!quiet.ShouldCheck(Now),"unrecoverable preferences fail quiet without automatic requests");
        }
        File.WriteAllBytes(badPath,new byte[]{0xff,0xfe,0xff});Check(badStore.Load().ReadProblem,"invalid UTF8 fails quiet");
        File.WriteAllBytes(badPath,new byte[UpdatePreferenceStore.MaximumBytes+1]);Check(badStore.Load().ReadProblem,"oversized preferences never deserialized");
        Throws<ArgumentException>(()=>store.Save(new UpdatePreferences{IgnoredVersion="bad"}),"invalid refusal rejected before saving");
        Throws<ArgumentException>(()=>store.Save(new UpdatePreferences{LastAutomaticCheckUtc="bad"}),"invalid timestamps rejected before saving");
        Throws<ArgumentNullException>(()=>store.Save(null),"null preferences rejected");
        foreach(string invalid in new[]{"relative.json",@"\\server\share\p.json",root+":ads",Path.Combine(root,"p.txt"),Path.Combine(root,".json"),"C:relative.json"})
            Throws<ArgumentException>(()=>new UpdatePreferenceStore(invalid),"ambiguous remote or non-JSON path rejected");
        Check(!File.ReadAllText(path).Contains("SnoozedUntilUtc"),"corrupted primary remains intact after rejected writes");
    }
    static void Check(bool value,string description){checks++;if(!value)throw new Exception(description);}
    static void Throws<T>(Action action,string description) where T:Exception
    {bool thrown=false;try{action();}catch(T){thrown=true;}Check(thrown,description);}
}
