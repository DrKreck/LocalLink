// Synthetic in-memory report projection only. No network, driver, process,
// account, user setting, private key, hardware identifier or report file access.
using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using LocalLink;

internal static class DiscordReportTests
{
    static int assertions;
    const string Secret="PRIVATE_SYNTHETIC_FIXTURE_password_192.0.2.77";
    static int Main()
    {
        try
        {
            var rows=new List<CheckRow>();
            foreach(string endpoint in new[]{"api","updater","cdn","manifest"})
            foreach(string family in new[]{"v4","v6"})
            {
                string key="discord.desktop."+endpoint+"."+family;
                rows.Add(Row(key+".dns","ok",true,22));
                rows.Add(Row(key+".tcp","ok",true,11));
                rows.Add(Row(key+".tls","ok",true,17));
                rows.Add(Row(key,endpoint=="api"?"gateway-url-valid":endpoint=="updater"?"updater-redirect-valid":
                    endpoint=="manifest"?"manifest-payload-valid":"http-4xx-reachable",endpoint=="cdn"?(bool?)null:true,31));
            }
            rows.Add(Row("discord.gateway","ok",true,44));
            rows.Add(Row("discord.web.tls","ok",true,37));
            rows.Add(Row("discord.web.http","http-4xx-reachable",null,37));
            var probes=Tuning.ProbeEvidence(rows);
            Check(probes.Count==35,"all fixed endpoint/family/stage and Gateway/web keys preserved");
            foreach(var row in rows)
            {
                var probe=Find(probes,row.Key);
                Check(probe!=null && probe.Key==row.Key && probe.Code==row.DiagnosticCode,"known ID and diagnostic code retained");
                Check(probe.Passed==row.Passed && probe.Milliseconds==row.Milliseconds,"typed status and timing retained");
            }

            var polluted=new List<CheckRow>(rows);
            polluted.Add(Row(Secret,"ok",true,100));
            polluted.Add(Row("discord.desktop.api.v4."+Secret,"ok",true,100));
            polluted.Add(Row("discord.desktop.unknown.v4","ok",true,100));
            polluted.Add(Row("discord.desktop.api.v5","ok",true,100));
            polluted.Add(Row("discord.desktop.api.v4.http","ok",true,100));
            polluted.Add(Row("discord.voice","ok",true,100));
            polluted.Add(Row("youtube.web.tls","ok",true,100));
            polluted.Add(Row("discord.desktop.api.v4","ok",false,100));
            polluted.Add(null);
            var sanitized=Tuning.ProbeEvidence(polluted);
            Check(sanitized.Count==35,"unknown identity/manual service/duplicates cannot increase collection");
            Check(Find(sanitized,"discord.desktop.api.v4").Code=="gateway-url-valid","duplicate cannot replace first observation");
            Check(Tuning.ProbeEvidence(null).Count==0,"null projection empty");
            var wrongService=Row("discord.desktop.api.v4","gateway-url-valid",true,20); wrongService.ServiceId=Secret;
            Check(Tuning.ProbeEvidence(new[]{wrongService}).Count==0,"foreign/free-form service is not Discord evidence");

            var bounded=Tuning.ProbeEvidence(new[]{Row("discord.desktop.api.v4.tcp","tcp-refused",false,-1),
                Row("discord.desktop.api.v6.dns","dns-no-address",null,Int64.MaxValue),
                Row("discord.desktop.api.v6.tls","not-tested-dns",null,0)});
            Check(Find(bounded,"discord.desktop.api.v4.tcp").Milliseconds==0,"negative time clamped");
            Check(Find(bounded,"discord.desktop.api.v6.dns").Milliseconds==32000,"excessive time clamped");
            Check(Find(bounded,"discord.desktop.api.v6.dns").Passed==null,"absent family remains unknown");
            Check(Find(bounded,"discord.desktop.api.v6.tls").Code=="not-tested-dns","not-tested stage explicit");

            var result=new TuningResult{Scope="discord",Strategy="disorder",Enabled=true,Verified=true,
                Summary=Secret,Limits=Secret,BaselineProbes=probes};
            var trial=new TuningTrialResult{Strategy="sni-split",Stage="verification",Verified=true,Probes=sanitized};
            result.Trials.Add(trial);
            var serializer=new JavaScriptSerializer();
            string json=TuningReport.ToJson(result);
            var root=Map(serializer.DeserializeObject(json));
            Check((string)root["Scope"]=="discord","Discord-only scope explicit");
            Check(!json.Contains(Secret),"CheckRow free-form fields and result prose excluded");
            Check(((object[])root["BaselineProbes"]).Length==35,"baseline typed evidence included");
            var exportedTrial=Map(((object[])root["Trials"])[0]);
            Check(((object[])exportedTrial["Probes"]).Length==35,"trial typed evidence included");
            AssertProbeFields((object[])root["BaselineProbes"]);
            AssertProbeFields((object[])exportedTrial["Probes"]);
            Check(FindMap((object[])root["BaselineProbes"],"discord.desktop.updater.v4")["Code"].Equals("updater-redirect-valid"),"expected updater status preserved");
            Check(FindMap((object[])root["BaselineProbes"],"discord.desktop.manifest.v4")["Code"].Equals("manifest-payload-valid"),"manifest evidence preserved");
            Check(FindMap((object[])root["BaselineProbes"],"discord.desktop.cdn.v4")["Passed"]==null,"CDN404 never invented as success");

            // A caller can fabricate the typed model after projection. The
            // exporter must apply its own allowlist rather than trusting it.
            result.BaselineProbes=new List<TuningProbeResult>{
                new TuningProbeResult{Key=Secret,Code="ok",Passed=true,Milliseconds=10},
                new TuningProbeResult{Key="discord.desktop.api.v4",Code=Secret,Passed=true,Milliseconds=10},
                new TuningProbeResult{Key="discord.desktop.manifest.v4.tls",Code="certificate-or-tls",Passed=false,Milliseconds=Int64.MaxValue},
                new TuningProbeResult{Key="discord.desktop.updater.v4",Code="updater-redirect-valid",Passed=true,Milliseconds=-20},
                new TuningProbeResult{Key="discord.desktop.updater.v4",Code=Secret,Passed=true,Milliseconds=10},null};
            trial.Probes=new List<TuningProbeResult>(result.BaselineProbes);
            json=TuningReport.ToJson(result);
            Check(!json.Contains(Secret),"forged typed Key/Code cannot bypass exporter filtering");
            root=Map(serializer.DeserializeObject(json));
            var exported=(object[])root["BaselineProbes"];
            var tls=FindMap(exported,"discord.desktop.manifest.v4.tls");
            Check(tls!=null && (long)Convert.ToInt64(tls["Milliseconds"])==32000 && !(bool)tls["Passed"],"typed time and failure sanitized");
            var updater=FindMap(exported,"discord.desktop.updater.v4");
            Check(updater!=null && Convert.ToInt64(updater["Milliseconds"])==0,"typed negative time sanitized");
            Check(FindMap(exported,Secret)==null,"forged endpoint is not a fixed label");

            var spec=new ProfileSpec{Domains="discord.com\nyoutube.com\nprivate-synthetic.example",Strategy="disorder",DiscordStrategy="sni-split"};
            var profile=ProfileSummary.FromArguments(Profiles.Build(spec),null,true,new string('a',64));
            var evidence=new ProfileLaunchEvidence(profile,"stopped-history","saved");
            json=TuningReport.ToJson(result,evidence);
            root=Map(serializer.DeserializeObject(json));
            var recorded=Map(root["RecordedEngineProfile"]);
            Check((string)recorded["EngineState"]=="stopped-history","stopped launch remains historical");
            Check(((string)root["StateMeaning"]).Contains("stopped/historical"),"recorded launch not current connectivity proof");
            Check(!json.Contains("private-synthetic.example") && !json.Contains(Secret),"profile removes synthetic private domain");
            Check(Map(recorded["Profile"])["DiscordStrategy"].Equals("sni-split"),"separate selected Discord strategy retained");
            result.Scope=Secret;
            Check(!TuningReport.ToJson(result).Contains(Secret),"untrusted scope rejected from projection");
            var full=new TuningResult{Scope="discord",Strategy="fake-disorder",BaselineProbes=probes};
            foreach(var probe in probes) {probe.Code="http-redirect-not-followed";probe.Passed=null;probe.Milliseconds=32000;}
            foreach(string service in new[]{"control","discord","youtube","steam","arma","tarkov","dcs"})
                full.BaselineServices.Add(new TuningServiceResult{ServiceId=service,Passed=512,Failed=512,Unknown=512});
            for(int i=0;i<12;i++)
                full.Trials.Add(new TuningTrialResult{Strategy="fake-disorder",Stage="verification",Passed=512,Failed=512,Unknown=512,
                    Probes=probes,Services=new List<TuningServiceResult>(full.BaselineServices)});
            string maximumJson=TuningReport.ToJson(full);
            Check(maximumJson.Length<=131072,"full allowed identity/trial matrix stays within explicit report bound");
            Console.WriteLine("Discord structured report: "+assertions+" assertions passed; synthetic memory only.");
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    static CheckRow Row(string key,string code,bool? passed,long milliseconds)
    {return new CheckRow{Key=key,DiagnosticCode=code,Passed=passed,Milliseconds=milliseconds,Name=Secret,Detail=Secret,ServiceId="discord",State=Secret};}
    static TuningProbeResult Find(IEnumerable<TuningProbeResult> probes,string key)
    {foreach(var probe in probes)if(probe!=null&&probe.Key==key)return probe;return null;}
    static Dictionary<string,object> Map(object value){return value as Dictionary<string,object>;}
    static Dictionary<string,object> FindMap(IEnumerable<object> rows,string key)
    {foreach(var raw in rows){var row=Map(raw);if(row!=null&&row["Key"].Equals(key))return row;}return null;}
    static void AssertProbeFields(IEnumerable<object> rows)
    {
        foreach(var raw in rows)
        {
            var row=Map(raw);
            Check(row!=null&&row.Count==4&&row.ContainsKey("Key")&&row.ContainsKey("Code")&&row.ContainsKey("Passed")&&row.ContainsKey("Milliseconds"),"probe exact safe contract");
        }
    }
    static void Check(bool condition,string name){assertions++;if(!condition)throw new Exception("FAIL "+name);}
}
