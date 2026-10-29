using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using LocalLink;

internal static class PresetExchangeTests
{
    static int checks;
    static readonly UTF8Encoding Utf8=new UTF8Encoding(false);
    static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=PresetExchange.MaximumBytes+1024,RecursionLimit=20};
    static int Main()
    {
        string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"px",Guid.NewGuid().ToString("N").Substring(0,12));
        Directory.CreateDirectory(root);
        try
        {
            var original=new AppSettings{Services=new List<string>{"discord","youtube"},Strategy="fake-disorder",DiscordStrategy="sni-split",
                Quic=true,DiscordVoice=true,TelegramNative=false,UsePublicLists=true,UseCustomDomains=true,
                CustomDomains="EXAMPLE.ORG\nsecond.example.org",DisabledDomains=new List<string>{"excluded.example.org"},
                Endpoint="203.0.113.10",Port=8443,Appearance="dark",AutomaticRecovery=false,AutomaticSetup=false,ConnectOnOpen=true,
                SelectedPresetId=Guid.NewGuid().ToString("N")};
            string file=Path.Combine(root,"shared.json");
            PresetExchange.Write(file,"  Моя сеть  ",original);
            var imported=PresetExchange.Read(file);
            Check(imported.Name=="Моя сеть","portable preset retains trimmed readable name");
            Check(imported.Settings.Services.SequenceEqual(original.Services),"service categories retained");
            Check(imported.Settings.Strategy=="fake-disorder"&&imported.Settings.DiscordStrategy=="sni-split","both TLS methods retained");
            Check(imported.Settings.Quic&&imported.Settings.DiscordVoice&&!imported.Settings.TelegramNative,"protocol selection retained");
            Check(imported.Settings.UsePublicLists&&imported.Settings.UseCustomDomains&&imported.Settings.DisabledDomains.SequenceEqual(original.DisabledDomains),"category exclusions retained");
            Check(imported.Settings.CustomDomains=="example.org"+Environment.NewLine+"second.example.org","custom domains use validated canonical form");
            Check(imported.Settings.Endpoint==original.Endpoint&&imported.Settings.Port==8443&&imported.Settings.Appearance=="dark","explicit endpoint, port and theme retained");
            Check(!imported.Settings.AutomaticRecovery&&!imported.Settings.AutomaticSetup,"automatic setup and recovery selections retained");
            Check(!imported.Settings.ConnectOnOpen&&imported.Settings.SelectedPresetId=="","portable file cannot enable connection on opening or select a foreign local preset");
            string raw=File.ReadAllText(file);
            foreach(string absent in new[]{"ConnectOnOpen","SelectedPresetId","AutoCheck","IgnoredVersion","LastAutomaticCheckUtc","NetworkKey","HealthToken","PrivateKey"})
                Check(!raw.Contains(absent),"portable export excludes "+absent);
            Check(original.ConnectOnOpen&&original.SelectedPresetId.Length==32&&original.CustomDomains.StartsWith("EXAMPLE"),"export never mutates caller settings");
            imported.Settings.Services.Clear();imported.Settings.DisabledDomains.Clear();
            Check(PresetExchange.Read(file).Settings.Services.Count==2&&PresetExchange.Read(file).Settings.DisabledDomains.Count==1,"read returns detached settings");
            byte[] first=File.ReadAllBytes(file);
            Expect<IOException>(()=>PresetExchange.Write(file,"X",new AppSettings()),"overwrite requires explicit opt-in");
            Check(File.ReadAllBytes(file).SequenceEqual(first),"denied overwrite preserves original bytes");
            PresetExchange.Write(file,"Моя сеть",original,true);
            Check(Directory.GetFiles(root,"shared.json.backup-*").Length==0,"identical overwrite produces no backup churn");
            PresetExchange.Write(file,"Новый вариант",new AppSettings{Strategy="disorder"},true);
            Check(PresetExchange.Read(file).Name=="Новый вариант","explicit overwrite commits a new portable preset");
            Check(Directory.GetFiles(root,"shared.json.backup-*").Length==1&&File.ReadAllBytes(Directory.GetFiles(root,"shared.json.backup-*")[0]).SequenceEqual(first),"overwrite retains exact preceding file as backup");
            byte[] second=File.ReadAllBytes(file);
            using(var held=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read))
                Expect<IOException>(()=>PresetExchange.Write(file,"Locked",new AppSettings(),true),"locked primary refuses replacement");
            Check(File.ReadAllBytes(file).SequenceEqual(second),"failed replacement leaves prior file unchanged");
            foreach(string invalid in new[]{"relative.json",@"\\host\share\x.json",file+":hidden",Path.Combine(root,"x.txt")})
                Expect<ArgumentException>(()=>PresetExchange.Read(invalid),"nonlocal or non-JSON import path rejected");
            foreach(string invalid in new[]{"", " ","bad\nname",new string('n',81)})
                Expect<IOException>(()=>PresetExchange.Write(Path.Combine(root,"invalid-name.json"),invalid,new AppSettings()),"invalid preset name rejected before write");
            Check(!File.Exists(Path.Combine(root,"invalid-name.json")),"invalid names create no destination");
            Expect<ArgumentNullException>(()=>PresetExchange.Write(Path.Combine(root,"null.json"),"X",null),"null settings rejected");
            var maximum=new AppSettings{CustomDomains=String.Join("\n",Enumerable.Range(0,128).Select(i=>"custom"+i+".example.org"))};
            maximum.DisabledDomains=Enumerable.Range(0,DomainSelection.MaximumRules).Select(i=>"disabled"+i+".example.org").ToList();
            string maximumPath=Path.Combine(root,"maximum.json");PresetExchange.Write(maximumPath,"Полный пресет",maximum);
            var maximumCopy=PresetExchange.Read(maximumPath).Settings;
            Check(maximumCopy.DisabledDomains.Count==DomainSelection.MaximumRules,"all 2048 explicit exclusions fit a portable preset");
            Check(Profiles.ValidateDomains(maximumCopy.CustomDomains).Length==128,"all 128 custom domain roots roundtrip");
            maximum.CustomDomains+="\ncustom-overflow.example.org";
            Expect<IOException>(()=>PresetExchange.Write(Path.Combine(root,"too-many.json"),"Overflow",maximum),"custom-domain count overflow rejected before any destination");
            Check(!File.Exists(Path.Combine(root,"too-many.json")),"count-overflow export leaves destination absent");
            ValidateDocuments(root,raw);
            Working(root);
            Console.WriteLine("Preset exchange: "+checks+" offline checks passed. No real preferences, filter, apps or network changed.");
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    static void ValidateDocuments(string root,string valid)
    {
        string bad=Path.Combine(root,"bad.json");
        foreach(string text in new[]{"", "{broken", "[]", "null", "{}",valid.Replace("locallink-preset-v1","locallink-preset-v9")})
        {File.WriteAllText(bad,text,Utf8);Expect<IOException>(()=>PresetExchange.Read(bad),"malformed or unsupported document rejected");}
        Action<Dictionary<string,object>>[] corrupt={
            d=>d["Commands"]=new[]{"execute"},d=>d.Remove("Settings"),d=>d["Name"]="bad\nname",
            d=>Settings(d)["ConnectOnOpen"]=true,d=>Settings(d)["AutomaticSetup"]="true",d=>Settings(d).Remove("Mode"),
            d=>Settings(d)["Mode"]=99,d=>Settings(d)["Port"]=0,d=>Settings(d)["Port"]=65536,d=>Settings(d)["Port"]="443",
            d=>Settings(d)["Services"]=new[]{"not-a-service"},d=>Settings(d)["Services"]=new[]{"discord","discord"},
            d=>Settings(d)["Services"]="discord",d=>Settings(d)["Strategy"]="--run-command",d=>Settings(d)["DiscordStrategy"]="unknown",
            d=>Settings(d)["Appearance"]="foreign",d=>Settings(d)["CustomDomains"]="https://example.org/path",
            d=>Settings(d)["CustomDomains"]=new string('x',8193),d=>Settings(d)["Endpoint"]="127.0.0.1 & execute",
            d=>Settings(d)["DisabledDomains"]=new[]{"https://example.org"},d=>Settings(d)["DisabledDomains"]=Enumerable.Repeat("example.org",DomainSelection.MaximumRules+1).ToArray()
        };
        foreach(var corruptDocument in corrupt)
        {
            var value=Json.DeserializeObject(valid) as Dictionary<string,object>;corruptDocument(value);
            string text=Json.Serialize(value);File.WriteAllText(bad,text,Utf8);
            Expect<IOException>(()=>PresetExchange.Read(bad),"unknown field, wrong type or invalid connection data rejected");
            Check(File.ReadAllText(bad)==text,"invalid import is never rewritten");
        }
        File.WriteAllBytes(bad,new byte[]{0xff,0xfe,0x01});
        Expect<ArgumentException>(()=>PresetExchange.Read(bad),"invalid UTF8 rejected");
        File.WriteAllBytes(bad,new byte[PresetExchange.MaximumBytes+1]);
        Expect<IOException>(()=>PresetExchange.Read(bad),"oversized input rejected before JSON allocation");
        string sentinel=Path.Combine(root,"sentinel.txt");File.WriteAllText(sentinel,"not-app-data",Utf8);
        Expect<IOException>(()=>PresetExchange.Write(Path.Combine(root,"invalid-settings.json"),"X",new AppSettings{CustomDomains="--execute"}),"invalid exported custom domains rejected");
        Check(File.ReadAllText(sentinel)=="not-app-data","unrelated files preserved");
    }
    static Dictionary<string,object> Settings(Dictionary<string,object> document){return (Dictionary<string,object>)document["Settings"];}
    static void Working(string root)
    {
        string folder=Path.Combine(root,"working");var store=new WorkingSettingsStore(folder);
        Check(store.Load()==null&&!Directory.Exists(folder),"reading absent working snapshot creates no directories");
        var first=new AppSettings{Strategy="split",ConnectOnOpen=true};store.Remember(first);
        Check(store.Load().Strategy=="split"&&!store.Load().ConnectOnOpen,"working snapshot cannot introduce startup connection permission");
        store.Remember(first);Check(Directory.GetFiles(folder,"working.json.backup-*").Length==0,"unchanged working snapshot has no backup churn");
        store.Remember(new AppSettings{Strategy="disorder",AutomaticSetup=false});
        Check(store.Load().Strategy=="disorder"&&!store.Load().AutomaticSetup,"explicit working snapshot replaces previous configuration");
        Check(Directory.GetFiles(folder,"working.json.backup-*").Length==1,"previous working snapshot is retained");
        string primary=Path.Combine(folder,"working.json");File.WriteAllText(primary,"{damaged",Utf8);
        Check(store.Load().Strategy=="split","damaged working snapshot recovers preceding valid backup");
        Check(File.ReadAllText(primary)=="{damaged","recovery preserves damaged file for diagnosis");
        var detached=store.Load();detached.Services.Clear();Check(store.Load().Services.Count>0,"working snapshot reads are detached");
        Expect<ArgumentException>(()=>new WorkingSettingsStore("relative"),"working store rejects relative folder");
    }
    static void Check(bool passed,string description){if(!passed)throw new InvalidOperationException("FAIL: "+description);checks++;}
    static void Expect<T>(Action action,string description) where T:Exception
    {try{action();}catch(T){checks++;return;}throw new InvalidOperationException("FAIL: "+description);}
}
