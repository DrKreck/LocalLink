using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace LocalLink
{
    // A separate whitelist exporter, never the UI journal or CheckRow.Detail.
    // No custom domains, VPN addresses, process arguments or account state.
    public static class TuningReport
    {
        public static string ToJson(TuningResult result,ProfileLaunchEvidence evidence=null)
        {
            if(result==null)throw new ArgumentNullException("result");
            var trials=new List<object>();
            if(result.Trials!=null)foreach(var trial in result.Trials)
            {
                if(trial==null)continue;
                if(trials.Count>=12)break;
                trials.Add(new {Strategy=Strategy(trial.Strategy),Stage=trial.Stage=="trial"||trial.Stage=="verification"?trial.Stage:"unknown",
                    Passed=Count(trial.Passed),Failed=Count(trial.Failed),Unknown=Count(trial.Unknown),
                    trial.MatrixValid,trial.PreservedBaseline,trial.Improved,trial.Verified,Services=Services(trial.Services),Probes=Probes(trial.Probes)});
            }
            object profile=null;
            if(evidence!=null&&evidence.Profile!=null)
                profile=new {EngineState=evidence.Status,ReceiptStatus=evidence.ReceiptStatus,
                    Profile=ProfileSummary.FromFields(evidence.Profile.Fields()).Fields()};
            var report=new {Schema="locallink-tls-tuning-v1",ExportedUtc=DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture),
                Scope=result.Scope=="discord"?"discord":"general",
                result.Enabled,result.Verified,result.Cancelled,result.TimedOut,Strategy=Strategy(result.Strategy),
                BaselinePassed=Count(result.BaselinePassed),BaselineFailed=Count(result.BaselineFailed),BaselineUnknown=Count(result.BaselineUnknown),
                BaselineServices=Services(result.BaselineServices),BaselineProbes=Probes(result.BaselineProbes),Trials=trials,RecordedEngineProfile=profile,Limits=Tuning.ResultLimits,
                StateMeaning="Results describe the completed tuning run, not the current connection. RecordedEngineProfile describes the last owned launch and may be stopped/historical; it is not proof of packet delivery. Unknown/manual rows are not successes."};
            return new JavaScriptSerializer{MaxJsonLength=65536,RecursionLimit=16}.Serialize(report);
        }
        static string Strategy(string value){return Profiles.IsTlsStrategy(value)?value:"unknown";}
        static List<object> Probes(IEnumerable<TuningProbeResult> rows)
        {
            var result=new List<object>();var seen=new HashSet<string>(StringComparer.Ordinal);
            if(rows==null)return result;
            foreach(var row in rows)
                if(row!=null&&Tuning.KnownProbeKey(row.Key)&&seen.Add(row.Key))
                    result.Add(new {row.Key,Code=Tuning.KnownProbeCode(row.Code)?row.Code:"unknown",row.Passed,Milliseconds=Math.Max(0,Math.Min(32000,row.Milliseconds))});
            return result;
        }
        static int Count(int value){return Math.Max(0,Math.Min(512,value));}
        static List<object> Services(IEnumerable<TuningServiceResult> rows)
        {
            var result=new List<object>();var seen=new HashSet<string>(StringComparer.Ordinal);
            if(rows==null)return result;
            foreach(var row in rows)
            {
                if(row==null||!KnownService(row.ServiceId)||!seen.Add(row.ServiceId))continue;
                result.Add(new {row.ServiceId,Passed=Count(row.Passed),Failed=Count(row.Failed),Unknown=Count(row.Unknown)});
            }
            return result;
        }
        static bool KnownService(string value)
        {return value=="control"||value=="discord"||value=="youtube"||value=="steam"||value=="arma"||value=="tarkov";}
        public static string Export(string appRoot,TuningResult result,ProfileLaunchEvidence evidence=null)
        {
            if(result==null)throw new ArgumentNullException("result");
            string root=Path.GetFullPath(appRoot);RejectReparse(root);
            string folder=Path.Combine(root,"reports");RejectReparse(folder);
            Directory.CreateDirectory(folder);RejectReparse(folder);
            string name="tls-tuning-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss",CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N").Substring(0,8)+".json";
            string path=Path.Combine(folder,name);RejectReparse(path);
            byte[] bytes=new UTF8Encoding(false,true).GetBytes(ToJson(result,evidence));
            using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {stream.Write(bytes,0,bytes.Length);stream.Flush(true);}
            return path;
        }
        static void RejectReparse(string path)
        {
            for(string current=Path.GetFullPath(path);!String.IsNullOrEmpty(current);current=Path.GetDirectoryName(current))
                if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                    throw new IOException("Reports must use an ordinary local folder.");
        }
    }
}
