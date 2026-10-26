using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LocalLink
{
    public sealed class BugReportCheck
    {
        public string ServiceId,Stage,Code;
        public bool? Passed;
        public int Milliseconds;
    }
    public sealed class BugReportSnapshot
    {
        public string AppVersion,Strategy,DiscordStrategy,Operation,TuningJson,Appearance;
        public bool Busy,FilterRunning,IsElevated,EndpointConfigured,DiscordVoice,TelegramNative,Quic,UsePublicLists;
        public int Mode,CustomDomainCount,Dpi,ClientWidth,ClientHeight;
        public List<string> SelectedServices=new List<string>();
        public List<BugReportCheck> Checks=new List<BugReportCheck>();
        public ProfileLaunchEvidence ProfileEvidence;
    }

    public static class BugReport
    {
        const int MaximumChecks=256,MaximumTuningCharacters=65536;
        public static string Export(string destination,DiagnosticHistory history,BugReportSnapshot state)
        {
            if(history==null)throw new ArgumentNullException("history");
            if(state==null)throw new ArgumentNullException("state");
            string path=Path.GetFullPath(destination);
            if(Path.GetFileName(path).IndexOfAny(Path.GetInvalidFileNameChars())>=0)throw new IOException("An ordinary report filename is required.");
            if(!String.Equals(Path.GetExtension(path),".zip",StringComparison.OrdinalIgnoreCase))throw new IOException("The bug report must use a .zip extension.");
            string directory=DiagnosticFiles.DirectoryPath(Path.GetDirectoryName(path),false);
            if(File.Exists(path)||Directory.Exists(path))throw new IOException("The report destination already exists.");
            DiagnosticHistorySnapshot captured=history.Snapshot();
            string version=VersionText(state.AppVersion);
            var checks=new List<object>();
            if(state.Checks!=null)foreach(var check in state.Checks.Take(MaximumChecks))
                if(check!=null)checks.Add(new {ServiceId=DiagnosticPolicy.Service(check.ServiceId),Stage=DiagnosticPolicy.ProbeKey(check.Stage),Code=DiagnosticPolicy.ProbeCode(check.Code),check.Passed,Milliseconds=Math.Max(0,Math.Min(86400000,check.Milliseconds))});
            object profile=SafeProfile(state.ProfileEvidence);
            var report=new {
                Schema="locallink-bug-report-v1",CreatedUtc=DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture),
                App=new {Version=version,AssemblyVersion=Assembly.GetExecutingAssembly().GetName().Version.ToString(),AssemblySHA256=AssemblyHash(),
                    WindowsVersion=Environment.OSVersion.Version.ToString(),ClrVersion=Environment.Version.ToString(),Process64Bit=Environment.Is64BitProcess,Os64Bit=Environment.Is64BitOperatingSystem},
                State=new {Operation=DiagnosticPolicy.Operation(state.Operation),state.Busy,state.FilterRunning,state.IsElevated,
                    Appearance=DiagnosticPolicy.Appearance(state.Appearance),Dpi=Math.Max(48,Math.Min(768,state.Dpi)),ClientWidth=Math.Max(0,Math.Min(32768,state.ClientWidth)),ClientHeight=Math.Max(0,Math.Min(32768,state.ClientHeight)),
                    Mode=Math.Max(0,Math.Min(2,state.Mode)),Strategy=DiagnosticPolicy.Method(state.Strategy),DiscordStrategy=DiagnosticPolicy.Method(state.DiscordStrategy),
                    state.EndpointConfigured,state.DiscordVoice,state.TelegramNative,state.Quic,state.UsePublicLists,
                    CustomDomainCount=Math.Max(0,Math.Min(2000000,state.CustomDomainCount)),SelectedServices=DiagnosticPolicy.ServiceList(state.SelectedServices)},
                Checks=checks,LastTuning=SafeTuning(state.TuningJson),ProfileEvidence=profile,
                History=new {captured.PersistenceStatus,captured.SessionCount,EventCount=captured.Events.Count,captured.Truncated,captured.DroppedEvents,captured.MemoryEvictions,captured.ReadFailures,
                    CounterMeaning="DroppedEvents counts known events omitted by export caps after disk/memory merge. MemoryEvictions counts events removed from the in-memory ring; they may still be recovered from disk. ReadFailures and rotation indicate other possible history gaps without claiming an exact lost-event count."},
                Privacy=new[]{"Manual local export; no automatic upload.","No traffic bodies, messages, passwords, tokens, proxy secrets, device identifiers, user paths, raw settings or raw exception messages.","Only fixed event labels, numeric errors and sanitized diagnostic results are included."},
                Limits=new {MaximumHistoryFiles=DiagnosticHistory.MaximumSessionFiles,MaximumHistoryFileBytes=DiagnosticHistory.MaximumFileBytes,
                    MaximumHistoryEvents=DiagnosticHistory.MaximumSnapshotEvents,MaximumHistoryBytes=DiagnosticHistory.MaximumSnapshotBytes,MaximumChecks=MaximumChecks,
                    HistoryCoverage="Recorded only from LocalLink 0.8.0 onward. Earlier activity is not reconstructed. Bounded retention can omit older events; a missing session_ended is not proof of a crash.",
                    NetworkEvidence="No probes are run by export. Results may be partial or historical. Filter/process state and website responses do not prove successful packet delivery, voice, messaging, video or games.",
                    Exceptions="Stack frames contain assembly names and numeric metadata tokens/IL offsets, without source paths or raw messages. Some fatal failures cannot be logged."}
            };
            var contents=new Dictionary<string,byte[]>(StringComparer.Ordinal) {
                {"SUMMARY.txt",DiagnosticFiles.Utf8.GetBytes(Summary(version,captured,state.FilterRunning,checks.Count))},
                {"report.json",DiagnosticFiles.Utf8.GetBytes(DiagnosticFiles.Json(report))},
                {"history.json",DiagnosticFiles.Utf8.GetBytes(DiagnosticFiles.Json(captured))}
            };
            var manifest=new List<object>();
            foreach(var item in contents)manifest.Add(new {Path=item.Key,SHA256=Hash(item.Value),Bytes=item.Value.Length});
            contents.Add("manifest.json",DiagnosticFiles.Utf8.GetBytes(DiagnosticFiles.Json(new {Schema="locallink-bug-report-manifest-v1",Files=manifest})));
            string temporary=Path.Combine(directory,".locallink-report-"+Guid.NewGuid().ToString("N")+".tmp");
            bool committed=false;
            try
            {
                DiagnosticFiles.DirectoryPath(directory,false);
                using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None))
                {
                    using(var archive=new ZipArchive(stream,ZipArchiveMode.Create,true))
                        foreach(var item in contents)
                        {
                            ZipArchiveEntry entry=archive.CreateEntry(item.Key,CompressionLevel.Optimal);
                            using(var output=entry.Open())output.Write(item.Value,0,item.Value.Length);
                        }
                    stream.Flush(true);
                }
                DiagnosticFiles.DirectoryPath(directory,false);DiagnosticFiles.OrdinaryFile(temporary);
                // Move is atomic on the same local volume and never replaces a user's file.
                File.Move(temporary,path);committed=true;return path;
            }
            finally
            {
                if(!committed)try{if(File.Exists(temporary)){DiagnosticFiles.OrdinaryFile(temporary);File.Delete(temporary);}}catch(Exception){}
            }
        }
        static string VersionText(string value)
        {return DiagnosticPolicy.Version(value);}
        static string AssemblyHash()
        {
            try
            {
                string path=Assembly.GetExecutingAssembly().Location;DiagnosticFiles.OrdinaryFile(path);
                using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
                using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();
            }
            catch(Exception){return "unavailable";}
        }
        static string Hash(byte[] bytes)
        {using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
        static object SafeProfile(ProfileLaunchEvidence evidence)
        {
            if(evidence==null||evidence.Profile==null)return null;
            try
            {
                var profile=ProfileSummary.FromFields(evidence.Profile.Fields());
                string status=evidence.Status=="running"||evidence.Status=="stopped"||evidence.Status=="unexpected-exit"?evidence.Status:"unknown";
                string receipt=evidence.ReceiptStatus=="saved"||evidence.ReceiptStatus=="unavailable"?evidence.ReceiptStatus:"unknown";
                return new {Status=status,ReceiptStatus=receipt,Profile=profile.Fields(),Meaning="Last owned engine launch, distinct from current UI selections. Historical/stopped profiles are not current traffic evidence."};
            }
            catch(Exception){return null;}
        }
        static string Summary(string version,DiagnosticHistorySnapshot history,bool filterRunning,int checks)
        {
            return "LocalLink — баг-репорт\r\nВерсия: "+version+"\r\nСоздан UTC: "+DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture)+
                "\r\nФильтр на момент снимка: "+(filterRunning?"запущен":"не запущен")+"\r\nПроверок в снимке: "+checks.ToString(CultureInfo.InvariantCulture)+
                "\r\nСобытий: "+history.Events.Count.ToString(CultureInfo.InvariantCulture)+"; сессий: "+history.SessionCount.ToString(CultureInfo.InvariantCulture)+
                "\r\nХранилище истории: "+history.PersistenceStatus+"\r\nИстория ограничена: "+(history.Truncated?"да":"нет")+
                "\r\n\r\nОтправьте этот ZIP целиком вместе с описанием проблемы, временем и ожидаемым результатом.\r\n"+
                "report.json — версия, безопасные параметры, результаты проверок и последний профиль.\r\nhistory.json — ограниченная история событий LocalLink между запусками.\r\nmanifest.json — размеры и SHA-256 файлов архива.\r\n\r\n"+
                "Экспорт не запускает сетевые проверки, не меняет фильтр и ничего не отправляет автоматически.\r\n"+
                "Нет переписки, паролей, ключей, содержимого трафика, имён ПК, адресов пользователя, полных путей и сырых сообщений исключений.\r\n"+
                "История начинается с версии 0.8.0; старые версии её не записывали. Старые события могут быть вытеснены лимитами.\r\n"+
                "Если после session_started нет session_ended, сессия могла ещё работать или завершиться без записи — это не доказательство краша.\r\n"+
                "При недоступном хранилище остаются события текущего запуска в памяти. Результаты проверок могут быть неполными или историческими.\r\n";
        }
        static object SafeTuning(string json)
        {
            if(String.IsNullOrEmpty(json)||json.Length>MaximumTuningCharacters)return null;
            try
            {
                var source=DiagnosticFiles.Parser(MaximumTuningCharacters).DeserializeObject(json) as Dictionary<string,object>;
                if(source==null||Text(source,"Schema")!="locallink-tls-tuning-v1")return null;
                var result=new Dictionary<string,object>(StringComparer.Ordinal){{"Schema","locallink-tls-tuning-v1"}};
                string scope=Text(source,"Scope");result["Scope"]=scope=="discord"?"discord":"general";
                result["Strategy"]=DiagnosticPolicy.Method(Text(source,"Strategy"));
                DateTime exported;if(DateTime.TryParseExact(Text(source,"ExportedUtc"),"o",CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out exported))result["ExportedUtc"]=exported.ToUniversalTime().ToString("o",CultureInfo.InvariantCulture);
                foreach(string key in new[]{"Enabled","Verified","Cancelled","TimedOut"})result[key]=Flag(source,key);
                foreach(string key in new[]{"BaselinePassed","BaselineFailed","BaselineUnknown"})result[key]=Count(source,key);
                result["BaselineServices"]=SafeServices(Get(source,"BaselineServices"));result["BaselineProbes"]=SafeProbes(Get(source,"BaselineProbes"));
                var trials=new List<object>();var values=Get(source,"Trials") as IEnumerable;
                if(values!=null&&!(values is string))foreach(object item in values)
                {
                    if(trials.Count>=12)break;var row=item as Dictionary<string,object>;if(row==null)continue;
                    trials.Add(new {Strategy=DiagnosticPolicy.Method(Text(row,"Strategy")),Stage=Text(row,"Stage")=="verification"?"verification":"trial",
                        Passed=Count(row,"Passed"),Failed=Count(row,"Failed"),Unknown=Count(row,"Unknown"),MatrixValid=Flag(row,"MatrixValid"),PreservedBaseline=Flag(row,"PreservedBaseline"),
                        Improved=Flag(row,"Improved"),Verified=Flag(row,"Verified"),Services=SafeServices(Get(row,"Services")),Probes=SafeProbes(Get(row,"Probes"))});
                }
                result["Trials"]=trials;return result;
            }
            catch(Exception){return null;}
        }
        static object Get(Dictionary<string,object> value,string key){object item;return value.TryGetValue(key,out item)?item:null;}
        static string Text(Dictionary<string,object> value,string key){return Get(value,key) as string;}
        static bool Flag(Dictionary<string,object> value,string key){object item=Get(value,key);return item is bool&&(bool)item;}
        static int Count(Dictionary<string,object> value,string key){return DiagnosticPolicy.Number(Get(value,key),0,512);}
        static List<object> SafeServices(object input)
        {
            var result=new List<object>();var rows=input as IEnumerable;var seen=new HashSet<string>(StringComparer.Ordinal);if(rows==null||rows is string)return result;
            foreach(object item in rows)
            {
                if(result.Count>=7)break;var row=item as Dictionary<string,object>;if(row==null)continue;
                string service=DiagnosticPolicy.Service(Text(row,"ServiceId"));if(service=="unknown"||!seen.Add(service))continue;
                result.Add(new {ServiceId=service,Passed=Count(row,"Passed"),Failed=Count(row,"Failed"),Unknown=Count(row,"Unknown")});
            }
            return result;
        }
        static List<object> SafeProbes(object input)
        {
            var result=new List<object>();var rows=input as IEnumerable;var seen=new HashSet<string>(StringComparer.Ordinal);if(rows==null||rows is string)return result;
            foreach(object item in rows)
            {
                if(result.Count>=64)break;var row=item as Dictionary<string,object>;if(row==null)continue;
                string key=DiagnosticPolicy.ProbeKey(Text(row,"Key"));if(key=="unknown"||!seen.Add(key))continue;
                object passed=Get(row,"Passed");if(passed!=null&&!(passed is bool))passed=null;
                result.Add(new {Key=key,Code=DiagnosticPolicy.ProbeCode(Text(row,"Code")),Passed=passed,Milliseconds=DiagnosticPolicy.Number(Get(row,"Milliseconds"),0,32000)});
            }
            return result;
        }
    }
}
