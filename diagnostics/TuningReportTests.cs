using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using LocalLink;

internal static class TuningReportTests
{
    static int assertions;
    static void Check(bool ok,string name){assertions++;if(!ok)throw new Exception("FAIL "+name);}
    static int Main()
    {
        try
        {
            const string secret="PRIVATE-SYNTHETIC-FIXTURE";
            var trial=new TuningTrialResult{Strategy="sni-split",Stage="trial",Passed=7,Failed=2,Unknown=1,
                MatrixValid=true,PreservedBaseline=true,Improved=true};
            trial.Services.Add(new TuningServiceResult{ServiceId="discord",Passed=1,Failed=1,Unknown=1});
            trial.Services.Add(new TuningServiceResult{ServiceId=secret,Passed=9});
            var result=new TuningResult{Enabled=true,Verified=true,Strategy="sni-split",Summary=secret,Limits=secret,
                BaselinePassed=6,BaselineFailed=3,BaselineUnknown=1};
            result.BaselineServices.Add(new TuningServiceResult{ServiceId="control",Passed=4});result.Trials.Add(trial);
            string json=TuningReport.ToJson(result);
            Check(!json.Contains(secret),"private/free-form strings excluded");
            Check(json.Contains("locallink-tls-tuning-v1")&&json.Contains("sni-split"),"schema and fixed strategy present");
            var serializer=new JavaScriptSerializer();var root=serializer.DeserializeObject(json) as Dictionary<string,object>;
            Check(root!=null&&(bool)root["Verified"],"verification retained");
            Check(!root.ContainsKey("Summary")&&!root.ContainsKey("CheckRows"),"no raw journal/check details");
            Check(root["RecordedEngineProfile"]==null,"missing launch evidence is not invented");
            Check(((object[])root["Trials"]).Length==1,"only completed supplied trial exported");
            Check(json.Contains("discord")&&json.Contains("control"),"known service aggregates retained");
            result.Strategy=secret;trial.Strategy=secret;trial.Stage=secret;trial.Passed=Int32.MaxValue;trial.Failed=-9;
            json=TuningReport.ToJson(result);
            Check(!json.Contains(secret),"untrusted strategy/stage filtered");Check(json.Contains("unknown"),"unknown labels explicit");
            Check(json.Contains("\"Passed\":512")&&json.Contains("\"Failed\":0"),"counts bounded");
            for(int i=0;i<50;i++)result.Trials.Add(trial);
            root=serializer.DeserializeObject(TuningReport.ToJson(result)) as Dictionary<string,object>;
            Check(((object[])root["Trials"]).Length==12,"trial collection bounded");
            trial.Services.Add(new TuningServiceResult{ServiceId="discord",Passed=100});
            root=serializer.DeserializeObject(TuningReport.ToJson(result)) as Dictionary<string,object>;
            var exported=((object[])root["Trials"])[0] as Dictionary<string,object>;
            Check(((object[])exported["Services"]).Length==1,"duplicate/unknown services excluded");
            result.Cancelled=true;result.TimedOut=true;result.Enabled=false;result.Verified=false;
            root=serializer.DeserializeObject(TuningReport.ToJson(result)) as Dictionary<string,object>;
            Check((bool)root["Cancelled"]&&(bool)root["TimedOut"]&&!(bool)root["Enabled"],"partial cancellation status explicit");
            bool rejected=false;try{TuningReport.ToJson(null);}catch(ArgumentNullException){rejected=true;}
            Check(rejected,"null result rejected");
            string folder=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"tuning-report-fixture-"+Guid.NewGuid().ToString("N"));
            string first=TuningReport.Export(folder,result),second=TuningReport.Export(folder,result);
            Check(first!=second&&File.Exists(first)&&File.Exists(second),"unique exports preserve both reports");
            Check(Path.GetDirectoryName(first)==Path.Combine(folder,"reports"),"writes only own report folder");
            Check(new FileInfo(first).Length<65536,"export bounded");
            Check(!File.ReadAllText(first).Contains(secret),"on-disk report contains no fixture secret");
            Console.WriteLine("Tuning report: "+assertions+" assertions passed. Synthetic own files preserved; no network/filter/settings.");
            return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
