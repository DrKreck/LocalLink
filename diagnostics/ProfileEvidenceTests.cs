using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace LocalLink
{
    // OFFLINE: pure argument summaries and isolated workspace receipts only.
    // Never calls ReadCurrent, launches a filter, observes real processes,
    // touches real appdata/preferences or deletes any fixture or backup.
    static class ProfileEvidenceTests
    {
        static int assertions;
        static readonly string Sha=new string('a',64);
        static string fixture;
        static int Main()
        {
            try
            {
                // Keep fixture overhead small in deeply nested Windows
                // workspaces. The GUID still isolates every run; no cleanup.
                fixture=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"pe-"+Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(fixture);
                TestSummaries();TestCoverage();TestPrivacy();TestReceipts();TestFailures();TestReparse();
                Console.WriteLine("Profile evidence: "+assertions+" offline assertions passed; no real engine, network, appdata or deletion.");
                return 0;
            }
            catch(Exception error)
            {
                Console.Error.WriteLine("Profile evidence fixture failed: "+error.GetType().Name);
                return 1;
            }
        }
        static void TestSummaries()
        {
            foreach(string strategy in new[]{"split","sni-split","disorder","fake-split","fake-disorder"})
                for(int flags=0;flags<8;flags++)
                {
                    bool quic=(flags&1)!=0,voice=(flags&2)!=0,telegram=(flags&4)!=0;
                    var spec=new ProfileSpec{Mode=HelperMode.Direct,Domains="customer-secret.example\nexample.org",Strategy=strategy,
                        Quic=quic,DiscordVoice=voice,TelegramNative=telegram};
                    var args=Profiles.Build(spec);
                    ProfileSummary summary=ProfileSummary.FromArguments(args,null,true,Sha);
                    Check(summary.Strategy==strategy&&summary.Mode=="direct"&&!summary.EndpointScoped,"actual action determines strategy/mode");
                    Check(summary.Quic==quic&&summary.DiscordVoice==voice&&summary.TelegramNative==telegram,"scope flags derive from actual filters");
                    Check(summary.ProfileCount==2+(quic?1:0)+(voice?6:0)+(telegram?1:0),"actual profile count");
                    Check(summary.InlineDomainCount==2+(voice?1:0),"inline hosts counted/deduplicated without output");
                    Check(summary.EngineFilesVerifiedAtPreflight&&summary.KnownEngineSha256==Sha,"known manifest provenance metadata");
                    Check(!summary.UsesExternalList&&!summary.ExternalListDomainCount.HasValue,"no external list claimed without actual argument");
                    args.Clear();spec.Strategy="wireguard";
                    Check(summary.Strategy==strategy&&summary.ProfileCount>0,"summary immutable after source/UI changes");
                }
            foreach(HelperMode mode in new[]{HelperMode.VpnTls,HelperMode.VpnWireGuard})
            {
                var args=Profiles.Build(new ProfileSpec{Mode=mode,Endpoint="10.20.30.40",Port=23456,Strategy=mode==HelperMode.VpnTls?"sni-split":"wireguard"});
                var summary=ProfileSummary.FromArguments(args,null,true,Sha);
                Check(summary.Mode==(mode==HelperMode.VpnTls?"vpn-tls":"vpn-wireguard")&&summary.EndpointScoped,"endpoint scoped without exporting address");
                Check(mode==HelperMode.VpnTls?summary.TcpPorts=="23456":summary.UdpPorts=="23456","actual numeric port retained");
            }
            var listArgs=Profiles.Build(new ProfileSpec{Domains="",HostListFile=@"C:\Private Folder\secret-list.txt",Strategy="split"});
            var external=ProfileSummary.FromArguments(listArgs,123456,true,Sha);
            Check(external.UsesExternalList&&external.ExternalListDomainCount==123456&&external.InlineDomainCount==0,"verified list count separate from inline domain union");
            foreach(PropertyInfo property in typeof(ProfileSummary).GetProperties())
                Check(property.GetSetMethod(false)==null,"no public mutable summary setter");
            var unknown=ProfileSummary.FromArguments(new[]{"--filter-tcp=443","--filter-l7=tls","--lua-desync=unknown:SECRET"},null,true,Sha);
            Check(unknown.Strategy=="unrecognized","future/unknown action never mislabelled split");
            unknown=ProfileSummary.FromArguments(new[]{"--filter-tcp=443","--lua-desync=multisplit:pos=2","--lua-desync=unknown:SECRET"},null,true,Sha);
            Check(unknown.Strategy=="unrecognized","extra unknown action does not hide behind a recognized strategy");
            Expect<IOException>(()=>ProfileSummary.FromArguments(new[]{"--filter-tcp=SECRET"},null,true,Sha),"invalid port text rejected without echo");
            Expect<IOException>(()=>ProfileSummary.FromArguments(new[]{"--filter-tcp=70000"},null,true,Sha),"invalid range rejected");
            Expect<IOException>(()=>ProfileSummary.FromArguments(new[]{"--filter-tcp=443"},0,true,Sha),"invalid external count rejected");
        }
        static void TestPrivacy()
        {
            var args=Profiles.Build(new ProfileSpec{Domains="customer-secret.example",Strategy="fake-disorder",DiscordVoice=true});
            var profile=ProfileSummary.FromArguments(args,null,true,Sha);
            string json=new JavaScriptSerializer().Serialize(profile),display=profile.ToDisplayString();
            Check(!json.Contains("customer-secret.example")&&!display.Contains("customer-secret.example"),"private domains never exported");
            Check(!json.Contains("--lua")&&!json.Contains("--wf")&&!json.Contains("ip.DstAddr"),"raw arguments not exported");
            foreach(string field in new[]{"Arguments","Endpoint","Domains","HostListFile","ProcessId","StartedUtcTicks","Path","Token"})
                Check(!json.Contains("\""+field+"\":"),"no private profile field");
            var receipt=new ProfileReceiptEvidence("running-match-unverified",false,profile);
            json=new JavaScriptSerializer().Serialize(receipt);
            Check(json.Contains("unverified")&&json.Contains("not cryptographic")&&!json.Contains("InternalProcessId"),"public receipt explicitly unverified and sanitized");
            var fields=profile.Fields();fields["Strategy"]="private-secret";
            Expect<IOException>(()=>ProfileSummary.FromFields(fields),"receipt cannot introduce arbitrary strategy text");
            fields=profile.Fields();fields["ExtraSecret"]="secret";
            Expect<IOException>(()=>ProfileSummary.FromFields(fields),"unknown profile fields rejected");
        }
        static ProfileSummary SummaryFor(string domains,bool external)
        {
            return ProfileSummary.FromArguments(Profiles.Build(new ProfileSpec{Domains=domains,Strategy="split",
                HostListFile=external?@"C:\Private Folder\secret-list.txt":""}),external?(int?)123:null,true,Sha);
        }
        static void TestCoverage()
        {
            var summary=SummaryFor("discord.com\ndiscord.gg\nyoutube.com\nytimg.com",false);
            var coverage=summary.TargetCoverage;
            Check(coverage.Count==5,"coverage bounded to exact public whitelist");
            foreach(var entry in coverage)Check(entry.Value=="inline-domain-match","exact and parent-domain matches declared inline");
            coverage["discord-site"]="forged";coverage["private-secret"]="forged";
            Check(summary.TargetCoverage.Count==5&&summary.TargetCoverage["discord-site"]=="inline-domain-match","getter returns fresh copy, no mutable summary map");
            summary=SummaryFor("www.youtube.com",false);
            Check(summary.TargetCoverage["youtube-www"]=="inline-domain-match"&&summary.TargetCoverage["youtube-site"]=="no-domain-match",
                "subdomain selection does not incorrectly include its parent");
            summary=SummaryFor("discord.com.evil.example\nnotdiscord.gg\nyoutube.com.attacker.example\nbadytimg.com",false);
            foreach(var entry in summary.TargetCoverage)Check(entry.Value=="no-domain-match","lookalike suffix boundary rejected");
            // Cyrillic o is converted to its actual punycode argument by Profiles.
            summary=SummaryFor("discоrd.com\nyоutube.com",false);
            foreach(var entry in summary.TargetCoverage)Check(entry.Value=="no-domain-match","IDN lookalikes never match ASCII diagnostic target");
            summary=SummaryFor("",true);
            foreach(var entry in summary.TargetCoverage)Check(entry.Value=="external-list-unchecked","external file flagged unchecked, never scanned or guessed");
            summary=SummaryFor("discord.gg",true);
            Check(summary.TargetCoverage["discord-gateway"]=="inline-domain-match","inline match retained in external-list union");
            Check(summary.TargetCoverage["youtube-site"]=="external-list-unchecked","union external contents remain unknown for no inline match");
            var fields=summary.Fields();var map=(Dictionary<string,string>)fields["TargetCoverage"];
            map["unknown-private-target"]="inline-domain-match";
            Expect<IOException>(()=>ProfileSummary.FromFields(fields),"unknown coverage key rejected");
            fields=summary.Fields();map=(Dictionary<string,string>)fields["TargetCoverage"];
            map["discord-site"]="secret-status";
            Expect<IOException>(()=>ProfileSummary.FromFields(fields),"arbitrary coverage value rejected");
            fields=SummaryFor("discord.com",false).Fields();map=(Dictionary<string,string>)fields["TargetCoverage"];
            map["discord-gateway"]="external-list-unchecked";
            Expect<IOException>(()=>ProfileSummary.FromFields(fields),"unchecked external status requires an actual external-list argument");
            string json=new JavaScriptSerializer().Serialize(summary);
            Check(json.Contains("TargetCoverage")&&!json.Contains("discord.gg")&&!json.Contains("secret-list.txt"),"only fixed safe coverage IDs/statuses exported");
        }
        static ProfileReceiptStore.Record Record(string state)
        {
            return new ProfileReceiptStore.Record{Identity=new string('b',32),State=state,ProcessId=987654321,
                StartedUtcTicks=new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc).Ticks,
                Summary=ProfileSummary.FromArguments(Profiles.Build(new ProfileSpec{Domains="private-customer.example",Strategy="split"}),null,true,Sha)};
        }
        static string PathFor(string label)
        {
            string folder=Path.Combine(fixture,label);Directory.CreateDirectory(folder);
            return Path.Combine(folder,"profile-receipt-v1.json");
        }
        static void TestReceipts()
        {
            string path=PathFor("lifecycle");int observations=0;
            Func<int,long,string> match=delegate(int pid,long ticks){observations++;Check(pid==987654321&&ticks==Record("running").StartedUtcTicks,"identity used only inside observer");return "match";};
            var missing=ProfileReceiptStore.ReadFromPath(path,match);
            Check(missing.Status=="missing"&&missing.Profile==null&&observations==0,"missing receipt never inferred from UI/settings");
            ProfileReceiptStore.WriteToPath(path,Record("running"),null);
            var running=ProfileReceiptStore.ReadFromPath(path,match);
            Check(running.Status=="running-match-unverified"&&!running.Historical&&running.Profile.Strategy=="split","matched process remains explicitly unverified");
            string publicJson=new JavaScriptSerializer().Serialize(running);
            Check(!publicJson.Contains("987654321")&&!publicJson.Contains(Record("running").StartedUtcTicks.ToString())&&
                !publicJson.Contains("InternalProcessId")&&!publicJson.Contains("InternalStartedUtcTicks"),"PID/starttime never enter public JSON");
            Check(ProfileReceiptStore.ReadFromPath(path,(pid,ticks)=>"absent").Status=="stale-history","absent engine leaves historical metadata");
            var mismatch=ProfileReceiptStore.ReadFromPath(path,(pid,ticks)=>"mismatch");
            Check(mismatch.Status=="stale-history"&&mismatch.Historical,"reused PID/name mismatch not running confirmation");
            var inaccessible=ProfileReceiptStore.ReadFromPath(path,(pid,ticks)=>"unavailable");
            Check(inaccessible.Status=="unverified-history"&&inaccessible.Historical,"access failure is unverified, not success");
            ProfileReceiptStore.WriteToPath(path,Record("stopped"),null);
            var stopped=ProfileReceiptStore.ReadFromPath(path,delegate(int pid,long ticks){throw new InvalidOperationException("must not observe stopped identity");});
            Check(stopped.Status=="stopped-history"&&stopped.Historical,"confirmed Stop is historical, observer not called");
            Check(Directory.GetFiles(Path.GetDirectoryName(path),"*.backup-*").Length==1,"atomic update preserves previous receipt backup");
            Check(Directory.GetFiles(Path.GetDirectoryName(path),"*.staged-*").Length==0,"committed stage consumed by atomic rename, no cleanup deletion");
            Check(new FileInfo(path).Length<=ProfileReceiptStore.MaximumReceiptBytes,"receipt bounded by bytes");
        }
        static void TestFailures()
        {
            string path=PathFor("failures");ProfileReceiptStore.WriteToPath(path,Record("running"),null);
            string previous=File.ReadAllText(path);
            Expect<IOException>(()=>ProfileReceiptStore.WriteToPath(path,Record("stopped"),(stage)=>{throw new IOException("simulated precommit");}),"interrupted save fails safely");
            Check(File.ReadAllText(path)==previous,"precommit failure preserves current receipt");
            Check(Directory.GetFiles(Path.GetDirectoryName(path),"*.staged-*").Length==1,"failure does not delete staged evidence");
            using(var held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
                Expect<IOException>(()=>ProfileReceiptStore.WriteToPath(path,Record("stopped"),null),"locked destination fails safely");
            Check(File.ReadAllText(path)==previous,"replace failure preserves old receipt");
            string corrupt=PathFor("corrupt");File.WriteAllText(corrupt,"not json",new UTF8Encoding(false));
            var invalid=ProfileReceiptStore.ReadFromPath(corrupt,(pid,ticks)=>"match");
            Check(invalid.Status=="invalid"&&invalid.Profile==null,"malformed receipt never reports profile success");
            File.WriteAllText(corrupt,new string('x',ProfileReceiptStore.MaximumReceiptBytes+1),new UTF8Encoding(false));
            Check(ProfileReceiptStore.ReadFromPath(corrupt,(pid,ticks)=>"match").Status=="invalid","oversized receipt rejected before JSON parse");
            string unknown=PathFor("unknown-fields");
            ProfileReceiptStore.WriteToPath(unknown,Record("running"),null);
            var fields=(Dictionary<string,object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(unknown));
            fields["OtherPrivateData"]="secret";File.WriteAllText(unknown,new JavaScriptSerializer().Serialize(fields),new UTF8Encoding(false));
            Check(ProfileReceiptStore.ReadFromPath(unknown,(pid,ticks)=>"match").Status=="invalid","unknown receipt field rejected without exporting it");
            string wrongName=Path.Combine(fixture,"not-the-own-receipt.json");
            Check(ProfileReceiptStore.ReadFromPath(wrongName,(pid,ticks)=>"match").Status=="invalid","whitelisted own receipt filename only");
            var notVerified=Record("running");notVerified.Summary=ProfileSummary.FromArguments(new[]{"--filter-tcp=443"},null,false,String.Empty);
            Expect<IOException>(()=>ProfileReceiptStore.WriteToPath(PathFor("unverified-save"),notVerified,null),"preparation without component verification cannot persist a running launch");
        }
        static void TestReparse()
        {
            string target=Path.Combine(fixture,"own-link-target"),link=Path.Combine(fixture,"own-directory-link");
            Directory.CreateDirectory(target);
            // Own workspace target only. No privilege request and no deletion.
            if(!CreateSymbolicLink(link,target,3))
            {Console.WriteLine("Reparse fixture skipped: unprivileged directory symlink unavailable; no elevation requested.");return;}
            string redirected=Path.Combine(link,"profile-receipt-v1.json");
            Check(ProfileReceiptStore.ReadFromPath(redirected,(pid,ticks)=>"match").Status=="invalid","receipt reader rejects reparse ancestor");
            Expect<IOException>(()=>ProfileReceiptStore.WriteToPath(redirected,Record("running"),null),"receipt writer rejects reparse ancestor before writing");
            Check(!File.Exists(Path.Combine(target,"profile-receipt-v1.json")),"reparse target untouched");
        }
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        [return:MarshalAs(UnmanagedType.I1)]
        static extern bool CreateSymbolicLink(string link,string target,int flags);
        static void Check(bool result,string label){assertions++;if(!result)throw new InvalidOperationException(label);}
        static void Expect<T>(Action action,string label)where T:Exception
        {assertions++;try{action();}catch(T){return;}throw new InvalidOperationException(label);}
    }
}
