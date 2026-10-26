using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LocalLink
{
    // Export only fixed strategy/stage labels and aggregate counts. In
    // particular no CheckRow.Detail, custom domains, endpoints or raw errors.
    public sealed class TuningServiceResult
    {
        public string ServiceId;
        public int Passed,Failed,Unknown;
    }
    public sealed class TuningTrialResult
    {
        public string Strategy,Stage;
        public int Passed,Failed,Unknown;
        public bool MatrixValid,PreservedBaseline,Improved,Verified;
        public List<TuningServiceResult> Services=new List<TuningServiceResult>();
        public List<TuningProbeResult> Probes=new List<TuningProbeResult>();
    }
    public sealed class TuningProbeResult
    {
        public string Key,Code;
        public bool? Passed;
        public long Milliseconds;
    }
    public sealed class TuningResult
    {
        public bool Enabled,Verified,Cancelled,TimedOut;
        public string Strategy,Summary;
        public string Scope="general";
        public int BaselinePassed,BaselineFailed,BaselineUnknown;
        public List<TuningTrialResult> Trials=new List<TuningTrialResult>();
        public List<TuningServiceResult> BaselineServices=new List<TuningServiceResult>();
        public List<TuningProbeResult> BaselineProbes=new List<TuningProbeResult>();
        public string Limits=Tuning.ResultLimits;
    }

    public static class Tuning
    {
        public const int TotalTimeoutMilliseconds=8*60*1000;
        public const int MatrixTimeoutMilliseconds=150*1000;
        public const int MaximumTrials=5;
        public const string ResultLimits="Публичные API, обновление, CDN и Discord Gateway не подтверждают вход в аккаунт, сообщения, голос, видео или игровые сессии. Telegram в TLS-подборе не проверяется. Наблюдения адаптеров/VPN — только признаки: неизменная сводка не доказывает неизменность маршрута.";

        // A null result permits trials; a result explains why no filter should
        // be started. VPN hints are deliberately not routing attestation.
        public static TuningResult BaselineOutcome(IList<CheckRow> baseline,bool likelyVpn,string strategy)
        {
            if(likelyVpn)return Outcome(strategy,"Есть признаки VPN: автоподбор не может отделить его эффект от фильтра. Фильтр не включён. VPN не изменён.");
            if(baseline==null||!baseline.Any(x=>x!=null&&x.Passed.HasValue))
                return Outcome(strategy,"Нужна проверка в приложении: автоматических результатов нет. Фильтр не включён.");
            if(!baseline.Any(x=>x!=null&&x.Passed==false))
                return Outcome(strategy,"Проверенные HTTPS-узлы доступны по текущему маршруту. Telegram, голос и игры этим не подтверждены. Фильтр не включён.");
            return null;
        }

        // Every matrix must contain exactly the same unique keys. A candidate
        // cannot hide regressions by dropping checks or add apparent wins by
        // converting baseline-unknown/manual rows into successes.
        public static bool IsImprovement(IList<CheckRow> baseline,IList<CheckRow> candidate)
        {
            MatrixScore score=Score(baseline,candidate);
            return score.Valid&&score.Preserved&&score.TargetGains>0;
        }
        internal sealed class MatrixScore
        {
            internal bool Valid,Preserved;
            internal int Passed,Failed,Unknown,Gains,TargetGains;
            internal List<TuningServiceResult> Services=new List<TuningServiceResult>();
        }
        internal static MatrixScore Score(IList<CheckRow> baseline,IList<CheckRow> candidate)
        {
            MatrixScore score=new MatrixScore();
            Dictionary<string,CheckRow> before,after;
            if(!TryMap(baseline,out before)||!TryMap(candidate,out after)||before.Count==0||before.Count!=after.Count)return score;
            score.Preserved=true;
            SortedDictionary<string,TuningServiceResult> services=new SortedDictionary<string,TuningServiceResult>(StringComparer.Ordinal);
            foreach(KeyValuePair<string,CheckRow> pair in before)
            {
                CheckRow row;
                if(!after.TryGetValue(pair.Key,out row))return new MatrixScore();
                bool? was=pair.Value.Passed;
                AddServiceCount(services,pair.Value.ServiceId,was.HasValue?row.Passed:null);
                if(!was.HasValue){score.Unknown++;continue;}
                if(row.Passed==true)score.Passed++;
                else if(row.Passed==false)score.Failed++;
                else score.Unknown++;
                if(was==true&&row.Passed!=true)score.Preserved=false;
                if(was==false&&row.Passed==true)
                {
                    score.Gains++;
                    // Controls guard regressions, but their ordinary recovery
                    // cannot by itself establish a target-service improvement.
                    // Missing ServiceId in legacy pure fixtures is a target.
                    if(pair.Value.ServiceId!="control")score.TargetGains++;
                }
            }
            score.Valid=true;
            score.Services=services.Values.ToList();
            return score;
        }
        static bool TryMap(IList<CheckRow> rows,out Dictionary<string,CheckRow> map)
        {
            map=new Dictionary<string,CheckRow>(StringComparer.Ordinal);
            if(rows==null)return false;
            foreach(CheckRow row in rows)
            {
                if(row==null||String.IsNullOrEmpty(row.Key)||map.ContainsKey(row.Key))return false;
                map.Add(row.Key,row);
            }
            return true;
        }

        internal static List<TuningProbeResult> ProbeEvidence(IEnumerable<CheckRow> rows)
        {
            var result=new List<TuningProbeResult>();var seen=new HashSet<string>(StringComparer.Ordinal);
            if(rows==null)return result;
            foreach(var row in rows)
            {
                if(row==null||row.ServiceId!="discord"||!KnownProbeKey(row.Key)||!seen.Add(row.Key))continue;
                result.Add(new TuningProbeResult{Key=row.Key,Code=KnownProbeCode(row.DiagnosticCode)?row.DiagnosticCode:"unknown",
                    Passed=row.Passed,Milliseconds=Math.Max(0,Math.Min(32000,row.Milliseconds))});
            }
            return result;
        }
        internal static bool KnownProbeKey(string key)
        {
            if(key=="discord.gateway"||key=="discord.web.tls"||key=="discord.web.http")return true;
            foreach(string target in new[]{"api","updater","manifest","cdn"})
                foreach(string family in new[]{"v4","v6"})
                    foreach(string stage in new[]{"",".dns",".tcp",".tls"})
                        if(key=="discord.desktop."+target+"."+family+stage)return true;
            return false;
        }
        internal static bool KnownProbeCode(string value)
        {
            foreach(string code in new[]{"ok","unknown","certificate-or-tls","dns-failed","dns-no-address","dns-nonpublic-address",
                "gateway-payload-invalid","gateway-url-valid","http-2xx","http-4xx-reachable","http-5xx-reachable",
                "http-body-encoding","http-body-limit","http-chunk-limit","http-header-limit","http-incomplete-body",
                "http-incomplete-headers","http-invalid-chunk","http-invalid-framing","http-invalid-headers","http-invalid-redirect",
                "http-invalid-status","http-line-limit","http-redirect-not-followed","http-status-unexpected","http-trailer-limit",
                "http-unbounded-body","manifest-payload-invalid","manifest-payload-valid","network-unreachable","not-tested-dns",
                "not-tested-tcp","not-tested-tls","socket-failed","tcp-refused","tcp-reset","updater-redirect-invalid",
                "updater-redirect-valid","updater-response-unexpected"})if(value==code)return true;
            foreach(string stage in new[]{"dns","tcp","tls","http"})
                foreach(string kind in new[]{"-timeout","-closed","-unavailable"})if(value==stage+kind)return true;
            return false;
        }

        // Isolated runtime makes orchestration testable without starting a
        // driver/filter, observing local routes, or making a network request.
        internal abstract class Runtime
        {
            internal string ExpectedRouteSummary;
            internal abstract bool IsRunning {get;}
            internal abstract bool HasLikelyVpn {get;}
            internal abstract string RouteSummary {get;}
            internal abstract Task StartAsync(string strategy,CancellationToken token);
            internal abstract void Stop();
            internal abstract Task<List<CheckRow>> CheckAsync(CancellationToken token);
            internal virtual Task SettleAsync(CancellationToken token){return Task.Delay(600,token);}
            internal virtual bool BaselineUsesFilter {get{return false;}}
            internal virtual string Scope {get{return "general";}}
            internal virtual bool CandidateAcceptable(IList<CheckRow> rows){return true;}
        }
        sealed class EngineRuntime:Runtime
        {
            readonly Engine engine;
            readonly ProfileSpec spec;
            readonly List<string> selected;
            readonly IProgress<CheckRow> progress;
            readonly bool discordOnly;
            internal EngineRuntime(Engine engine,ProfileSpec spec,List<string> selected,IProgress<CheckRow> progress,bool discordOnly=false)
            {this.engine=engine;this.spec=spec;this.selected=selected;this.progress=progress;this.discordOnly=discordOnly;}
            internal override bool BaselineUsesFilter {get{return discordOnly;}}
            internal override string Scope {get{return discordOnly?"discord":"general";}}
            internal override bool CandidateAcceptable(IList<CheckRow> rows){return !discordOnly||DiscordBootstrapReady(rows);}
            internal override bool IsRunning {get{return engine.IsRunning;}}
            internal override bool HasLikelyVpn {get{return NetworkInfo.HasLikelyVpn();}}
            internal override string RouteSummary {get{return NetworkInfo.Summary();}}
            internal override async Task StartAsync(string strategy,CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if(HasLikelyVpn||RouteSummary!=ExpectedRouteSummary)throw new RouteChangedException();
                // Only the TLS method changes; all user-selected scope and
                // optional native profiles remain fixed throughout the run.
                ProfileSpec trial=new ProfileSpec{Mode=HelperMode.Direct,Domains=spec.Domains,HostListFile=spec.HostListFile,
                    Strategy=discordOnly?spec.Strategy:strategy,DiscordStrategy=discordOnly?strategy:spec.DiscordStrategy,
                    Quic=spec.Quic,DiscordVoice=spec.DiscordVoice,TelegramNative=spec.TelegramNative};
                using(var prepared=await engine.PrepareStartAsync(trial,token))
                {
                    token.ThrowIfCancellationRequested();
                    if(HasLikelyVpn||RouteSummary!=ExpectedRouteSummary)throw new RouteChangedException();
                    engine.StartPrepared(prepared);
                }
            }
            internal override void Stop(){engine.Stop();}
            internal override async Task<List<CheckRow>> CheckAsync(CancellationToken token)
            {
                // Ordinary Check is unchanged. Extra HTTPS controls are made
                // only after the user explicitly starts this TLS tuning run.
                var gated=new TokenProgress(progress,token);
                Task<List<CheckRow>> service=ServiceDiagnostics.RunAsync(selected,gated,token);
                Task<List<CheckRow>> microsoft=ControlAsync("microsoft","Microsoft — контроль HTTPS","www.microsoft.com",gated,token);
                Task<List<CheckRow>> cloudflare=ControlAsync("cloudflare","Cloudflare — контроль HTTPS","www.cloudflare.com",gated,token);
                List<CheckRow>[] groups=await Task.WhenAll(service,microsoft,cloudflare);
                token.ThrowIfCancellationRequested();
                List<CheckRow> rows=new List<CheckRow>();
                foreach(List<CheckRow> group in groups)rows.AddRange(group);
                return rows;
            }
        }
        sealed class TokenProgress:IProgress<CheckRow>
        {
            readonly IProgress<CheckRow> target;
            readonly CancellationToken token;
            internal TokenProgress(IProgress<CheckRow> target,CancellationToken token){this.target=target;this.token=token;}
            public void Report(CheckRow row){if(target!=null&&!token.IsCancellationRequested)target.Report(row);}
        }
        static async Task<List<CheckRow>> ControlAsync(string key,string name,string host,IProgress<CheckRow> progress,CancellationToken token)
        {
            List<ProbeResult> samples=new List<ProbeResult>();
            for(int i=0;i<ServiceDiagnostics.AttemptCount;i++)
            {
                token.ThrowIfCancellationRequested();
                samples.Add(await NetworkInfo.ProbeAsync(host,token));
                token.ThrowIfCancellationRequested();
            }
            List<CheckRow> rows=ServiceDiagnostics.AggregateHttps("control",key,name,host,samples);
            if(progress!=null)foreach(CheckRow row in rows)progress.Report(row);
            return rows;
        }
        sealed class RouteChangedException:Exception{}
        sealed class MatrixTimeoutException:Exception{}
        sealed class RankedTrial
        {
            internal string Strategy;
            internal int Order;
            internal MatrixScore Score;
            internal List<CheckRow> Rows;
        }

        public static Task<TuningResult> RunAsync(Engine engine,ProfileSpec spec,IEnumerable<string> ids,
            IProgress<CheckRow> progress,Action<string> phase,CancellationToken token)
        {
            if(engine==null||spec==null)throw new ArgumentNullException();
            if(spec.Mode!=HelperMode.Direct)throw new ArgumentException("Подбор доступен только в режиме сервисов.");
            if(engine.IsRunning)throw new InvalidOperationException("Перед подбором остановите обработку.");
            Profiles.Build(spec);
            List<string> selected=ServiceCatalog.Select(ids??new string[0]).Where(x=>x.Id!="telegram").Select(x=>x.Id).ToList();
            if(selected.Count==0)throw new ArgumentException("Выберите хотя бы один сервис HTTPS. Telegram не входит в TLS-подбор.");
            return RunWithRuntimeAsync(new EngineRuntime(engine,spec,selected,progress),spec.Strategy,Profiles.TlsStrategies(),phase,token);
        }

        public static Task<TuningResult> RunDiscordAsync(Engine engine,ProfileSpec spec,IEnumerable<string> ids,
            IProgress<CheckRow> progress,Action<string> phase,CancellationToken token)
        {
            if(engine==null||spec==null)throw new ArgumentNullException();
            if(spec.Mode!=HelperMode.Direct||engine.IsRunning)throw new InvalidOperationException("Остановите обработку перед отдельным подбором Discord.");
            Profiles.Build(spec);
            var selected=ServiceCatalog.Select(ids??new string[0]).Where(x=>x.Id!="telegram").Select(x=>x.Id).ToList();
            if(!selected.Contains("discord"))throw new ArgumentException("Выберите Discord для отдельного подбора.");
            string original=String.IsNullOrEmpty(spec.DiscordStrategy)?spec.Strategy:spec.DiscordStrategy;
            return RunWithRuntimeAsync(new EngineRuntime(engine,spec,selected,progress,true),original,Profiles.TlsStrategies(),phase,token);
        }

        // Public bootstrap, not authenticated Desktop use. Do not select a
        // strategy because DNS/TCP/site/Hello alone improved. Every DNS-present
        // API family must reach these public dependencies; absent AAAA stays
        // unknown rather than fabricating an IPv6 failure or success.
        internal static bool DiscordBootstrapReady(IList<CheckRow> rows)
        {
            Dictionary<string,CheckRow> map;if(!TryMap(rows,out map))return false;
            CheckRow gateway;if(!map.TryGetValue("discord.gateway",out gateway)||gateway.ServiceId!="discord"||gateway.Passed!=true)return false;
            foreach(string endpoint in new[]{"api","updater","manifest","cdn"})
            {
                bool supported=false;
                foreach(string family in new[]{"v4","v6"})
                {
                    string prefix="discord.desktop."+endpoint+"."+family;
                    CheckRow dns;if(!map.TryGetValue(prefix+".dns",out dns)||dns.ServiceId!="discord")return false;
                    if(dns.Passed==false)return false;
                    if(dns.Passed!=true)continue;
                    supported=true;
                    string target=prefix+(endpoint=="cdn"?".tls":"");
                    CheckRow row;if(!map.TryGetValue(target,out row)||row.ServiceId!="discord"||row.Passed!=true)return false;
                }
                if(!supported)return false;
            }
            return true;
        }

        internal static async Task<TuningResult> RunWithRuntimeAsync(Runtime runtime,string originalStrategy,
            IEnumerable<string> strategies,Action<string> phase,CancellationToken token,
            int totalTimeoutMilliseconds=TotalTimeoutMilliseconds,int matrixTimeoutMilliseconds=MatrixTimeoutMilliseconds)
        {
            if(runtime==null)throw new ArgumentNullException("runtime");
            if(totalTimeoutMilliseconds<1||totalTimeoutMilliseconds>TotalTimeoutMilliseconds||matrixTimeoutMilliseconds<1||matrixTimeoutMilliseconds>MatrixTimeoutMilliseconds)
                throw new ArgumentOutOfRangeException("totalTimeoutMilliseconds");
            List<string> methods=ValidateStrategies(strategies);
            TuningResult result=Outcome(originalStrategy,String.Empty);
            result.Scope=runtime.Scope;
            bool keep=false,started=false;
            using(CancellationTokenSource deadline=CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(totalTimeoutMilliseconds);
                CancellationToken runToken=deadline.Token;
                try
                {
                    runToken.ThrowIfCancellationRequested();
                    if(runtime.IsRunning)throw new InvalidOperationException("Перед подбором остановите обработку.");
                    if(runtime.HasLikelyVpn){result.Summary=BaselineOutcome(null,true,originalStrategy).Summary;return result;}
                    string route=runtime.RouteSummary;
                    runtime.ExpectedRouteSummary=route;
                    if(runtime.BaselineUsesFilter)
                    {
                        Emit(phase,"Исходный профиль включён · общий метод остальных сервисов сохраняется");
                        started=true;await runtime.StartAsync(originalStrategy,runToken);
                        await runtime.SettleAsync(runToken);CheckRunning(runtime);
                    }
                    Emit(phase,runtime.BaselineUsesFilter?"Проверка исходного профиля и публичных узлов Discord":"Проверка без фильтра · сервисы и два контрольных HTTPS-сайта");
                    List<CheckRow> baseline=await MatrixAsync(runtime,runToken,matrixTimeoutMilliseconds);
                    CheckRoute(runtime,route,runToken);
                    if(runtime.BaselineUsesFilter)CheckRunning(runtime);
                    SetBaselineCounts(result,baseline);
                    result.BaselineProbes=ProbeEvidence(baseline);
                    if(!Score(baseline,baseline).Valid)
                    {
                        result.Summary="Исходная матрица проверок неполна или некорректна. Фильтр не включён; исходный метод сохранён.";
                        return result;
                    }
                    if(runtime.BaselineUsesFilter&&runtime.CandidateAcceptable(baseline))
                    {
                        // No needless method change when public bootstrap is
                        // already available. This is NOT a successful call test.
                        keep=true;result.Enabled=true;
                        result.Summary="Публичные узлы Discord доступны на исходном профиле. Общий метод не изменён; фильтр включён. Работа аккаунта и голоса не подтверждена.";
                        return result;
                    }
                    if(started){runtime.Stop();started=false;}
                    TuningResult baselineOutcome=BaselineOutcome(baseline,false,originalStrategy);
                    if(baselineOutcome!=null)
                    {
                        result.Summary=runtime.BaselineUsesFilter?"Публичный bootstrap Discord полностью не подтверждён; сетевой отказ для подбора не установлен. Метод не менялся, фильтр выключен. См. этапы API/обновления/CDN в отчёте.":baselineOutcome.Summary;
                        return result;
                    }
                    List<RankedTrial> ranked=new List<RankedTrial>();
                    for(int i=0;i<methods.Count;i++)
                    {
                        string method=methods[i];
                        CheckRoute(runtime,route,runToken);
                        List<CheckRow> current;
                        try
                        {
                            started=true;
                            Emit(phase,"Метод "+(i+1)+"/"+methods.Count+": "+method+" · подготовка");
                            await runtime.StartAsync(method,runToken);
                            CheckRoute(runtime,route,runToken);
                            await runtime.SettleAsync(runToken);
                            CheckRunning(runtime);
                            Emit(phase,"Метод "+(i+1)+"/"+methods.Count+": "+method+" · проверка полной матрицы");
                            current=await MatrixAsync(runtime,runToken,matrixTimeoutMilliseconds);
                            CheckRoute(runtime,route,runToken);
                            CheckRunning(runtime);
                        }
                        finally {if(started){runtime.Stop();started=false;}}
                        MatrixScore score=Score(baseline,current);
                        var trialMetadata=Metadata(method,"trial",score,false);trialMetadata.Probes=ProbeEvidence(current);result.Trials.Add(trialMetadata);
                        if(score.Valid&&score.Preserved&&score.TargetGains>0&&runtime.CandidateAcceptable(current))
                            ranked.Add(new RankedTrial{Strategy=method,Order=i,Score=score,Rows=KnownSnapshot(baseline,current)});
                    }
                    // Evaluate all methods before ranking. Equal gains prefer
                    // fewer unknowns (not apparent wins), then fewer failures
                    // and stable method
                    // order (a less intrusive profile is tried first on ties).
                    ranked.Sort(delegate(RankedTrial left,RankedTrial right)
                    {
                        int order=right.Score.Passed.CompareTo(left.Score.Passed);
                        if(order==0)order=left.Score.Unknown.CompareTo(right.Score.Unknown);
                        if(order==0)order=left.Score.Failed.CompareTo(right.Score.Failed);
                        if(order==0)order=left.Order.CompareTo(right.Order);
                        return order;
                    });
                    foreach(RankedTrial candidate in ranked)
                    {
                        CheckRoute(runtime,route,runToken);
                        bool verified=false;
                        try
                        {
                            started=true;
                            Emit(phase,"Повторная проверка кандидата: "+candidate.Strategy);
                            await runtime.StartAsync(candidate.Strategy,runToken);
                            CheckRoute(runtime,route,runToken);
                            await runtime.SettleAsync(runToken);
                            CheckRunning(runtime);
                            List<CheckRow> current=await MatrixAsync(runtime,runToken,matrixTimeoutMilliseconds);
                            CheckRoute(runtime,route,runToken);
                            CheckRunning(runtime);
                            MatrixScore score=Score(baseline,current);
                            // A best single run is not enough: every success
                            // observed in this candidate must repeat, including
                            // all baseline successes. Unknowns stay unknown.
                            MatrixScore repeated=Score(candidate.Rows,current);
                            verified=score.Valid&&score.Preserved&&score.TargetGains>0&&repeated.Valid&&repeated.Preserved&&runtime.CandidateAcceptable(current);
                            var verificationMetadata=Metadata(candidate.Strategy,"verification",score,verified);verificationMetadata.Probes=ProbeEvidence(current);result.Trials.Add(verificationMetadata);
                            if(verified)
                            {
                                runToken.ThrowIfCancellationRequested();
                                keep=true;
                                result.Enabled=true;result.Verified=true;result.Strategy=candidate.Strategy;
                                bool partial=score.Failed>0||score.Unknown>baseline.Count(x=>x.Passed==null);
                                result.Summary=(partial?"Частичное улучшение HTTPS-проверок подтверждено повтором":"Улучшение HTTPS-проверок подтверждено повтором")+
                                    " без потери ранее успешных проверок. Метод: "+candidate.Strategy+
                                    ". Фильтр включён. Telegram, голос, видео и игровые сессии этим не подтверждены.";
                                return result;
                            }
                        }
                        finally{if(started&&!verified){runtime.Stop();started=false;}}
                    }
                    result.Summary="Повторяемого улучшения HTTPS-проверок без регрессии не найдено. Фильтр выключен; исходный метод сохранён. Telegram в TLS-подборе не проверяется.";
                    return result;
                }
                catch(RouteChangedException)
                {
                    result.Summary="Изменились признаки сети или VPN. Сравнение остановлено; фильтр выключен, исходный метод сохранён. VPN не изменён.";
                    return result;
                }
                catch(MatrixTimeoutException)
                {
                    result.TimedOut=true;
                    result.Summary="Проверка превысила лимит времени. Неполная матрица не выбирает метод; фильтр выключен, исходный метод сохранён.";
                    return result;
                }
                catch(OperationCanceledException)
                {
                    if(token.IsCancellationRequested)
                    {
                        result.Cancelled=true;
                        result.Summary="Подбор отменён. Завершённые результаты сохранены; фильтр выключен, исходный метод сохранён.";
                        return result;
                    }
                    result.TimedOut=true;
                    result.Summary="Достигнут лимит подбора (8 минут). Частичные результаты не выбирают метод; фильтр выключен, исходный метод сохранён.";
                    return result;
                }
                finally
                {
                    if(runToken.IsCancellationRequested)
                    {
                        keep=false;result.Enabled=false;result.Verified=false;result.Strategy=originalStrategy;
                        if(token.IsCancellationRequested)
                        {result.Cancelled=true;result.Summary="Подбор отменён. Завершённые результаты сохранены; фильтр выключен, исходный метод сохранён.";}
                        else
                        {result.TimedOut=true;result.Summary="Достигнут лимит подбора (8 минут). Фильтр выключен, исходный метод сохранён.";}
                    }
                    if(!keep&&started)runtime.Stop();
                }
            }
        }

        static async Task<List<CheckRow>> MatrixAsync(Runtime runtime,CancellationToken token,int timeoutMilliseconds)
        {
            using(CancellationTokenSource matrix=CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                Task<List<CheckRow>> check=runtime.CheckAsync(matrix.Token);
                Task limit=Task.Delay(timeoutMilliseconds,matrix.Token);
                if(await Task.WhenAny(check,limit)!=check)
                {
                    matrix.Cancel();Observe(check);token.ThrowIfCancellationRequested();
                    throw new MatrixTimeoutException();
                }
                try{List<CheckRow> rows=await check;token.ThrowIfCancellationRequested();return rows;}
                finally{matrix.Cancel();}
            }
        }
        static List<string> ValidateStrategies(IEnumerable<string> strategies)
        {
            if(strategies==null)throw new ArgumentNullException("strategies");
            HashSet<string> allowed=new HashSet<string>(new[]{"split","sni-split","disorder","fake-split","fake-disorder"},StringComparer.Ordinal);
            List<string> result=new List<string>();
            foreach(string strategy in strategies)
            {
                if(!allowed.Contains(strategy)||result.Contains(strategy)||result.Count>=MaximumTrials)throw new ArgumentException("Недопустимый набор методов.","strategies");
                result.Add(strategy);
            }
            if(result.Count==0)throw new ArgumentException("Методы не заданы.","strategies");
            return result;
        }
        static List<CheckRow> KnownSnapshot(IList<CheckRow> baseline,IList<CheckRow> candidate)
        {
            Dictionary<string,CheckRow> map;TryMap(candidate,out map);
            return baseline.Select(x=>new CheckRow{Key=x.Key,Passed=x.Passed.HasValue?map[x.Key].Passed:null}).ToList();
        }
        static TuningTrialResult Metadata(string strategy,string stage,MatrixScore score,bool verified)
        {
            return new TuningTrialResult{Strategy=strategy,Stage=stage,Passed=score.Passed,Failed=score.Failed,Unknown=score.Unknown,
                MatrixValid=score.Valid,PreservedBaseline=score.Preserved,Improved=score.Valid&&score.Preserved&&score.TargetGains>0,Verified=verified,Services=score.Services};
        }
        static void SetBaselineCounts(TuningResult result,IList<CheckRow> baseline)
        {
            if(baseline==null)return;
            result.BaselineServices=Score(baseline,baseline).Services;
            foreach(CheckRow row in baseline)
            {
                if(row==null)continue;
                if(row.Passed==true)result.BaselinePassed++;
                else if(row.Passed==false)result.BaselineFailed++;
                else result.BaselineUnknown++;
            }
        }
        static void AddServiceCount(SortedDictionary<string,TuningServiceResult> services,string id,bool? passed)
        {
            // Never copy an arbitrary row/service label into exported metadata.
            if(id!="control"&&id!="discord"&&id!="youtube"&&id!="steam"&&id!="arma"&&id!="tarkov")return;
            TuningServiceResult service;
            if(!services.TryGetValue(id,out service))
            {service=new TuningServiceResult{ServiceId=id};services.Add(id,service);}
            if(passed==true)service.Passed++;
            else if(passed==false)service.Failed++;
            else service.Unknown++;
        }
        static TuningResult Outcome(string strategy,string summary){return new TuningResult{Enabled=false,Strategy=strategy,Summary=summary};}
        static void CheckRoute(Runtime runtime,string route,CancellationToken token)
        {token.ThrowIfCancellationRequested();if(runtime.HasLikelyVpn||route!=runtime.RouteSummary)throw new RouteChangedException();}
        static void CheckRunning(Runtime runtime)
        {if(!runtime.IsRunning)throw new InvalidOperationException("Движок завершился во время подбора. Фильтр не оставлен включённым.");}
        static void Emit(Action<string> phase,string text){if(phase!=null)phase(text);}
        static void Observe(Task task)
        {
            task.ContinueWith(delegate(Task finished){if(finished.IsFaulted){AggregateException observed=finished.Exception;GC.KeepAlive(observed);}},
                CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        }
    }
}
