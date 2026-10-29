using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LocalLink
{
    public sealed class TroubleshootingCase
    {
        public string Id { get; private set; }
        public string ServiceId { get; private set; }
        public string Name { get; private set; }
        public string Instruction { get; private set; }
        internal TroubleshootingCase(string id,string service,string name,string instruction)
        { Id=id;ServiceId=service;Name=name;Instruction=instruction; }
        public override string ToString() { return Name; }
    }

    public sealed class TroubleshootingState
    {
        public bool FilterRunning;
        public string Strategy,DiscordStrategy;
        public int Mode;
        internal TroubleshootingState SafeCopy()
        { return new TroubleshootingState { FilterRunning=FilterRunning,Strategy=DiagnosticPolicy.Method(Strategy),DiscordStrategy=DiagnosticPolicy.Method(DiscordStrategy),Mode=Math.Max(0,Math.Min(2,Mode)) }; }
    }

    public sealed class TroubleshootingProbe
    {
        public string Key,Code;
        public bool? Passed;
        public long Milliseconds;
    }

    public sealed class TroubleshootingSession
    {
        public const int MaximumProbes=96;
        public const int MaximumDurationMinutes=20;
        public string Id { get; private set; }
        public TroubleshootingCase Case { get; private set; }
        public DateTime StartedUtc { get; private set; }
        public DateTime? ReproduceStartedUtc { get; private set; }
        public DateTime? EndedUtc { get; private set; }
        public string Outcome { get; private set; }
        public bool BaselineTimedOut { get; private set; }
        public bool FinalTimedOut { get; private set; }
        public bool EnvironmentChanged { get; private set; }
        public TroubleshootingState Before { get; private set; }
        public TroubleshootingState After { get; private set; }
        readonly List<TroubleshootingProbe> baseline=new List<TroubleshootingProbe>();
        readonly List<TroubleshootingProbe> final=new List<TroubleshootingProbe>();
        public IList<TroubleshootingProbe> Baseline { get { return CopyProbes(baseline).AsReadOnly(); } }
        public IList<TroubleshootingProbe> Final { get { return CopyProbes(final).AsReadOnly(); } }

        public TroubleshootingSession(string caseId,TroubleshootingState state):this(caseId,state,DateTime.UtcNow) { }
        internal TroubleshootingSession(string caseId,TroubleshootingState state,DateTime now)
        {
            Case=Troubleshooting.Case(caseId);Id=Guid.NewGuid().ToString("N");StartedUtc=Utc(now);
            Before=(state??new TroubleshootingState()).SafeCopy();Outcome="not-checked";
        }
        public void SetBaseline(IEnumerable<CheckRow> rows,bool timedOut)
        {
            if(ReproduceStartedUtc.HasValue||EndedUtc.HasValue)throw new InvalidOperationException("Начальная проверка уже завершена.");
            baseline.Clear();baseline.AddRange(Troubleshooting.SafeProbes(Case.ServiceId,rows));BaselineTimedOut=timedOut;
        }
        public void BeginReproduction() { BeginReproduction(DateTime.UtcNow); }
        internal void BeginReproduction(DateTime now)
        {
            if(ReproduceStartedUtc.HasValue||EndedUtc.HasValue)throw new InvalidOperationException("Проверка уже начата.");
            ReproduceStartedUtc=BoundTime(now);
        }
        public void Finish(string outcome,IEnumerable<CheckRow> rows,bool timedOut,TroubleshootingState state)
        { Finish(outcome,rows,timedOut,state,DateTime.UtcNow); }
        internal void Finish(string outcome,IEnumerable<CheckRow> rows,bool timedOut,TroubleshootingState state,DateTime now)
        {
            if(EndedUtc.HasValue)throw new InvalidOperationException("Проверка уже завершена.");
            if(outcome!="works"&&outcome!="still-fails"&&outcome!="not-checked"&&outcome!="cancelled")throw new ArgumentException("Неизвестный результат.","outcome");
            if(!ReproduceStartedUtc.HasValue&&outcome!="cancelled")throw new InvalidOperationException("Сначала воспроизведите проблему.");
            final.Clear();final.AddRange(Troubleshooting.SafeProbes(Case.ServiceId,rows));FinalTimedOut=timedOut;
            After=(state??new TroubleshootingState()).SafeCopy();EndedUtc=BoundTime(now);
            if(ReproduceStartedUtc.HasValue&&EndedUtc.Value<ReproduceStartedUtc.Value)EndedUtc=ReproduceStartedUtc;Outcome=outcome;
            EnvironmentChanged=Before.FilterRunning!=After.FilterRunning||Before.Mode!=After.Mode||Before.Strategy!=After.Strategy||Before.DiscordStrategy!=After.DiscordStrategy;
        }
        DateTime BoundTime(DateTime value)
        { DateTime now=Utc(value);if(now<StartedUtc)return StartedUtc;return now>StartedUtc.AddMinutes(MaximumDurationMinutes)?StartedUtc.AddMinutes(MaximumDurationMinutes):now; }
        static DateTime Utc(DateTime value) { return value.Kind==DateTimeKind.Utc?value:value.ToUniversalTime(); }
        public string Summary()
        {
            if(!EndedUtc.HasValue)return "Проверка не завершена.";
            string result=Outcome=="works"?"По вашей проверке сценарий работает.":Outcome=="still-fails"?"По вашей проверке проблема сохранилась.":Outcome=="cancelled"?"Проверка прервана.":"Результат в приложении не указан.";
            if(EnvironmentChanged)result+=" Во время проверки изменился профиль или состояние фильтра; сравнение ограничено.";
            var failures=final.Where(x=>x.Passed==false).ToList();
            if(failures.Any(x=>x.Code=="dns-failed"))result+=" Есть ошибка DNS при проверке публичного узла.";
            else if(failures.Any(x=>x.Code=="certificate-or-tls"))result+=" Есть ошибка проверки TLS/сертификата публичного узла.";
            else if(failures.Any(x=>x.Key.EndsWith(".tcp",StringComparison.Ordinal)))result+=" Не установлено TCP-соединение с одним из публичных узлов.";
            else if(failures.Count>0)result+=" Часть публичных узлов не ответила успешно.";
            else if(final.Any(x=>x.Passed==true))result+=" Проверенные публичные узлы доступны; это не проверка звука, передачи файла или игровой сессии.";
            else result+=" Сетевых данных недостаточно.";
            if(BaselineTimedOut||FinalTimedOut)result+=" Проверка узлов завершилась по лимиту времени.";
            return result;
        }
        internal object Document()
        {
            return new {Schema="locallink-troubleshooting-v1",CaseId=Case.Id,ServiceId=Case.ServiceId,SessionId=Id,
                StartedUtc=StartedUtc.ToString("o",CultureInfo.InvariantCulture),
                ReproduceStartedUtc=ReproduceStartedUtc.HasValue?ReproduceStartedUtc.Value.ToString("o",CultureInfo.InvariantCulture):null,
                EndedUtc=EndedUtc.HasValue?EndedUtc.Value.ToString("o",CultureInfo.InvariantCulture):null,
                Outcome=Outcome,BaselineTimedOut=BaselineTimedOut,FinalTimedOut=FinalTimedOut,EnvironmentChanged=EnvironmentChanged,
                Before=Before.SafeCopy(),After=After==null?null:After.SafeCopy(),Baseline=CopyProbes(baseline),Final=CopyProbes(final),
                Meaning="User-reported scenario result plus bounded public endpoint observations, not a proven root cause. No packet capture or account access. Full LocalLink history is in history.json; timestamps delimit this case."};
        }
        static List<TroubleshootingProbe> CopyProbes(IEnumerable<TroubleshootingProbe> source)
        { return source.Select(x=>new TroubleshootingProbe {Key=x.Key,Code=x.Code,Passed=x.Passed,Milliseconds=x.Milliseconds}).ToList(); }
    }

    public sealed class TroubleshootingRun
    {
        public List<CheckRow> Rows=new List<CheckRow>();
        public bool TimedOut;
    }

    public static class Troubleshooting
    {
        public const int ProbeTimeoutMilliseconds=120000;
        public static List<TroubleshootingCase> Cases()
        {
            return new List<TroubleshootingCase> {
                new TroubleshootingCase("discord-startup","discord","Discord — запуск и загрузка","Откройте Discord. Проверьте, появились ли список каналов и сообщения."),
                new TroubleshootingCase("discord-voice","discord","Discord — голос","Войдите в голосовой канал. Проверьте звук в обе стороны и переподключения в течение 1–2 минут."),
                new TroubleshootingCase("discord-upload","discord","Discord — отправка файлов","Попробуйте отправить небольшую несекретную картинку в Discord. Отметьте, завершилась ли отправка."),
                new TroubleshootingCase("youtube-video","youtube","YouTube — видео","Откройте видео, поменяйте качество и перемотайте вперёд."),
                new TroubleshootingCase("telegram-connect","telegram","Telegram — подключение","Откройте Telegram. Проверьте сообщение в «Избранное» и загрузку вложения."),
                new TroubleshootingCase("telegram-call","telegram","Telegram — звонок","С согласия собеседника сделайте звонок и проверьте звук в обе стороны."),
                new TroubleshootingCase("dcs-connect","dcs","DCS — подключение к серверу","Подключитесь к нужному серверу DCS. Сайт игры проверяется отдельно от игровой сессии."),
                new TroubleshootingCase("tarkov-connect","tarkov","Tarkov — вход и рейд","Проверьте вход через лаунчер и подключение к рейду; не меняйте сеть во время теста."),
                new TroubleshootingCase("arma-connect","arma","Arma Reforger — подключение","Подключитесь к нужному серверу Arma Reforger."),
                new TroubleshootingCase("steam-connect","steam","Steam — клиент и загрузки","Проверьте вход в Steam и начало загрузки игры.") };
        }
        public static TroubleshootingCase Case(string id)
        { var item=Cases().FirstOrDefault(x=>x.Id==id);if(item==null)throw new ArgumentException("Неизвестный сценарий.","id");return item; }
        public static Task<TroubleshootingRun> RunAsync(TroubleshootingCase item,IProgress<CheckRow> progress,CancellationToken token)
        { if(item==null)throw new ArgumentNullException("item");var known=Case(item.Id);return RunBoundedAsync(known,progress,token,RunPublicAsync,ProbeTimeoutMilliseconds); }
        internal static async Task<TroubleshootingRun> RunBoundedAsync(TroubleshootingCase item,IProgress<CheckRow> progress,CancellationToken token,
            Func<TroubleshootingCase,IProgress<CheckRow>,CancellationToken,Task<List<CheckRow>>> runner,int timeoutMs)
        {
            if(timeoutMs<1||timeoutMs>ProbeTimeoutMilliseconds)throw new ArgumentOutOfRangeException("timeoutMs");
            var capture=new ProbeCapture(item.ServiceId,progress);
            using(var limit=CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                limit.CancelAfter(timeoutMs);
                Task<List<CheckRow>> pending=runner(item,capture,limit.Token);
                var deadline=Task.Delay(timeoutMs,token);
                try
                {
                    if(await Task.WhenAny(pending,deadline).ConfigureAwait(false)!=pending)
                    {
                        Observe(pending);token.ThrowIfCancellationRequested();limit.Cancel();
                        return new TroubleshootingRun {TimedOut=true,Rows=capture.Snapshot()};
                    }
                    return new TroubleshootingRun { Rows=await pending.ConfigureAwait(false) };
                }
                catch(OperationCanceledException) { token.ThrowIfCancellationRequested();return new TroubleshootingRun {TimedOut=true,Rows=capture.Snapshot()}; }
                finally { capture.Stop(); }
            }
        }
        sealed class ProbeCapture:IProgress<CheckRow>
        {
            readonly object gate=new object();readonly string service;readonly IProgress<CheckRow> progress;
            readonly List<CheckRow> rows=new List<CheckRow>();readonly HashSet<string> keys=new HashSet<string>(StringComparer.Ordinal);bool stopped;
            internal ProbeCapture(string service,IProgress<CheckRow> progress) {this.service=service;this.progress=progress;}
            public void Report(CheckRow row)
            {
                CheckRow safe;
                lock(gate)
                {
                    if(stopped||row==null||row.ServiceId!=service||!KnownKey(service,row.Key)||rows.Count>=TroubleshootingSession.MaximumProbes||!keys.Add(row.Key))return;
                    safe=new CheckRow {ServiceId=service,Key=row.Key,DiagnosticCode=DiagnosticPolicy.ProbeCode(row.DiagnosticCode),Passed=row.Passed,Milliseconds=Math.Max(0,Math.Min(120000,row.Milliseconds))};rows.Add(safe);
                }
                if(progress!=null)progress.Report(safe);
            }
            internal List<CheckRow> Snapshot() {lock(gate)return new List<CheckRow>(rows);}
            internal void Stop() {lock(gate)stopped=true;}
        }
        static void Observe(Task task)
        { task.ContinueWith(delegate(Task faulted){var ignored=faulted.Exception;},TaskContinuationOptions.OnlyOnFaulted); }
        static async Task<List<CheckRow>> RunPublicAsync(TroubleshootingCase item,IProgress<CheckRow> progress,CancellationToken token)
        {
            var rows=await ServiceDiagnostics.RunAsync(new[]{item.ServiceId},progress,token).ConfigureAwait(false);
            if(item.Id=="discord-upload")
            {
                // HEAD only; no upload, credentials or signed upload URLs are requested.
                var sample=await NetworkInfo.ProbeAsync("discord-attachments-uploads-prd.storage.googleapis.com",token).ConfigureAwait(false);
                foreach(var row in ServiceDiagnostics.AggregateHttps("discord","upload","Discord — узел вложений","discord-attachments-uploads-prd.storage.googleapis.com",new[]{sample}))
                { rows.Add(row);if(progress!=null)progress.Report(row); }
            }
            return rows;
        }
        internal static List<TroubleshootingProbe> SafeProbes(string service,IEnumerable<CheckRow> rows)
        {
            var output=new List<TroubleshootingProbe>();var keys=new HashSet<string>(StringComparer.Ordinal);
            if(rows==null)return output;
            foreach(var row in rows.Take(TroubleshootingSession.MaximumProbes*2))
            {
                if(row==null||row.ServiceId!=service||!KnownKey(service,row.Key)||!keys.Add(row.Key))continue;
                output.Add(new TroubleshootingProbe {Key=row.Key,Code=DiagnosticPolicy.ProbeCode(row.DiagnosticCode),Passed=row.Passed,Milliseconds=Math.Max(0,Math.Min(120000,row.Milliseconds))});
                if(output.Count==TroubleshootingSession.MaximumProbes)break;
            }
            return output;
        }
        static bool KnownKey(string service,string key)
        {
            if(service=="discord"&&(Tuning.KnownProbeKey(key)||key=="discord.upload.tls"||key=="discord.upload.http"))return true;
            foreach(string part in new[]{"web.tls","web.http","community.tls","community.http","voice","screen","playback","mtproto","client","game","dc1","dc2","dc3"})
                if(key==service+"."+part)return true;
            return false;
        }
        public static string Export(string destination,TroubleshootingSession session,Action<string> exportStandardReport)
        {
            if(session==null||!session.EndedUtc.HasValue)throw new InvalidOperationException("Сначала завершите проверку.");
            if(exportStandardReport==null)throw new ArgumentNullException("exportStandardReport");
            string path=Path.GetFullPath(destination);
            if(!String.Equals(Path.GetExtension(path),".zip",StringComparison.OrdinalIgnoreCase))throw new IOException("Выберите ZIP-файл.");
            string directory=DiagnosticFiles.DirectoryPath(Path.GetDirectoryName(path),false);
            if(File.Exists(path)||Directory.Exists(path))throw new IOException("Файл уже существует.");
            string temporary=Path.Combine(directory,".locallink-case-"+Guid.NewGuid().ToString("N")+".zip");
            bool ownTemporary=false;
            try
            {
                ownTemporary=true;exportStandardReport(temporary);DiagnosticFiles.OrdinaryFile(temporary);
                if(new FileInfo(temporary).Length>8*1024*1024)throw new IOException("Превышен размер отчёта.");
                using(var stream=new FileStream(temporary,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                {
                    using(var archive=new ZipArchive(stream,ZipArchiveMode.Update,true))
                    {
                        string[] standard={"SUMMARY.txt","report.json","history.json","manifest.json"};
                        if(archive.Entries.Count!=standard.Length||standard.Any(x=>archive.Entries.Count(e=>e.FullName==x)!=1))throw new IOException("Неизвестная структура отчёта.");
                        long size=0;foreach(var entry in archive.Entries){size+=entry.Length;if(size>8*1024*1024||entry.Length<0)throw new IOException("Превышен размер отчёта.");}
                        AddEntry(archive,"case.json",DiagnosticFiles.Utf8.GetBytes(DiagnosticFiles.Json(session.Document())));
                        AddEntry(archive,"CASE-SUMMARY.txt",DiagnosticFiles.Utf8.GetBytes(session.Case.Name+"\r\n"+session.Summary()+"\r\n\r\nИнтервал проверки UTC: "+session.StartedUtc.ToString("o",CultureInfo.InvariantCulture)+" — "+session.EndedUtc.Value.ToString("o",CultureInfo.InvariantCulture)+"\r\nИстория LocalLink: history.json. Ничего не отправлено автоматически.\r\n"));
                        archive.GetEntry("manifest.json").Delete();
                        var manifest=new List<object>();
                        foreach(var entry in archive.Entries)
                        {
                            using(var input=entry.Open())using(var sha=SHA256.Create())
                            {
                                var buffer=new byte[8192];long count=0;int read;
                                while((read=input.Read(buffer,0,buffer.Length))>0)
                                {count+=read;if(count>8*1024*1024)throw new IOException("Превышен размер отчёта.");sha.TransformBlock(buffer,0,read,buffer,0);}
                                sha.TransformFinalBlock(new byte[0],0,0);
                                manifest.Add(new {Path=entry.FullName,SHA256=BitConverter.ToString(sha.Hash).Replace("-","").ToLowerInvariant(),Bytes=count});
                            }
                        }
                        AddEntry(archive,"manifest.json",DiagnosticFiles.Utf8.GetBytes(DiagnosticFiles.Json(new {Schema="locallink-bug-report-manifest-v1",Files=manifest})));
                    }
                    stream.Flush(true);
                }
                DiagnosticFiles.DirectoryPath(directory,false);DiagnosticFiles.OrdinaryFile(temporary);File.Move(temporary,path);ownTemporary=false;return path;
            }
            finally { if(ownTemporary)try{if(File.Exists(temporary)){DiagnosticFiles.OrdinaryFile(temporary);File.Delete(temporary);}}catch(IOException){}catch(UnauthorizedAccessException){} }
        }
        static void AddEntry(ZipArchive archive,string name,byte[] bytes)
        { var entry=archive.CreateEntry(name,CompressionLevel.Optimal);using(var output=entry.Open())output.Write(bytes,0,bytes.Length); }
    }
}
