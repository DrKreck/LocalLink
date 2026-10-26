using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using LocalLink;

// Only unique package-local fixtures. No user data, network, live filter,
// registry, telemetry, external processes, or elevation.
internal static class BugReportTests
{
    static int assertions,skipped;
    static readonly Encoding Utf8=new UTF8Encoding(false);
    static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=4*1024*1024,RecursionLimit=64};
    static readonly string[] Sentinels={"SENTINEL_SECRET_TOKEN_7263","SENTINEL_CUSTOM_DOMAIN.invalid","203.0.113.77","SecretFixtureUser","Bearer secret-auth-fixture"};
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.U1)] static extern bool CreateSymbolicLink(string link,string target,int flags);

    static int Main()
    {
        string fixture=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"bug-report-test-fixtures",Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(fixture);
            Disabled(Path.Combine(fixture,"disabled"));
            Persistence(Path.Combine(fixture,"history"));
            RecoveredMemoryEvictions(Path.Combine(fixture,"recovered-memory"));
            PrivacyAndArchive(Path.Combine(fixture,"privacy"));
            Retention(Path.Combine(fixture,"retention"));
            CorruptInput(Path.Combine(fixture,"corrupt"));
            FailedStorage(Path.Combine(fixture,"failed-storage"));
            Reparse(Path.Combine(fixture,"reparse"));
            Console.WriteLine("Bug reports: "+assertions+" offline assertions passed; "+skipped+" optional symlink checks skipped. Own fixtures retained: "+fixture);
            return 0;
        }
        catch(Exception error)
        {
            Console.Error.WriteLine(error);Console.Error.WriteLine("Own fixture retained: "+fixture);return 1;
        }
    }
    static void Disabled(string root)
    {
        using(var history=new DiagnosticHistory(root,false))
        {
            history.Record("session_started");history.RecordException("caught_error",new IOException(Sentinels[0]));history.Snapshot();
            Check(!history.PersistenceAvailable,"disabled preview has no persistent store");
        }
        Check(!Directory.Exists(root),"disabled preview never creates a diagnostic directory");
        using(var history=DiagnosticHistory.Disabled()){history.Record("session_started");Check(!history.PersistenceAvailable,"disabled factory stays nonpersistent");}
    }
    static void Persistence(string root)
    {
        string firstSession;
        using(var history=new DiagnosticHistory(root,true))
        {
            firstSession=history.SessionId;Guid parsed;
            Check(Guid.TryParse(firstSession,out parsed),"session identifier is random GUID-shaped data");
            Check(history.PersistenceAvailable,"ordinary own fixture persistence is available");
            history.Record("operation_started",new Dictionary<string,object>{{"operation","check"},{"mode",0},{"strategy","disorder"}});
            history.Record("check_result",new Dictionary<string,object>{{"service","discord"},{"stage","tls"},{"passed",true},{"milliseconds",123}});
            var snapshot=history.Snapshot();
            Check(snapshot.Events.Any(x=>x.SessionId==firstSession&&x.Code=="check_result"),"current structured result is present in memory snapshot");
            Check(snapshot.Events.All(x=>DateTime.Parse(x.TimestampUtc).Kind!=DateTimeKind.Unspecified),"events have time-zone-qualified timestamps");
        }
        using(var history=new DiagnosticHistory(root,true))
        {
            history.Record("operation_finished",new Dictionary<string,object>{{"operation","check"},{"count",2}});
            var snapshot=history.Snapshot();
            Check(history.SessionId!=firstSession,"next app session has a different identifier");
            Check(snapshot.Events.Any(x=>x.SessionId==firstSession)&&snapshot.Events.Any(x=>x.SessionId==history.SessionId),"snapshot restores history across sessions");
            var tasks=new List<Task>();
            for(int worker=0;worker<4;worker++)tasks.Add(Task.Factory.StartNew(delegate{for(int index=0;index<30;index++)history.Record("check_result",new Dictionary<string,object>{{"service","discord"},{"milliseconds",index}});}));
            Task.WaitAll(tasks.ToArray());snapshot=history.Snapshot();
            var own=snapshot.Events.Where(x=>x.SessionId==history.SessionId).ToArray();
            Check(own.Length>=121,"concurrent writers keep their structured events");
            Check(own.Select(x=>x.Sequence).Distinct().Count()==own.Length,"concurrent sequence numbers are unique per session");
        }
        Check(Directory.GetFiles(root,"session-*.jsonl").Length>=2,"independent session files exist on disk");
        foreach(string file in Directory.GetFiles(root,"session-*.jsonl"))AssertPrivateAbsent(File.ReadAllText(file,Utf8),"persisted structured history");
    }
    static void PrivacyAndArchive(string root)
    {
        Directory.CreateDirectory(root);string historyRoot=Path.Combine(root,"history");
        using(var history=new DiagnosticHistory(historyRoot,true))
        {
            var fields=new Dictionary<string,object>{{"operation","check"},{"strategy","disorder"},{"milliseconds",17},{"enabled",true},{"password",Sentinels[0]},{"endpoint",Sentinels[2]},{"customDomains",Sentinels[1]},{"stage",Sentinels[3]},{"services",new[]{"discord",Sentinels[0]}}};
            history.Record("check_result",fields);
            history.Record(Sentinels[0],new Dictionary<string,object>{{"strategy",Sentinels[0]},{"service",Sentinels[1]},{"code",Sentinels[4]}});
            try{ThrowFixtureException();}catch(Exception error){history.RecordException("caught_error",error);}
            history.RecordException("caught_error",new System.ComponentModel.Win32Exception(5,Sentinels[0]));
            var snapshot=history.Snapshot();
            string safeSnapshot=Json.Serialize(snapshot);AssertPrivateAbsent(safeSnapshot,"history DTO");
            Check(snapshot.Events.Any(x=>x.Code=="caught_error"&&x.Error!=null),"typed exception diagnostic is retained");
            Check(snapshot.Events.Any(x=>x.Error!=null&&x.Error.NativeCode==5),"numeric native Win32 error code survives without its private message");
            Check(snapshot.Events.Any(x=>x.Code=="unknown_event"),"unknown event identifiers are not echoed");
            Check(safeSnapshot.IndexOf("HResult",StringComparison.Ordinal)>=0,"exception diagnostic contains a numeric HResult");
            Check(safeSnapshot.IndexOf("Message\"",StringComparison.Ordinal)<0&&safeSnapshot.IndexOf("StackTrace\"",StringComparison.Ordinal)<0,"raw exception text and source stack strings are excluded");
            var state=new BugReportSnapshot{AppVersion="0.8.0",Mode=0,Strategy="disorder",DiscordStrategy="fake-split",Busy=false,FilterRunning=false,DiscordVoice=true,TelegramNative=true,Quic=false,UsePublicLists=false,CustomDomainCount=7};
            state.SelectedServices.Add("discord");state.SelectedServices.Add("youtube");state.SelectedServices.Add(Sentinels[0]);
            state.Checks.Add(new BugReportCheck{ServiceId="discord",Stage="tls",Code="ok",Passed=true,Milliseconds=123});
            state.Checks.Add(new BugReportCheck{ServiceId=Sentinels[0],Stage=Sentinels[1],Code=Sentinels[4],Passed=false,Milliseconds=int.MaxValue});
            state.TuningJson="{\"Schema\":\"locallink-tls-tuning-v1\",\"RawSecret\":\""+Sentinels[0]+"\"}";
            string path=Path.Combine(root,"first-report.zip");
            string actual=BugReport.Export(path,history,state);
            Check(Path.GetFullPath(actual)==Path.GetFullPath(path)&&File.Exists(path),"manual export creates the requested local ZIP");
            var entries=ReadZip(path);
            Check(entries.Keys.OrderBy(x=>x,StringComparer.Ordinal).SequenceEqual(new[]{"SUMMARY.txt","history.json","manifest.json","report.json"}.OrderBy(x=>x,StringComparer.Ordinal)),"ZIP has exactly four fixed entries");
            Check(entries.Keys.All(x=>x.IndexOf("..",StringComparison.Ordinal)<0&&x.IndexOf('/')<0&&x.IndexOf('\\')<0),"ZIP entry names cannot traverse paths");
            foreach(var entry in entries)AssertPrivateAbsent(Utf8.GetString(entry.Value),"ZIP "+entry.Key);
            Check(Utf8.GetString(entries["report.json"]).Contains("locallink-bug-report-v1"),"report JSON has a versioned schema");
            Check(Utf8.GetString(entries["history.json"]).Contains("locallink-diagnostic-history-v1"),"history JSON has a versioned schema");
            Check(Utf8.GetString(entries["manifest.json"]).Contains("locallink-bug-report-manifest-v1"),"manifest JSON has a versioned schema");
            var report=(Dictionary<string,object>)Json.DeserializeObject(Utf8.GetString(entries["report.json"]));
            Check(report.ContainsKey("App")&&report.ContainsKey("State")&&report.ContainsKey("Checks")&&report.ContainsKey("History"),"report includes app, current state, checks and history metadata");
            ValidateManifest(entries);
            byte[] before=File.ReadAllBytes(path);Expect<IOException>(delegate{BugReport.Export(path,history,state);},"export refuses overwriting an existing report");
            Check(before.SequenceEqual(File.ReadAllBytes(path)),"failed overwrite preserves original ZIP bytes");
            string folderDestination=Path.Combine(root,"directory.zip");Directory.CreateDirectory(folderDestination);int filesBefore=Directory.GetFiles(root).Length;
            Expect<Exception>(delegate{BugReport.Export(folderDestination,history,state);},"a directory cannot be used as a report destination");
            Check(Directory.GetFiles(root).Length==filesBefore,"failed directory destination leaves no temporary archive");
            Expect<Exception>(delegate{BugReport.Export(Path.Combine(root,"report.zip:alternate-stream"),history,state);},"report destination cannot use an alternate data stream");
            Check(!File.Exists(Path.Combine(root,"report.zip")),"invalid alternate-stream path creates no base file");
            Expect<IOException>(delegate{BugReport.Export(Path.Combine(root,"report.txt"),history,state);},"report destination requires the ZIP extension");
            var validTuning=new TuningResult{Strategy="disorder",Verified=true,Scope="discord",BaselinePassed=1};
            state.TuningJson=TuningReport.ToJson(validTuning);
            string second=BugReport.Export(Path.Combine(root,"second-report.zip"),history,state);
            var secondEntries=ReadZip(second);Check(Utf8.GetString(secondEntries["report.json"]).Contains("disorder"),"valid safe tuning method survives export");
            foreach(var entry in secondEntries)AssertPrivateAbsent(Utf8.GetString(entry.Value),"second ZIP "+entry.Key);
            string unicodeDirectory=Path.Combine(root,"Отчёты тест");Directory.CreateDirectory(unicodeDirectory);
            string unicodePath=Path.Combine(unicodeDirectory,"Отчёт с пробелом.zip");
            Check(BugReport.Export(unicodePath,history,state)==Path.GetFullPath(unicodePath)&&File.Exists(unicodePath),"Unicode and spaces in the report folder and filename work");
            var unicodeEntries=ReadZip(unicodePath);ValidateManifest(unicodeEntries);
            foreach(var entry in unicodeEntries)AssertPrivateAbsent(Utf8.GetString(entry.Value),"Unicode-path ZIP "+entry.Key);
            state.AppVersion=Sentinels[0];state.Strategy=Sentinels[1];state.DiscordStrategy=Sentinels[2];
            var invalidState=ReadZip(BugReport.Export(Path.Combine(root,"invalid-state.zip"),history,state));
            foreach(var entry in invalidState)AssertPrivateAbsent(Utf8.GetString(entry.Value),"untrusted DTO string values are never echoed");
        }
        File.WriteAllText(Path.Combine(historyRoot,"settings-v3.json"),Sentinels[0],Utf8);
        File.WriteAllText(Path.Combine(historyRoot,"proxy-secret.dpapi"),Sentinels[0],Utf8);
        using(var history=new DiagnosticHistory(historyRoot,true))
        {
            string third=BugReport.Export(Path.Combine(root,"third-report.zip"),history,new BugReportSnapshot{AppVersion="0.8.0",Strategy="split"});
            foreach(var entry in ReadZip(third))AssertPrivateAbsent(Utf8.GetString(entry.Value),"unrelated local files are never collected");
        }
    }
    static void Retention(string root)
    {
        Directory.CreateDirectory(root);string unrelated=Path.Combine(root,"notes.jsonl");File.WriteAllText(unrelated,Sentinels[0],Utf8);
        string latestSession=null;
        for(int session=0;session<20;session++)using(var history=new DiagnosticHistory(root,true)){latestSession=history.SessionId;history.Record("settings_saved",new Dictionary<string,object>{{"count",session}});}
        Check(Directory.GetFiles(root,"session-*.jsonl").Length<=16,"history retains at most sixteen own session files");
        using(var reader=new DiagnosticHistory(root,true))Check(reader.Snapshot().Events.Any(x=>x.SessionId==latestSession),"rapid sessions do not prune the latest session history");
        Check(File.Exists(unrelated)&&File.ReadAllText(unrelated,Utf8)==Sentinels[0],"retention does not remove unrelated local files");
        using(var history=new DiagnosticHistory(root,true))
        {
            for(int index=0;index<5000;index++)history.Record("check_result",new Dictionary<string,object>{{"service","discord"},{"milliseconds",index},{"count",index}});
            var snapshot=history.Snapshot();
            Check(snapshot.Events.Count<=2048,"cross-session snapshot event count is bounded");
            Check(Utf8.GetByteCount(Json.Serialize(snapshot))<=2*1024*1024,"serialized snapshot respects its 2 MiB bound");
            Check(snapshot.Truncated||snapshot.DroppedEvents>0,"history reports truncation when its limits are reached");
            AssertPrivateAbsent(Json.Serialize(snapshot),"retention snapshot ignores unrelated history-like files");
        }
        using(var reader=new DiagnosticHistory(root,true))Check(reader.Snapshot().Events.Any(x=>x.Fields.ContainsKey("count")&&Convert.ToInt32(x.Fields["count"])==4999),"latest events remain persistent after a full file rotates and a new session begins");
        Check(Directory.GetFiles(root,"session-*.jsonl").All(x=>new FileInfo(x).Length<=256*1024),"each persistent session file respects the 256 KiB bound");
        Check(Directory.GetFiles(root,"session-*.jsonl").Length<=16,"rotation also respects the retained file bound");
    }
    static void RecoveredMemoryEvictions(string root)
    {
        using(var history=new DiagnosticHistory(root,true))
        {
            for(int index=0;index<1050;index++)history.Record("window_hidden");
            var snapshot=history.Snapshot();
            Check(history.PersistenceAvailable&&Directory.GetFiles(root,"session-*.jsonl").Length==1,"tiny eviction fixture stays in one available persistent file");
            Check(Directory.GetFiles(root,"session-*.jsonl").All(x=>new FileInfo(x).Length<256*1024),"tiny eviction fixture remains below the file rotation limit");
            Check(snapshot.MemoryEvictions>0,"memory ring reports its evictions separately");
            Check(snapshot.Events.Count==1051,"persisted history restores every event evicted from memory");
            Check(snapshot.DroppedEvents==0,"fully recovered memory evictions are not reported as lost events");
            Check(!snapshot.Truncated,"fully recovered history is not falsely marked truncated");
        }
    }
    static void CorruptInput(string root)
    {
        using(var history=new DiagnosticHistory(root,true))history.Record("filter_stopped",new Dictionary<string,object>{{"exitCode",0}});
        string path=Directory.GetFiles(root,"session-*.jsonl")[0];
        File.AppendAllText(path,"{not-json\n"+"{\"TimestampUtc\":\"2026-10-25T00:00:00Z\",\"SessionId\":\""+Guid.NewGuid().ToString("N")+"\",\"Code\":\""+Sentinels[0]+"\",\"Sequence\":7,\"Fields\":{\"password\":\""+Sentinels[0]+"\",\"strategy\":\""+Sentinels[1]+"\"},\"Error\":{\"Type\":\""+Sentinels[2]+"\",\"HResult\":5,\"Frames\":[{\"Assembly\":\""+Sentinels[3]+"\",\"MethodToken\":1,\"IlOffset\":0}],\"Message\":\""+Sentinels[4]+"\"}}\n"+"{\"truncated\":",Utf8);
        using(var history=new DiagnosticHistory(root,true))
        {
            var snapshot=history.Snapshot();Check(snapshot.Events.Any(x=>x.Code=="filter_stopped"),"valid earlier history survives malformed and truncated lines");
            AssertPrivateAbsent(Json.Serialize(snapshot),"persisted history is re-sanitized before export");
        }
    }
    static void FailedStorage(string root)
    {
        Directory.CreateDirectory(root);string blocker=Path.Combine(root,"not-a-directory");File.WriteAllText(blocker,"fixture",Utf8);
        using(var history=new DiagnosticHistory(Path.Combine(blocker,"history"),true))
        {
            history.Record("session_started");history.RecordException("caught_error",new IOException(Sentinels[0]));
            Check(!history.PersistenceAvailable,"unavailable history storage does not break app logging");
            string report=BugReport.Export(Path.Combine(root,"memory-only-report.zip"),history,new BugReportSnapshot{AppVersion="0.8.0",Strategy="split"});
            Check(File.Exists(report),"bug report still exports when persistent history is unavailable");
            foreach(var entry in ReadZip(report))AssertPrivateAbsent(Utf8.GetString(entry.Value),"memory-only diagnostic fallback");
        }
    }
    static void Reparse(string root)
    {
        Directory.CreateDirectory(root);string target=Path.Combine(root,"target");Directory.CreateDirectory(target);
        string secret=Path.Combine(target,"proxy-secret.dpapi");File.WriteAllText(secret,Sentinels[0],Utf8);string link=Path.Combine(root,"history-link");
        if(!CreateSymbolicLink(link,target,3)&&!CreateSymbolicLink(link,target,1))
        {skipped++;Console.WriteLine("SKIP own directory symlink check: creation unavailable without additional rights");return;}
        int before=Directory.GetFiles(target).Length;bool rejected=false;
        try{using(var history=new DiagnosticHistory(link,true)){history.Record("session_started");Check(!history.PersistenceAvailable,"reparse history directory cannot persist");AssertPrivateAbsent(Json.Serialize(history.Snapshot()),"reparse history is not read");}}
        catch(IOException){rejected=true;}
        Check(rejected||Directory.GetFiles(target).Length==before,"history never writes through own directory symlink");
        using(var history=DiagnosticHistory.Disabled())Expect<IOException>(delegate{BugReport.Export(Path.Combine(link,"forbidden.zip"),history,new BugReportSnapshot{AppVersion="0.8.0",Strategy="split"});},"report export refuses a reparse ancestor");
        Check(!File.Exists(Path.Combine(target,"forbidden.zip")),"reparse refusal leaves external fixture target unchanged");
    }
    static Dictionary<string,byte[]> ReadZip(string path)
    {
        var result=new Dictionary<string,byte[]>(StringComparer.Ordinal);
        using(var zip=ZipFile.OpenRead(path))foreach(var entry in zip.Entries)
        {
            Check(entry.Length<=4*1024*1024,"ZIP entry decompressed size is bounded");
            using(var source=entry.Open())using(var memory=new MemoryStream()){source.CopyTo(memory);result.Add(entry.FullName,memory.ToArray());}
        }
        return result;
    }
    static void ValidateManifest(Dictionary<string,byte[]> entries)
    {
        var manifest=(Dictionary<string,object>)Json.DeserializeObject(Utf8.GetString(entries["manifest.json"]));
        var rows=(IEnumerable)manifest["Files"];var covered=new HashSet<string>(StringComparer.Ordinal);
        foreach(Dictionary<string,object> row in rows)
        {
            string path=(string)row["Path"];Check(path!="manifest.json"&&entries.ContainsKey(path),"manifest covers an existing non-self entry");
            Check(covered.Add(path),"manifest paths are unique");
            Check(Convert.ToInt64(row["Bytes"])==entries[path].LongLength,"manifest byte length matches "+path);
            Check(string.Equals((string)row["SHA256"],Hash(entries[path]),StringComparison.OrdinalIgnoreCase),"manifest SHA256 matches "+path);
        }
        Check(covered.SetEquals(new[]{"SUMMARY.txt","report.json","history.json"}),"manifest covers every payload entry");
    }
    static string Hash(byte[] data){using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(data)).Replace("-","");}
    static void ThrowFixtureException()
    {
        var inner=new InvalidOperationException(Sentinels[4]);var error=new IOException(Sentinels[0]+" C:\\Users\\"+Sentinels[3]+"\\"+Sentinels[1]+" "+Sentinels[2],inner);
        error.Data["password"]=Sentinels[0];throw error;
    }
    static void AssertPrivateAbsent(string text,string label)
    {
        Check(Sentinels.All(value=>text.IndexOf(value,StringComparison.OrdinalIgnoreCase)<0),label+": sentinel secrets, custom endpoint/domain, username and auth header absent");
        string profile=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if(!String.IsNullOrEmpty(profile))Check(text.IndexOf(profile,StringComparison.OrdinalIgnoreCase)<0&&text.IndexOf(profile.Replace("\\","\\\\"),StringComparison.OrdinalIgnoreCase)<0,label+": local user-profile path absent");
    }
    static void Expect<T>(Action action,string label)where T:Exception
    {bool rejected=false;try{action();}catch(T){rejected=true;}Check(rejected,label);}
    static void Check(bool success,string label)
    {if(!success)throw new InvalidOperationException("FAIL: "+label);assertions++;Console.WriteLine("PASS: "+label);}
}
