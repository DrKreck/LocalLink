using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace LocalLink
{
    public sealed class DiagnosticFrame
    {
        public string Assembly;
        public int MethodToken,IlOffset;
    }
    public sealed class DiagnosticExceptionInfo
    {
        public string Type;
        public int HResult;
        public int? NativeCode;
        public List<DiagnosticFrame> Frames=new List<DiagnosticFrame>();
        public List<DiagnosticExceptionInfo> Inner=new List<DiagnosticExceptionInfo>();
    }
    public sealed class DiagnosticEvent
    {
        public string TimestampUtc,SessionId,Code;
        public long Sequence;
        public Dictionary<string,object> Fields=new Dictionary<string,object>(StringComparer.Ordinal);
        public DiagnosticExceptionInfo Error;
    }
    public sealed class DiagnosticHistorySnapshot
    {
        public string Schema="locallink-diagnostic-history-v1",PersistenceStatus;
        public List<DiagnosticEvent> Events=new List<DiagnosticEvent>();
        public bool Truncated;
        public int DroppedEvents,MemoryEvictions,SessionCount,ReadFailures;
    }

    public sealed class DiagnosticHistory:IDisposable
    {
        public const int MaximumSessionFiles=16,MaximumFileBytes=256*1024,MaximumMemoryEvents=1024;
        public const int MaximumSnapshotEvents=2048,MaximumSnapshotBytes=2*1024*1024;
        readonly object gate=new object();
        readonly string directory;
        readonly bool enabled;
        readonly List<DiagnosticEvent> memory=new List<DiagnosticEvent>();
        string file,status;
        long sequence;
        int dropped;
        bool disposed,fileFull;
        public string SessionId {get;private set;}
        public bool PersistenceAvailable {get{lock(gate)return enabled&&status=="available";}}
        public string StorageStatus {get{lock(gate)return status;}}

        public static DiagnosticHistory CreateDefault(bool enabled)
        {
            if(!enabled)return Disabled();
            try
            {
                string local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return new DiagnosticHistory(String.IsNullOrEmpty(local)?null:Path.Combine(local,"LocalLink","diagnostics"),true);
            }
            catch(Exception){return new DiagnosticHistory(null,true);}
        }
        public static DiagnosticHistory Disabled(){return new DiagnosticHistory(null,false);}
        public DiagnosticHistory(string directory,bool enabled)
        {
            this.enabled=enabled;SessionId=Guid.NewGuid().ToString("N");status=enabled?"unavailable":"disabled";
            if(enabled)
            {
                try
                {
                    this.directory=DiagnosticFiles.DirectoryPath(directory,true);
                    StartFile();
                    status="available";
                }
                catch(Exception){status="unavailable";}
                Record("session_started",new Dictionary<string,object>{{"appVersion",Assembly.GetExecutingAssembly().GetName().Version.ToString()}});
            }
        }
        public void Record(string code){Record(code,null);}
        public void Record(string code,IDictionary<string,object> fields)
        {Write(code,fields,null);}
        public void RecordException(string code,Exception error)
        {RecordException(code,error,null);}
        public void RecordException(string code,Exception error,IDictionary<string,object> fields)
        {Write(code,fields,DiagnosticPolicy.ExceptionInfo(error,0));}
        void Write(string code,IDictionary<string,object> fields,DiagnosticExceptionInfo error)
        {
            lock(gate)
            {
                if(!enabled||disposed)return;
                var row=new DiagnosticEvent{TimestampUtc=DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture),SessionId=SessionId,
                    Sequence=++sequence,Code=DiagnosticPolicy.EventCode(code),Fields=DiagnosticPolicy.Fields(fields),Error=error};
                memory.Add(row);if(memory.Count>MaximumMemoryEvents){memory.RemoveAt(0);dropped++;}
                if(status!="available")return;
                try
                {
                    DiagnosticFiles.DirectoryPath(directory,false);DiagnosticFiles.OrdinaryFile(file);
                    byte[] bytes=DiagnosticFiles.Utf8.GetBytes(DiagnosticFiles.Json(row)+"\n");
                    if(bytes.Length>MaximumFileBytes){fileFull=true;return;}
                    if(new FileInfo(file).Length+bytes.Length>MaximumFileBytes){fileFull=true;StartFile();}
                    using(var stream=new FileStream(file,FileMode.Open,FileAccess.Write,FileShare.Read))
                    {
                        if(stream.Length+bytes.Length>MaximumFileBytes)throw new IOException("Diagnostic history file changed while writing.");
                        stream.Position=stream.Length;stream.Write(bytes,0,bytes.Length);stream.Flush(true);
                    }
                }
                catch(Exception){status="unavailable";}
            }
        }
        void StartFile()
        {
            DiagnosticFiles.DirectoryPath(directory,false);Prune();
            string next=Path.Combine(directory,"session-"+DateTime.UtcNow.ToString("yyyyMMddHHmmss",CultureInfo.InvariantCulture)+"-"+(file==null?SessionId:Guid.NewGuid().ToString("N"))+".jsonl");
            using(var stream=new FileStream(next,FileMode.CreateNew,FileAccess.Write,FileShare.Read))stream.Flush(true);
            file=next;
        }
        void Prune()
        {
            var files=HistoryFiles(directory);
            for(int i=0;i<Math.Max(0,files.Count-(MaximumSessionFiles-1));i++)
            {DiagnosticFiles.OrdinaryFile(files[i]);File.Delete(files[i]);}
        }
        static List<string> HistoryFiles(string root)
        {
            var result=new List<string>();int seen=0;
            foreach(string path in Directory.EnumerateFiles(root,"session-*.jsonl",SearchOption.TopDirectoryOnly))
            {
                if(++seen>4096)throw new IOException("Diagnostic history directory limit exceeded.");
                if(Regex.IsMatch(Path.GetFileName(path),@"\Asession-[0-9]{14}-[0-9a-f]{32}\.jsonl\z",RegexOptions.CultureInvariant))result.Add(path);
            }
            return result.OrderBy(x=>File.GetLastWriteTimeUtc(x)).ThenBy(x=>x,StringComparer.Ordinal).ToList();
        }
        public DiagnosticHistorySnapshot Snapshot()
        {
            lock(gate)
            {
                var snapshot=new DiagnosticHistorySnapshot{PersistenceStatus=status,MemoryEvictions=dropped,Truncated=fileFull||(status!="available"&&dropped>0)};
                var events=new Dictionary<string,DiagnosticEvent>(StringComparer.Ordinal);int consumed=0;
                if(enabled&&directory!=null)
                {
                    try
                    {
                        DiagnosticFiles.DirectoryPath(directory,false);var files=HistoryFiles(directory);
                        if(files.Count>MaximumSessionFiles){snapshot.Truncated=true;files=files.Skip(files.Count-MaximumSessionFiles).ToList();}
                        files.Reverse();
                        foreach(string path in files)
                        {
                            try
                            {
                                DiagnosticFiles.OrdinaryFile(path);long length=new FileInfo(path).Length;
                                if(length>MaximumFileBytes||length>MaximumSnapshotBytes-consumed){snapshot.Truncated=true;continue;}
                                string text=DiagnosticFiles.ReadUtf8(path,MaximumFileBytes);consumed+=DiagnosticFiles.Utf8.GetByteCount(text);
                                foreach(string line in text.Split('\n'))
                                {
                                    if(String.IsNullOrWhiteSpace(line))continue;
                                    try
                                    {
                                        if(line.Length>16384){snapshot.ReadFailures++;continue;}
                                        var raw=DiagnosticFiles.Parser(16384).Deserialize<DiagnosticEvent>(line);
                                        DiagnosticEvent row=DiagnosticPolicy.Event(raw);
                                        if(row==null){snapshot.ReadFailures++;continue;}
                                        events[row.SessionId+":"+row.Sequence.ToString(CultureInfo.InvariantCulture)]=row;
                                    }
                                    catch(Exception){snapshot.ReadFailures++;}
                                }
                            }
                            catch(Exception){snapshot.ReadFailures++;}
                        }
                    }
                    catch(Exception){snapshot.ReadFailures++;snapshot.PersistenceStatus=enabled?"unavailable":"disabled";}
                }
                foreach(var raw in memory)
                {var row=DiagnosticPolicy.Event(raw);if(row!=null)events[row.SessionId+":"+row.Sequence.ToString(CultureInfo.InvariantCulture)]=row;}
                if(snapshot.ReadFailures>0)snapshot.Truncated=true;
                var ordered=events.Values.OrderBy(x=>x.TimestampUtc,StringComparer.Ordinal).ThenBy(x=>x.SessionId,StringComparer.Ordinal).ThenBy(x=>x.Sequence).ToList();
                if(ordered.Count>MaximumSnapshotEvents){snapshot.DroppedEvents+=ordered.Count-MaximumSnapshotEvents;snapshot.Truncated=true;ordered=ordered.Skip(ordered.Count-MaximumSnapshotEvents).ToList();}
                int budget=MaximumSnapshotBytes-1024;var bounded=new List<DiagnosticEvent>();
                for(int index=ordered.Count-1;index>=0;index--)
                {
                    int bytes=DiagnosticFiles.Utf8.GetByteCount(DiagnosticFiles.Json(ordered[index]))+2;
                    if(bytes>budget){snapshot.DroppedEvents+=index+1;snapshot.Truncated=true;break;}
                    bounded.Add(ordered[index]);budget-=bytes;
                }
                bounded.Reverse();ordered=bounded;
                snapshot.Events=ordered;snapshot.SessionCount=ordered.Select(x=>x.SessionId).Distinct(StringComparer.Ordinal).Count();
                return snapshot;
            }
        }
        public void Dispose()
        {
            lock(gate){if(disposed)return;Record("session_ended");disposed=true;}
        }
    }

    internal static class DiagnosticFiles
    {
        internal static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        internal static JavaScriptSerializer Parser(int maximum){return new JavaScriptSerializer{MaxJsonLength=maximum,RecursionLimit=32};}
        internal static string Json(object value){return Parser(4*1024*1024).Serialize(value);}
        internal static string DirectoryPath(string path,bool create)
        {
            if(String.IsNullOrWhiteSpace(path))throw new IOException("An ordinary local diagnostic directory is required.");
            string full=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            if(full.StartsWith(@"\\",StringComparison.Ordinal)||full.Length<4)throw new IOException("An ordinary local diagnostic directory is required.");
            string cursor=full;
            while(!String.IsNullOrEmpty(cursor))
            {
                if(File.Exists(cursor)&&!Directory.Exists(cursor))throw new IOException("A diagnostic directory component is not a directory.");
                if(Directory.Exists(cursor)&&(File.GetAttributes(cursor)&FileAttributes.ReparsePoint)!=0)throw new IOException("Diagnostic reparse points are not allowed.");
                cursor=Path.GetDirectoryName(cursor);
            }
            if(create)Directory.CreateDirectory(full);
            if(!Directory.Exists(full)||(File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)throw new IOException("Diagnostic directory unavailable.");
            return full;
        }
        internal static void OrdinaryFile(string path)
        {
            DirectoryPath(Path.GetDirectoryName(Path.GetFullPath(path)),false);
            FileAttributes attributes=File.GetAttributes(path);
            if((attributes&(FileAttributes.ReparsePoint|FileAttributes.Directory))!=0)throw new IOException("An ordinary diagnostic file is required.");
        }
        internal static string ReadUtf8(string path,int maximum)
        {
            OrdinaryFile(path);
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))
            {
                if(stream.Length>maximum)throw new IOException("Diagnostic file limit exceeded.");
                byte[] bytes=new byte[(int)stream.Length];int at=0,count;
                while(at<bytes.Length&&(count=stream.Read(bytes,at,bytes.Length-at))>0)at+=count;
                if(at!=bytes.Length||stream.ReadByte()!=-1)throw new IOException("Diagnostic file changed while reading.");
                return Utf8.GetString(bytes);
            }
        }
    }

    internal static class DiagnosticPolicy
    {
        static readonly HashSet<string> FeatureEvents=new HashSet<string>(("network_preset_saved network_preset_forgotten network_preset_applied network_observed system_suspended system_resumed recovery_started recovery_succeeded troubleshooting_opened app_update_check app_update_staged app_rollback_started automatic_setup_started automatic_setup_finished").Split(' '),StringComparer.Ordinal);
        static readonly HashSet<string> Events=new HashSet<string>(("session_started session_ended settings_opened settings_closed settings_saved settings_snapshot theme_changed operation_started operation_finished operation_cancelled filter_start_requested filter_prepared filter_started filter_stop_requested filter_stopped filter_unexpected_exit check_result checks_started checks_finished checks_reset tuning_started tuning_finished lists_update_started lists_update_finished startup_changed window_hidden window_restored elevation_requested elevation_cancelled telegram_open_requested telegram_opened report_requested report_created report_cancelled report_failed caught_error unhandled_ui unhandled_domain unobserved_task support_open_requested support_opened support_open_failed").Split(' '),StringComparer.Ordinal);
        static readonly HashSet<string> Operations=new HashSet<string>(("none start stop check tune discord_tune lists startup settings theme discover elevate telegram report contact close dispose recovery network_profile app_update troubleshooting automatic preset_load preset_save").Split(' '),StringComparer.Ordinal);
        static readonly HashSet<string> Services=new HashSet<string>(("control discord youtube telegram steam arma tarkov dcs").Split(' '),StringComparer.Ordinal);
        static readonly HashSet<string> Methods=new HashSet<string>(("split sni-split disorder fake-split fake-disorder wireguard unrecognized unknown").Split(' '),StringComparer.Ordinal);
        static readonly HashSet<string> ExceptionTypes=new HashSet<string>(("System.Exception System.IO.IOException System.IO.FileNotFoundException System.IO.DirectoryNotFoundException System.IO.PathTooLongException System.IO.InvalidDataException System.UnauthorizedAccessException System.Security.SecurityException System.ArgumentException System.ArgumentNullException System.ArgumentOutOfRangeException System.InvalidOperationException System.ObjectDisposedException System.OperationCanceledException System.Threading.Tasks.TaskCanceledException System.TimeoutException System.OutOfMemoryException System.NotSupportedException System.FormatException System.OverflowException System.Net.Sockets.SocketException System.Net.WebException System.Security.Authentication.AuthenticationException System.ComponentModel.Win32Exception System.AggregateException System.NullReferenceException System.IndexOutOfRangeException System.Runtime.InteropServices.ExternalException System.Runtime.InteropServices.COMException").Split(' '),StringComparer.Ordinal);
        internal static string EventCode(string value){return value!=null&&(Events.Contains(value)||FeatureEvents.Contains(value))?value:"unknown_event";}
        internal static string Operation(string value){return value!=null&&Operations.Contains(value)?value:"unknown";}
        internal static string Method(string value){return String.IsNullOrEmpty(value)?"":Methods.Contains(value)?value:"unknown";}
        internal static string Appearance(string value){return value=="system"||value=="light"||value=="dark"?value:"unknown";}
        internal static string Version(string value){return value!=null&&Regex.IsMatch(value,@"\A[0-9]{1,4}(\.[0-9]{1,4}){1,3}\z",RegexOptions.CultureInvariant)?value:"unknown";}
        internal static string Service(string value){return value!=null&&Services.Contains(value)?value:"unknown";}
        internal static string ProbeKey(string value)
        {
            if(String.IsNullOrEmpty(value))return "unknown";
            if(Tuning.KnownProbeKey(value))return value;
            foreach(string service in Services)
                foreach(string key in new[]{"web.tls","web.http","community.tls","community.http","voice","screen","playback","mtproto","client","game","dc1","dc2","dc3","microsoft.tls","microsoft.http","cloudflare.tls","cloudflare.http"})
                    if(value==service+"."+key)return value;
            return "unknown";
        }
        internal static string ProbeCode(string value){return Tuning.KnownProbeCode(value)?value:"unknown";}
        internal static int Number(object value,int minimum,int maximum)
        {
            if(!(value is byte)&&!(value is short)&&!(value is int)&&!(value is long))return minimum;
            long number=Convert.ToInt64(value,CultureInfo.InvariantCulture);return (int)Math.Max(minimum,Math.Min(maximum,number));
        }
        internal static Dictionary<string,object> Fields(IDictionary<string,object> raw)
        {
            var output=new Dictionary<string,object>(StringComparer.Ordinal);if(raw==null)return output;
            foreach(var pair in raw)
            {
                string key=pair.Key;object value=pair.Value;
                switch(key)
                {
                    case "operation":output[key]=Operation(value as string);break;
                    case "appearance":output[key]=Appearance(value as string);break;
                    case "appVersion":output[key]=Version(value as string);break;
                    case "strategy":case "discordStrategy":output[key]=Method(value as string);break;
                    case "service":output[key]=Service(value as string);break;
                    case "stage":output[key]=ProbeKey(value as string);break;
                    case "code":output[key]=ProbeCode(value as string);break;
                    case "mode":output[key]=Number(value,0,2);break;
                    case "milliseconds":output[key]=Number(value,0,24*60*60*1000);break;
                    case "count":case "customDomainCount":output[key]=Number(value,0,2000000);break;
                    case "exitCode":output[key]=value==null?null:(object)Number(value,Int32.MinValue,Int32.MaxValue);break;
                    case "passed":if(value==null||value is bool)output[key]=value;break;
                    case "enabled":case "busy":case "filterRunning":case "endpointConfigured":case "publicLists":case "discordVoice":case "telegramNative":case "quic":
                        if(value is bool)output[key]=value;break;
                    case "services":output[key]=ServiceList(value as IEnumerable);break;
                }
            }
            return output;
        }
        internal static List<string> ServiceList(IEnumerable values)
        {
            var output=new List<string>();if(values==null||values is string)return output;int count=0;
            foreach(object value in values){if(++count>32)break;string service=Service(value as string);if(service!="unknown"&&!output.Contains(service))output.Add(service);}
            return output;
        }
        internal static DiagnosticEvent Event(DiagnosticEvent raw)
        {
            if(raw==null||raw.Sequence<1||raw.Sequence>1000000000L||raw.SessionId==null||!Regex.IsMatch(raw.SessionId,@"\A[0-9a-f]{32}\z",RegexOptions.CultureInvariant))return null;
            DateTime timestamp;if(!DateTime.TryParseExact(raw.TimestampUtc,"o",CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out timestamp))return null;
            return new DiagnosticEvent{TimestampUtc=timestamp.ToUniversalTime().ToString("o",CultureInfo.InvariantCulture),SessionId=raw.SessionId,Sequence=raw.Sequence,
                Code=EventCode(raw.Code),Fields=Fields(raw.Fields),Error=CleanException(raw.Error,0)};
        }
        internal static DiagnosticExceptionInfo ExceptionInfo(Exception error,int depth)
        {
            if(error==null||depth>2)return null;
            var native=error as System.ComponentModel.Win32Exception;
            var result=new DiagnosticExceptionInfo{Type=ExceptionType(error.GetType().FullName),HResult=error.HResult,NativeCode=native==null?(int?)null:native.NativeErrorCode};
            try
            {
                var frames=new StackTrace(error,false).GetFrames();
                if(frames!=null)foreach(var frame in frames.Take(16))
                {
                    MethodBase method=frame.GetMethod();if(method==null)continue;
                    string assembly=AssemblyName(method.Module.Assembly.GetName().Name);
                    if(assembly=="other")continue;
                    int token=0;try{token=method.MetadataToken;}catch(InvalidOperationException){}
                    result.Frames.Add(new DiagnosticFrame{Assembly=assembly,MethodToken=token,IlOffset=Math.Max(-1,frame.GetILOffset())});
                }
            }
            catch(Exception){}
            var aggregate=error as AggregateException;
            if(aggregate!=null)foreach(var child in aggregate.InnerExceptions.Take(2)){var item=ExceptionInfo(child,depth+1);if(item!=null)result.Inner.Add(item);}
            else if(error.InnerException!=null){var item=ExceptionInfo(error.InnerException,depth+1);if(item!=null)result.Inner.Add(item);}
            return CleanException(result,depth);
        }
        static string ExceptionType(string value){return value!=null&&ExceptionTypes.Contains(value)?value:value!=null&&value.StartsWith("LocalLink.",StringComparison.Ordinal)?"LocalLink.Exception":"System.Exception";}
        static string AssemblyName(string value)
        {foreach(string known in new[]{"LocalLink","LocalLink.Telegram","mscorlib","System","System.Core","System.Drawing","System.Windows.Forms","System.Net.Http","System.Web.Extensions","System.Security","System.Management"})if(value==known)return known;return "other";}
        static DiagnosticExceptionInfo CleanException(DiagnosticExceptionInfo raw,int depth)
        {
            if(raw==null||depth>2)return null;
            var result=new DiagnosticExceptionInfo{Type=ExceptionType(raw.Type),HResult=raw.HResult,NativeCode=raw.NativeCode};
            if(raw.Frames!=null)foreach(var frame in raw.Frames.Take(16))
                if(frame!=null&&AssemblyName(frame.Assembly)!="other"&&(frame.MethodToken==0||(frame.MethodToken&unchecked((int)0xff000000))==0x06000000))
                    result.Frames.Add(new DiagnosticFrame{Assembly=AssemblyName(frame.Assembly),MethodToken=frame.MethodToken,IlOffset=Math.Max(-1,Math.Min(10000000,frame.IlOffset))});
            if(raw.Inner!=null)foreach(var child in raw.Inner.Take(2)){var item=CleanException(child,depth+1);if(item!=null)result.Inner.Add(item);}
            return result;
        }
    }
}
