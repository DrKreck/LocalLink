using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace LocalLink
{
    internal static class AutomaticSetupTests
    {
        static int checks;
        static readonly DateTime Now=new DateTime(2026,10,28,12,0,0,DateTimeKind.Utc);
        static readonly string Network=new string('a',64),OtherNetwork=new string('b',64);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        [return:MarshalAs(UnmanagedType.U1)]static extern bool CreateSymbolicLink(string link,string target,int flags);
        static int Main()
        {
            string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"auto",Guid.NewGuid().ToString("N").Substring(0,12));
            try
            {
                Fingerprints();Evidence(root);Cache(root);InvalidStores(root);Bounded(root);Links(root);
                Console.WriteLine("Automatic setup: "+checks+" offline assertions passed. No network, filters, privileges, user profiles or installed applications changed. Fixtures: "+root);
                return 0;
            }
            catch(Exception error){Console.Error.WriteLine(error);return 1;}
        }
        static AppSettings Settings(){return new AppSettings{Services=new List<string>{"discord","youtube","telegram","steam","arma","tarkov","dcs"}};}
        static void Fingerprints()
        {
            var value=Settings();value.CustomDomains="ExAmPlE.org\r\nexample.net";
            value.DisabledDomains=new List<string>{"disabled.example.org"};
            string original=AutomaticSetupCache.SettingsFingerprint(value,"engine-v1");
            Check(original.Length==64&&original.All(c=>c>='0'&&c<='9'||c>='a'&&c<='f'),"routing fingerprint is bounded lowercase SHA256");
            Check(value.CustomDomains=="ExAmPlE.org\r\nexample.net","fingerprinting never normalizes caller settings in-place");
            value.Services.Reverse();value.CustomDomains="example.net\nexample.org";
            Check(original==AutomaticSetupCache.SettingsFingerprint(value,"engine-v1"),"service/domain order and casing do not force a redundant tune");
            value.Strategy="fake-disorder";value.DiscordStrategy="sni-split";value.Appearance="light";value.SelectedPresetId=Guid.NewGuid().ToString("N");
            value.AutomaticRecovery=false;value.AutomaticSetup=false;value.ConnectOnOpen=true;
            Check(original==AutomaticSetupCache.SettingsFingerprint(value,"engine-v1"),"method outputs, theme, preset ID, recovery and startup choices are not routing inputs");
            Check(original!=AutomaticSetupCache.SettingsFingerprint(value,"engine-v2"),"engine/list version change invalidates cached evidence");
            var changes=new Action<AppSettings>[] {
                x=>x.Services.Remove("youtube"),x=>x.Mode=1,x=>x.Quic=true,x=>x.DiscordVoice=false,x=>x.TelegramNative=false,
                x=>x.UsePublicLists=true,x=>x.UseCustomDomains=false,x=>x.CustomDomains="different.example.org",x=>x.DisabledDomains.Add("other.example.org")};
            foreach(var change in changes)
            {
                var changed=Settings();changed.CustomDomains="example.org\nexample.net";changed.DisabledDomains=new List<string>{"disabled.example.org"};change(changed);
                Check(original!=AutomaticSetupCache.SettingsFingerprint(changed,"engine-v1"),"routing option changes cannot reuse a stale fingerprint");
            }
            var vpn=Settings();vpn.Mode=1;vpn.Endpoint="203.0.113.1";
            string vpnFingerprint=AutomaticSetupCache.SettingsFingerprint(vpn);
            vpn.Endpoint="203.0.113.2";Check(vpnFingerprint!=AutomaticSetupCache.SettingsFingerprint(vpn),"VPN endpoint is hashed into routing fingerprint, not stored raw");
            vpn.Endpoint="203.0.113.1";vpn.Port=8443;Check(vpnFingerprint!=AutomaticSetupCache.SettingsFingerprint(vpn),"VPN port changes fingerprint");
            Expect<ArgumentNullException>(()=>AutomaticSetupCache.SettingsFingerprint(null),"null settings rejected");
            Expect<ArgumentException>(()=>AutomaticSetupCache.SettingsFingerprint(value,new string('a',257)),"environment token is bounded");
            Expect<ArgumentException>(()=>AutomaticSetupCache.SettingsFingerprint(value,"bad\nvalue"),"control characters cannot enter environment identity");
        }
        static List<CheckRow> ReadyRows(AppSettings settings)
        {
            var rows=new List<CheckRow>();
            foreach(string id in settings.Services.Where(x=>x!="telegram").Distinct())
            {
                rows.Add(Row(id,id+".web.tls",true));rows.Add(Row(id,id+".web.http",true));
                if(id=="steam"){rows.Add(Row(id,"steam.community.tls",true));rows.Add(Row(id,"steam.community.http",true));}
                if(id=="discord")
                {
                    rows.Add(Row(id,"discord.gateway",true));rows.Add(Row(id,"discord.voice",null));
                    foreach(string endpoint in new[]{"api","updater","manifest","cdn"})
                    {
                        string prefix="discord.desktop."+endpoint;
                        rows.Add(Row(id,prefix+".v4.dns",true));rows.Add(Row(id,prefix+".v6.dns",null));
                        rows.Add(Row(id,prefix+".v4"+(endpoint=="cdn"?".tls":""),true));
                    }
                }
            }
            rows.Add(Row("control","control.web.tls",true));rows.Add(Row("control","control.web.http",true));return rows;
        }
        static CheckRow Row(string service,string key,bool? passed){return new CheckRow{ServiceId=service,Key=key,Passed=passed};}
        static void Evidence(string root)
        {
            var settings=Settings();var rows=ReadyRows(settings);
            Check(AutomaticSetupPolicy.PublicReachabilityReady(settings,rows),"all selected public web/bootstrap probes may establish reachability");
            Check(!AutomaticSetupPolicy.ShouldTryDiscordMethod(settings,rows),"healthy Discord public bootstrap does not trigger needless method changes");
            Check(rows.Single(x=>x.Key=="discord.voice").Passed==null,"voice remains manual/unknown even when all public probes pass");
            Check(!AutomaticSetupPolicy.PublicReachabilityReady(settings,null),"no observations are not success");
            Check(!AutomaticSetupPolicy.PublicReachabilityReady(settings,new CheckRow[0]),"empty observations are not success");
            Check(!AutomaticSetupPolicy.PublicReachabilityReady(settings,rows.Concat(new[]{rows[0]})),"duplicate observation keys cannot establish success");
            Check(!AutomaticSetupPolicy.PublicReachabilityReady(settings,rows.Concat(new CheckRow[]{null})),"null observation cannot establish success");
            Check(!AutomaticSetupPolicy.PublicReachabilityReady(settings,Enumerable.Repeat(rows[0],257)),"unbounded observation collection rejected");
            var missing=ReadyRows(settings);missing.RemoveAll(x=>x.Key=="steam.community.http");
            Check(!AutomaticSetupPolicy.PublicReachabilityReady(settings,missing),"Steam community cannot silently disappear from ready evidence");
            foreach(string key in new[]{"youtube.web.tls","youtube.web.http","discord.gateway","discord.desktop.api.v4","discord.desktop.updater.v4","discord.desktop.cdn.v4.tls","discord.desktop.manifest.v4"})
            {
                var failed=ReadyRows(settings);failed.Single(x=>x.Key==key).Passed=false;
                Check(!AutomaticSetupPolicy.PublicReachabilityReady(settings,failed),"failed required probe blocks ready evidence: "+key);
            }
            var unknown=ReadyRows(settings);unknown.Single(x=>x.Key=="youtube.web.http").Passed=null;
            Check(!AutomaticSetupPolicy.PublicReachabilityReady(settings,unknown),"HTTP refusal/unknown is not fabricated application success");
            var control=ReadyRows(settings);control.Single(x=>x.ServiceId=="control"&&x.Key.EndsWith(".tls")).Passed=false;
            Check(!AutomaticSetupPolicy.PublicReachabilityReady(settings,control),"failed control blocks healthy shortcut");
            var discordFailed=ReadyRows(settings);discordFailed.Single(x=>x.Key=="discord.desktop.api.v4").Passed=false;
            Check(AutomaticSetupPolicy.ShouldTryDiscordMethod(settings,discordFailed),"actual Discord endpoint failure permits targeted method selection");
            Check(!AutomaticSetupPolicy.ShouldTryDiscordMethod(settings,new[]{Row("discord","discord.voice",null)}),"unknown/manual voice is not a network failure trigger");
            var onlyTelegram=new AppSettings{Services=new List<string>{"telegram"}};
            Check(!AutomaticSetupPolicy.PublicReachabilityReady(onlyTelegram,new[]{Row("telegram","telegram.web.tls",true),Row("telegram","telegram.dc1",true)}),"Telegram TCP/site cannot pretend to prove MTProto");
            settings.Mode=1;
            Check(!AutomaticSetupPolicy.PublicReachabilityReady(settings,rows)&&!AutomaticSetupPolicy.ShouldTryDiscordMethod(settings,discordFailed),"direct automatic methods never override a VPN helper mode");
            var cache=new AutomaticSetupCache(Path.Combine(root,"evidence.json"));settings.Mode=0;
            Check(!cache.RememberReachable(Network,settings,unknown,Now)&&!File.Exists(Path.Combine(root,"evidence.json")),"unknown evidence creates no learned-state file");
            Check(cache.RememberReachable(Network,settings,rows,Now),"positive public reachability can cache the current method without an invented user confirmation");
            Check(cache.Find(Network,settings,Now).Evidence=="public-reachable","public reachability is explicitly distinguished from user confirmation");
        }
        static TuningResult Verified(AppSettings settings)
        {
            return new TuningResult{Enabled=true,Verified=true,Strategy=settings.Strategy,Scope="general",Trials=new List<TuningTrialResult>{
                new TuningTrialResult{Strategy=settings.Strategy,Stage="verification",MatrixValid=true,PreservedBaseline=true,Improved=true,Verified=true}}};
        }
        static void Cache(string root)
        {
            string file=Path.Combine(root,"cache.json");var store=new AutomaticSetupCache(file);var settings=Settings();settings.Strategy="disorder";
            Check(store.Find(Network,settings,Now)==null&&!File.Exists(file),"read-only missing lookup creates no cache file");
            Check(!store.Forget(Network,settings)&&!File.Exists(file),"forget missing evidence creates no file");
            Check(store.Find("",settings,Now)==null,"unknown network never receives a shared/global cache key");
            Expect<ArgumentException>(()=>store.RememberUserConfirmed("",settings,Now),"invalid network key cannot be persisted");
            Expect<ArgumentException>(()=>store.Find(Network,settings,DateTime.SpecifyKind(Now,DateTimeKind.Local)),"local/ambiguous timestamp rejected");
            Check(!store.RememberVerified(Network,settings,new TuningResult{Enabled=true,Strategy="disorder"},Now),"running process without verified trials is not learned success");
            var result=Verified(settings);result.VpnOverride=true;
            Check(!store.RememberVerified(Network,settings,result,Now),"VPN override outcome is not learned as a direct route success");
            result=Verified(settings);result.Cancelled=true;Check(!store.RememberVerified(Network,settings,result,Now),"cancelled tune cannot populate cache");
            result=Verified(settings);result.TimedOut=true;Check(!store.RememberVerified(Network,settings,result,Now),"timed-out tune cannot populate cache");
            result=Verified(settings);result.Trials[0].PreservedBaseline=false;Check(!store.RememberVerified(Network,settings,result,Now),"regressive verification cannot populate cache");
            result=Verified(settings);result.Trials[0].Improved=false;Check(!store.RememberVerified(Network,settings,result,Now),"no-improvement metadata cannot masquerade as verified improvement");
            result=Verified(settings);result.Strategy="split";Check(!store.RememberVerified(Network,settings,result,Now),"result must describe the actually selected method");
            Check(store.RememberVerified(Network,settings,Verified(settings),Now,"list:123"),"repeated nonregressive selected candidate is learned");
            var found=store.Find(Network,settings,Now,"list:123");
            Check(found!=null&&found.Strategy=="disorder"&&found.DiscordStrategy==""&&found.Evidence=="repeated-public-probes"&&found.VerifiedUtc==Now,"lookup returns exact learned method and honest evidence kind");
            found.Strategy="fake-disorder";Check(store.Find(Network,settings,Now,"list:123").Strategy=="disorder","returned object cannot mutate stored choice");
            Check(store.Find(OtherNetwork,settings,Now,"list:123")==null,"same settings on another network do not inherit a method");
            Check(store.Find(Network,settings,Now,"list:456")==null,"changed verified domain-list version invalidates lookup");
            Check(store.Find(Network,settings,Now.AddDays(7),"list:123")!=null&&store.Find(Network,settings,Now.AddDays(7).AddTicks(1),"list:123")==null,"cached choice expires after seven days");
            Check(store.Find(Network,settings,Now.AddMinutes(-6),"list:123")==null,"implausibly future evidence is not trusted after clock drift");
            string before=File.ReadAllText(file);store.RememberVerified(Network,settings,Verified(settings),Now.AddMinutes(1),"list:123");
            Check(File.ReadAllText(file)==before&&!File.Exists(file+".previous"),"unchanged recent hint does not create repeated backup writes");
            settings.Strategy="fake-split";
            Check(store.Find(Network,settings,Now,"list:123").Strategy=="disorder","automatic output method changing does not invalidate otherwise identical routing input");
            settings.DiscordStrategy="sni-split";
            var discord=Verified(settings);discord.Scope="discord";discord.Strategy="sni-split";discord.Trials[0].Strategy="sni-split";
            Check(store.RememberVerified(Network,settings,discord,Now.AddHours(25),"list:123"),"targeted Discord verified choice can preserve a different general method");
            Check(store.Find(Network,settings,Now.AddHours(25),"list:123").DiscordStrategy=="sni-split"&&File.Exists(file+".previous"),"atomic replacement retains a prior valid cache backup");
            Check(store.RememberUserConfirmed(OtherNetwork,settings,Now.AddHours(26),"list:123"),"explicit user-confirmed method uses separate evidence kind");
            Check(store.Find(OtherNetwork,settings,Now.AddHours(26),"list:123").Evidence=="user-confirmed","user confirmation is not relabeled as automatic proof");
            Check(store.Forget(Network,settings,"list:123")&&store.Find(Network,settings,Now.AddHours(26),"list:123")==null,"failed cached route can be forgotten without affecting other networks");
            Check(store.Find(OtherNetwork,settings,Now.AddHours(26),"list:123")!=null,"forget preserves unrelated network choices");
            string serialized=File.ReadAllText(file);
            Check(!serialized.Contains("example.org")&&!serialized.Contains("Endpoint")&&!serialized.Contains("Services")&&!serialized.Contains(Environment.UserName),"cache stores hashes/method labels, not raw settings or user identifiers");
            settings.Mode=1;Check(store.Find(OtherNetwork,settings,Now.AddHours(26),"list:123")==null&&!store.RememberUserConfirmed(OtherNetwork,settings,Now),"cached direct method never overrides VPN helper mode");
        }
        static void InvalidStores(string root)
        {
            foreach(string json in new[]{"{broken}","{}","{\"Schema\":2,\"Entries\":[]}","{\"Schema\":1,\"Entries\":[],\"Other\":1}","{\"Schema\":1,\"Entries\":[{}]}",""})
            {
                string file=Path.Combine(root,"bad-"+Guid.NewGuid().ToString("N").Substring(0,8)+".json");File.WriteAllText(file,json,new UTF8Encoding(false));
                var cache=new AutomaticSetupCache(file);Check(cache.Find(Network,Settings(),Now)==null&&cache.ReadProblem,"malformed cache fails quiet during lookup");
                Expect<Exception>(()=>cache.RememberUserConfirmed(Network,Settings(),Now),"malformed primary is never overwritten by a learned method");
                Check(File.ReadAllText(file)==json,"invalid original bytes are retained");
            }
            string large=Path.Combine(root,"large.json");File.WriteAllText(large,new string('x',AutomaticSetupCache.MaximumBytes+1));
            var huge=new AutomaticSetupCache(large);Check(huge.Find(Network,Settings(),Now)==null&&huge.ReadProblem,"oversized cache is bounded before JSON parsing");
            string backup=Path.Combine(root,"foreign.json");var store=new AutomaticSetupCache(backup);store.RememberUserConfirmed(Network,Settings(),Now);
            File.WriteAllText(backup+".previous","foreign user data");string primary=File.ReadAllText(backup);
            Expect<Exception>(()=>store.RememberUserConfirmed(OtherNetwork,Settings(),Now),"unrelated backup bytes are not silently overwritten");
            Check(File.ReadAllText(backup)==primary&&File.ReadAllText(backup+".previous")=="foreign user data","failed backup validation preserves both files");
            Expect<ArgumentException>(()=>new AutomaticSetupCache(@"\\server\share\cache.json"),"UNC cache destination rejected");
            Expect<ArgumentException>(()=>new AutomaticSetupCache(Path.Combine(root,"cache.json:stream")),"alternate data stream destination rejected");
            Expect<ArgumentException>(()=>new AutomaticSetupCache(Path.Combine(root,"cache.txt")),"non-JSON destination rejected");
        }
        static void Bounded(string root)
        {
            string file=Path.Combine(root,"bounded.json");var cache=new AutomaticSetupCache(file);var settings=Settings();
            for(int index=1;index<=AutomaticSetupCache.MaximumEntries+1;index++)
                cache.RememberUserConfirmed(index.ToString("x64"),settings,Now.AddMinutes(index));
            var document=new JavaScriptSerializer().DeserializeObject(File.ReadAllText(file)) as Dictionary<string,object>;
            Check(((object[])document["Entries"]).Length==AutomaticSetupCache.MaximumEntries,"learned-network record count is bounded");
            Check(cache.Find(1.ToString("x64"),settings,Now.AddMinutes(130))==null&&cache.Find(129.ToString("x64"),settings,Now.AddMinutes(130))!=null,"oldest own hint is evicted, newest survives");
            Check(new FileInfo(file).Length<=AutomaticSetupCache.MaximumBytes,"bounded record set stays within file-size limit");
            cache.RememberUserConfirmed(OtherNetwork,settings,Now.AddDays(20));
            document=new JavaScriptSerializer().DeserializeObject(File.ReadAllText(file)) as Dictionary<string,object>;
            Check(((object[])document["Entries"]).Length==1,"expired evidence is removed only on a valid explicit cache update");
        }
        static void Links(string root)
        {
            string target=Path.Combine(root,"link-target");Directory.CreateDirectory(target);string link=Path.Combine(root,"link-folder");
            if(CreateSymbolicLink(link,target,1|2)||CreateSymbolicLink(link,target,1))
                Expect<IOException>(()=>new AutomaticSetupCache(Path.Combine(link,"cache.json")),"reparse parent cannot redirect a cache write");
            else Console.WriteLine("SKIP: fixture directory symlink unavailable; no privileges changed.");
        }
        static void Check(bool value,string message){if(!value)throw new Exception("FAIL: "+message);checks++;}
        static void Expect<T>(Action action,string message) where T:Exception
        {try{action();}catch(T){checks++;return;}throw new Exception("FAIL: "+message);}
    }
}
