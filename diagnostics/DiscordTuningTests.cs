using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LocalLink
{
    // Synthetic public-bootstrap matrices and an isolated Runtime only. No
    // Engine, native driver/filter, process inventory, DNS, network request,
    // Discord installation, account, message, audio or microphone is touched.
    internal static class DiscordTuningTests
    {
        static int checks;
        static readonly string[] Methods={"split","sni-split","disorder","fake-split","fake-disorder"};

        static int Main()
        {
            try
            {
                Run();
                Console.WriteLine("DISCORD TUNING OFFLINE TESTS PASSED: "+checks+". Fake runtime only; no filter/network.");
                return 0;
            }
            catch(Exception error){Console.Error.WriteLine(error);return 1;}
        }

        internal static void Run()
        {
            BootstrapGate();
            ScopedOrchestration();
            ScopedProfileEvidence();
        }

        static void BootstrapGate()
        {
            List<CheckRow> ready=Rows(true,true,true,true,true,true);
            Check(Tuning.DiscordBootstrapReady(ready),"complete IPv4 public bootstrap with explicit no-address IPv6 accepted");
            Check(!Tuning.DiscordBootstrapReady(ready.Where(x=>!x.Key.Contains(".v6")).ToList()),"missing IPv6 DNS row is incomplete matrix, not no-address evidence");
            Check(!Tuning.DiscordBootstrapReady(null),"null bootstrap matrix refused");
            Check(!Tuning.DiscordBootstrapReady(new List<CheckRow>()),"empty bootstrap matrix refused");
            Check(!Tuning.DiscordBootstrapReady(new List<CheckRow>{Row("discord.gateway","discord",true)}),"Gateway Hello alone is not REST/updater evidence");
            Check(!Tuning.DiscordBootstrapReady(new List<CheckRow>{Row("discord.web.tls","discord",true),Row("discord.web.http","discord",true)}),"ordinary website success not Desktop bootstrap");
            var transportOnly=ready.Where(x=>x.Key.EndsWith(".dns",StringComparison.Ordinal)||x.Key.EndsWith(".tcp",StringComparison.Ordinal)).ToList();
            Check(!Tuning.DiscordBootstrapReady(transportOnly),"DNS/TCP evidence cannot choose a Desktop method");
            foreach(bool? badHello in new bool?[]{false,null})
            {
                var missingHello=Clone(ready);missingHello.Single(x=>x.Key=="discord.gateway").Passed=badHello;
                Check(!Tuning.DiscordBootstrapReady(missingHello),"REST/updater successes cannot hide failed/unknown Gateway Hello");
            }
            foreach(string key in new[]{"discord.desktop.api.v4","discord.desktop.manifest.v4","discord.desktop.updater.v4","discord.desktop.cdn.v4.tls"})
            {
                foreach(bool? bad in new bool?[]{false,null})
                {
                    var missing=Clone(ready);missing.Single(x=>x.Key==key).Passed=bad;
                    Check(!Tuning.DiscordBootstrapReady(missing),"required bootstrap stage false/unknown refused: "+key);
                }
                Check(!Tuning.DiscordBootstrapReady(ready.Where(x=>x.Key!=key).ToList()),"missing required bootstrap stage refused: "+key);
            }
            var duplicate=Clone(ready);duplicate.Add(Row("discord.desktop.api.v4","discord",true));
            Check(!Tuning.DiscordBootstrapReady(duplicate),"duplicate bootstrap keys refused");
            var nullRow=Clone(ready);nullRow.Add(null);
            Check(!Tuning.DiscordBootstrapReady(nullRow),"null bootstrap row refused");
            var ipv6=Clone(ready);ipv6.RemoveAll(x=>x.Key.Contains(".v6"));AddFamily(ipv6,"v6",true,true,true,true,true);
            Check(Tuning.DiscordBootstrapReady(ipv6),"all advertised IPv4/IPv6 bootstrap families ready");
            foreach(string suffix in new[]{"api.v6","manifest.v6","updater.v6","cdn.v6.tls"})
            {
                var broken6=Clone(ipv6);broken6.Single(x=>x.Key=="discord.desktop."+suffix).Passed=false;
                Check(!Tuning.DiscordBootstrapReady(broken6),"advertised IPv6 failed stage cannot be hidden by IPv4: "+suffix);
            }
            var failed6Dns=Clone(ipv6);failed6Dns.Single(x=>x.Key=="discord.desktop.api.v6.dns").Passed=false;
            Check(!Tuning.DiscordBootstrapReady(failed6Dns),"explicit failed IPv6 DNS stage refused instead of silently discarded");
            var noV6=Clone(ready);
            Check(Tuning.DiscordBootstrapReady(noV6),"explicit no-address IPv6 family unknown does not block ready IPv4");
            var onlyV6=ready.Where(x=>!x.Key.StartsWith("discord.desktop.",StringComparison.Ordinal)).Select(x=>new CheckRow{Key=x.Key,ServiceId=x.ServiceId,Passed=x.Passed}).ToList();
            AddFamily(onlyV6,"v4",null,null,null,null,null);AddFamily(onlyV6,"v6",true,true,true,true,true);
            Check(Tuning.DiscordBootstrapReady(onlyV6),"IPv6-only public bootstrap can be ready without pretending IPv4 exists");
            var noFamilies=ready.Where(x=>!x.Key.StartsWith("discord.desktop.",StringComparison.Ordinal)).ToList();
            AddFamily(noFamilies,"v4",null,null,null,null,null);AddFamily(noFamilies,"v6",null,null,null,null,null);
            Check(!Tuning.DiscordBootstrapReady(noFamilies),"no tested public address family never produces readiness");
            var failed4Dns=Clone(ipv6);failed4Dns.Single(x=>x.Key=="discord.desktop.api.v4.dns").Passed=false;
            Check(!Tuning.DiscordBootstrapReady(failed4Dns),"explicit failed IPv4 DNS cannot hide behind ready IPv6");
            var mixedFamilies=Clone(ipv6);
            foreach(CheckRow row in mixedFamilies.Where(x=>x.Key.StartsWith("discord.desktop.cdn.v6",StringComparison.Ordinal)))row.Passed=null;
            Check(Tuning.DiscordBootstrapReady(mixedFamilies),"dual-stack API with IPv4-only CDN is valid public bootstrap");
            foreach(string target in new[]{"api","manifest","updater","cdn"})
            {
                var failedEndpointDns=Clone(ipv6);failedEndpointDns.Single(x=>x.Key=="discord.desktop."+target+".v6.dns").Passed=false;
                Check(!Tuning.DiscordBootstrapReady(failedEndpointDns),"explicit DNS failure for each endpoint refused: "+target);
                var noEndpoint=Clone(ipv6);
                noEndpoint.Single(x=>x.Key=="discord.desktop."+target+".v4.dns").Passed=null;
                noEndpoint.Single(x=>x.Key=="discord.desktop."+target+".v6.dns").Passed=null;
                Check(!Tuning.DiscordBootstrapReady(noEndpoint),"endpoint with neither public address family not ready: "+target);
                Check(!Tuning.DiscordBootstrapReady(ipv6.Where(x=>x.Key!="discord.desktop."+target+".v6.dns").ToList()),"missing per-endpoint family DNS row refused: "+target);
            }
            var poisoned=Clone(ready);poisoned.Single(x=>x.Key=="discord.desktop.api.v4").ServiceId="youtube";
            Check(!Tuning.DiscordBootstrapReady(poisoned),"bootstrap key with another service cannot pass");
            var readyWithManual=Clone(ready);
            Check(Tuning.DiscordBootstrapReady(readyWithManual),"manual voice unknown does not claim or prevent public bootstrap proof");
        }

        static void ScopedOrchestration()
        {
            List<CheckRow> baseline=Rows(false,true,true,true,false,true);
            List<CheckRow> ready=Rows(true,true,true,true,true,true);
            List<CheckRow> weak=Rows(false,true,true,true,true,true);
            FakeRuntime best=new FakeRuntime(baseline,weak,ready,baseline,baseline,baseline,ready);
            TuningResult result=Execute(best,"disorder");
            Check(result.Enabled&&result.Verified&&result.Strategy=="sni-split","only repeatable complete bootstrap candidate selected");
            Check(result.Scope=="discord","scoped result labeled separately from global HTTPS tuning");
            Check(best.Starts.SequenceEqual(new[]{"disorder","split","sni-split","disorder","fake-split","fake-disorder","sni-split"}),"baseline starts original filter before all Discord trial methods");
            Check(best.ChecksWhileRunning.All(x=>x),"baseline and all candidate matrices measured with controlled filter running");
            Check(best.Events.Take(3).SequenceEqual(new[]{"start:disorder","check:disorder","stop:disorder"}),"baseline filter stopped before first trial");
            Check(best.StopCount==6&&best.CheckCount==7&&best.IsRunning,"baseline plus five trial filters stopped; verified winner left running");
            Check(result.Trials.Count==6&&result.Trials.Count(x=>x.Verified)==1,"all five trials and one verification retained");
            Check(best.AcceptCount>=3,"readiness guard consulted for gain candidates and verification");
            best.Stop();

            FakeRuntime onlyHello=new FakeRuntime(baseline,weak,weak,weak,weak,weak);
            result=Execute(onlyHello,"disorder");
            Check(!result.Enabled&&!result.Verified&&result.Strategy=="disorder"&&!onlyHello.IsRunning,"Gateway-only gains leave original choice and filter off");
            Check(onlyHello.Starts.Count==6&&onlyHello.StopCount==6&&onlyHello.CheckCount==6,"unready candidates not verified and every started filter stopped");

            List<CheckRow> regressed=Rows(true,true,true,true,true,false);
            FakeRuntime regression=new FakeRuntime(baseline,regressed,regressed,regressed,regressed,regressed);
            result=Execute(regression,"fake-split");
            Check(!result.Enabled&&!result.Verified&&!regression.IsRunning,"Discord bootstrap gain cannot erase prior YouTube success");
            Check(regression.Starts.Count==6&&regression.StopCount==6,"cross-service regression never reaches winner verification");

            FakeRuntime unstable=new FakeRuntime(baseline,ready,baseline,baseline,baseline,baseline,weak);
            result=Execute(unstable,"disorder");
            Check(!result.Enabled&&!result.Verified&&!unstable.IsRunning&&result.Strategy=="disorder","partial repeat cannot approve previously complete bootstrap");
            Check(unstable.StopCount==7&&unstable.CheckCount==7,"failed verification stopped in addition to baseline/trials");

            FakeRuntime readinessChanged=new FakeRuntime(baseline,ready,baseline,baseline,baseline,baseline,ready);
            readinessChanged.AcceptOverride=delegate(IList<CheckRow> rows,int count){return readinessChanged.CheckCount<7&&Tuning.DiscordBootstrapReady(rows);};
            result=Execute(readinessChanged,"disorder");
            Check(!result.Enabled&&!result.Verified&&!readinessChanged.IsRunning,"readiness guard runs again at verification, not only candidate ranking");

            FakeRuntime noChange=new FakeRuntime(ready);
            result=Execute(noChange,"fake-split");
            Check(result.Enabled&&!result.Verified&&noChange.Starts.SequenceEqual(new[]{"fake-split"})&&noChange.StopCount==0&&noChange.IsRunning,"already-ready filtered baseline keeps original without claiming candidate verification");
            noChange.Stop();

            FakeRuntime invalid=new FakeRuntime(new List<CheckRow>{Row("duplicate","discord",false),Row("duplicate","discord",true)});
            result=Execute(invalid,"disorder");
            Check(!result.Enabled&&!invalid.IsRunning&&invalid.Starts.Count==1&&invalid.StopCount==1,"invalid filtered baseline stopped before return");

            FakeRuntime vpn=new FakeRuntime();vpn.Vpn=true;
            result=Execute(vpn,"disorder");
            Check(!result.Enabled&&vpn.Starts.Count==0&&vpn.StopCount==0&&vpn.CheckCount==0,"initial VPN hints prevent any scoped filter start");
            Check(result.Scope=="discord","VPN refusal still retains scoped report label");

            FakeRuntime route=new FakeRuntime(baseline);route.AfterCheck=delegate(int count){route.Route="different route summary";};
            result=Execute(route,"disorder");
            Check(!result.Enabled&&!route.IsRunning&&route.Starts.Count==1&&route.StopCount==1,"route change during filtered baseline cleans up original filter");

            using(var cancellation=new CancellationTokenSource())
            {
                cancellation.Cancel();FakeRuntime preCancelled=new FakeRuntime();
                result=Execute(preCancelled,"disorder",cancellation.Token);
                Check(result.Cancelled&&!result.Enabled&&preCancelled.Starts.Count==0&&preCancelled.StopCount==0,"pre-cancellation no filter/network activity");
            }
            using(var cancellation=new CancellationTokenSource())
            {
                FakeRuntime cancelledBaseline=new FakeRuntime(baseline);cancelledBaseline.AfterStart=delegate{cancellation.Cancel();};
                result=Execute(cancelledBaseline,"disorder",cancellation.Token);
                Check(result.Cancelled&&!result.Enabled&&!cancelledBaseline.IsRunning&&cancelledBaseline.StopCount==1,"cancelled filtered-baseline start stopped safely");
                Check(cancelledBaseline.CheckCount==0&&result.Trials.Count==0,"cancelled baseline never attributed as candidate evidence");
            }
            using(var cancellation=new CancellationTokenSource())
            {
                FakeRuntime cancelledProbe=new FakeRuntime(baseline);cancelledProbe.AfterCheck=delegate(int count){cancellation.Cancel();};
                result=Execute(cancelledProbe,"fake-split",cancellation.Token);
                Check(result.Cancelled&&!result.Enabled&&!cancelledProbe.IsRunning&&cancelledProbe.StopCount==1,"cancellation while checking filtered baseline cleaned up");
            }
            using(var cancellation=new CancellationTokenSource())
            {
                FakeRuntime cancelledTrial=new FakeRuntime(baseline,ready);cancelledTrial.AfterCheck=delegate(int count){if(count==2)cancellation.Cancel();};
                result=Execute(cancelledTrial,"disorder",cancellation.Token);
                Check(result.Cancelled&&!result.Enabled&&!cancelledTrial.IsRunning&&cancelledTrial.StopCount==2,"cancelled first trial stops baseline and trial");
                Check(result.Trials.Count==0&&result.Strategy=="disorder","cancelled incomplete matrix not committed");
            }
            FakeRuntime crashed=new FakeRuntime(baseline);crashed.AfterStart=delegate{crashed.Running=false;};
            Reject(delegate{Execute(crashed,"disorder");},"failed baseline process surfaced");
            Check(!crashed.IsRunning&&crashed.StopCount==1&&crashed.CheckCount==0,"failed baseline start still cleaned up before probes");

            FakeRuntime diedAfterProbe=new FakeRuntime(ready);diedAfterProbe.AfterCheck=delegate(int count){diedAfterProbe.Running=false;};
            Reject(delegate{Execute(diedAfterProbe,"disorder");},"baseline process exit after ready probe matrix surfaced");
            Check(!diedAfterProbe.IsRunning&&diedAfterProbe.StopCount==1&&diedAfterProbe.CheckCount==1&&diedAfterProbe.Starts.Count==1,"ready public replies cannot claim live filter after process exits");

            FakeRuntime probeError=new FakeRuntime();probeError.CheckOverride=delegate(int count,CancellationToken token){throw new InvalidOperationException("synthetic probe error");};
            Reject(delegate{Execute(probeError,"disorder");},"baseline probe exception surfaced");
            Check(!probeError.IsRunning&&probeError.StopCount==1,"unexpected baseline probe failure stopped owned filter");

            FakeRuntime timeout=new FakeRuntime();timeout.CheckOverride=delegate(int count,CancellationToken token){return Wait(token);};
            result=Tuning.RunWithRuntimeAsync(timeout,"disorder",Methods,null,CancellationToken.None,1000,15).GetAwaiter().GetResult();
            Check(result.TimedOut&&!result.Cancelled&&!result.Enabled&&!timeout.IsRunning&&timeout.StopCount==1,"filtered baseline matrix timeout safely stops filter");
            Check(timeout.Starts.Count==1&&result.Trials.Count==0&&result.Strategy=="disorder","timed-out baseline never produces guessed method");
        }

        static void ScopedProfileEvidence()
        {
            // The receipt parser intentionally accepts only verified markers.
            // This marker/hash is synthetic input, not a real preflight claim.
            string fixtureHash=new string('a',64);
            foreach(string general in Methods)
            {
                var inherited=ProfileSummary.FromArguments(Profiles.Build(new ProfileSpec{Mode=HelperMode.Direct,
                    Domains="discord.com\nyoutube.com\nprivate-discord-fixture.invalid",Strategy=general}),null,true,fixtureHash);
                Check(inherited.Strategy==general&&inherited.DiscordStrategy=="","legacy inherited profile evidence retains general method");
                Check(!inherited.Fields().ContainsKey("DiscordStrategy"),"legacy schema does not acquire empty override field");
                Check(ProfileSummary.FromFields(inherited.Fields()).Strategy==general,"legacy receipt fields still roundtrip");
                foreach(string discord in Methods)
                foreach(bool voice in new[]{false,true})
                foreach(bool quic in new[]{false,true})
                {
                    var spec=new ProfileSpec{Mode=HelperMode.Direct,Domains="discord.com\nyoutube.com\nprivate-discord-fixture.invalid",
                        Strategy=general,DiscordStrategy=discord,DiscordVoice=voice,Quic=quic};
                    var summary=ProfileSummary.FromArguments(Profiles.Build(spec),null,true,fixtureHash);
                    Check(summary.Strategy==general&&summary.DiscordStrategy==discord,"actual arguments report separate general and Discord methods");
                    Check(summary.DiscordVoice==voice&&summary.Quic==quic,"scoped evidence retains actual optional native profile flags");
                    Check(summary.ToDisplayString().StartsWith(general+" · Discord "+discord+" ·",StringComparison.Ordinal),"visible evidence identifies both methods");
                    var fields=summary.Fields();
                    Check((string)fields["DiscordStrategy"]==discord,"scoped receipt includes override method");
                    var copy=ProfileSummary.FromFields(fields);
                    Check(copy.Strategy==general&&copy.DiscordStrategy==discord&&copy.ProfileCount==summary.ProfileCount,"typed receipt fields roundtrip separate methods");
                    var serializer=new JavaScriptSerializer();string json=serializer.Serialize(fields);
                    copy=ProfileSummary.FromFields(serializer.Deserialize<Dictionary<string,object>>(json));
                    Check(copy.Strategy==general&&copy.DiscordStrategy==discord&&copy.DiscordVoice==voice&&copy.Quic==quic,"JSON receipt fields roundtrip separate methods");
                    Check(!json.Contains("private-discord-fixture.invalid")&&!json.Contains("discord.com")&&!json.Contains("--lua-desync"),"lossy evidence does not export hostnames or arguments");
                }
            }
        }

        static async Task<List<CheckRow>> Wait(CancellationToken token)
        {await Task.Delay(60000,token);return new List<CheckRow>();}
        static TuningResult Execute(FakeRuntime runtime,string original,CancellationToken token=default(CancellationToken))
        {return Tuning.RunWithRuntimeAsync(runtime,original,Methods,null,token).GetAwaiter().GetResult();}
        static List<CheckRow> Rows(bool? api,bool? manifest,bool? updater,bool? cdnTls,bool? hello,bool? youtube)
        {
            var rows=new List<CheckRow>{Row("control.microsoft","control",true),Row("control.cloudflare","control",true),
                Row("discord.gateway","discord",hello),Row("youtube.web","youtube",youtube),Row("discord.voice","discord",null)};
            AddFamily(rows,"v4",true,api,manifest,updater,cdnTls);
            AddFamily(rows,"v6",null,null,null,null,null);
            return rows;
        }
        static void AddFamily(List<CheckRow> rows,string family,bool? dns,bool? api,bool? manifest,bool? updater,bool? cdnTls)
        {
            foreach(string target in new[]{"api","manifest","updater","cdn"})
            {
                string key="discord.desktop."+target+"."+family;
                rows.Add(Row(key+".dns","discord",dns));rows.Add(Row(key+".tcp","discord",true));
                rows.Add(Row(key+".tls","discord",target=="cdn"?cdnTls:true));
                rows.Add(Row(key,"discord",target=="api"?api:target=="manifest"?manifest:target=="updater"?updater:null));
            }
        }
        static List<CheckRow> Clone(IList<CheckRow> rows)
        {return rows.Select(x=>new CheckRow{Key=x.Key,ServiceId=x.ServiceId,Passed=x.Passed,Detail=x.Detail}).ToList();}
        static CheckRow Row(string key,string service,bool? passed)
        {return new CheckRow{Key=key,ServiceId=service,Passed=passed,Detail="synthetic fixed-target fixture, not actual diagnostic"};}
        static void Check(bool value,string message)
        {checks++;if(!value)throw new Exception("FAIL: "+message);}
        static void Reject(Action action,string label)
        {bool rejected=false;try{action();}catch(InvalidOperationException){rejected=true;}Check(rejected,label);}

        sealed class FakeRuntime:Tuning.Runtime
        {
            readonly Queue<List<CheckRow>> matrices;
            internal readonly List<string> Starts=new List<string>(),Events=new List<string>();
            internal readonly List<bool> ChecksWhileRunning=new List<bool>();
            internal bool Running,Vpn;
            internal string Route="stable coarse summary",Current;
            internal int StopCount,CheckCount,AcceptCount;
            internal Action AfterStart;
            internal Action<int> AfterCheck;
            internal Func<int,CancellationToken,Task<List<CheckRow>>> CheckOverride;
            internal Func<IList<CheckRow>,int,bool> AcceptOverride;
            internal FakeRuntime(params List<CheckRow>[] matrices){this.matrices=new Queue<List<CheckRow>>(matrices);}
            internal override bool BaselineUsesFilter{get{return true;}}
            internal override string Scope{get{return "discord";}}
            internal override bool CandidateAcceptable(IList<CheckRow> rows)
            {AcceptCount++;return AcceptOverride==null?Tuning.DiscordBootstrapReady(rows):AcceptOverride(rows,AcceptCount);}
            internal override bool IsRunning{get{return Running;}}
            internal override bool HasLikelyVpn{get{return Vpn;}}
            internal override string RouteSummary{get{return Route;}}
            internal override Task StartAsync(string method,CancellationToken token)
            {token.ThrowIfCancellationRequested();Starts.Add(method);Current=method;Events.Add("start:"+method);Running=true;if(AfterStart!=null)AfterStart();return Task.FromResult(0);}
            internal override void Stop(){StopCount++;Events.Add("stop:"+Current);Running=false;}
            internal override Task SettleAsync(CancellationToken token){token.ThrowIfCancellationRequested();return Task.FromResult(0);}
            internal override Task<List<CheckRow>> CheckAsync(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();CheckCount++;ChecksWhileRunning.Add(Running);Events.Add("check:"+Current);
                Task<List<CheckRow>> result;
                if(CheckOverride!=null)result=CheckOverride(CheckCount,token);
                else
                {
                    if(matrices.Count==0)throw new InvalidOperationException("Unexpected extra synthetic probe.");
                    result=Task.FromResult(matrices.Dequeue());
                }
                if(AfterCheck!=null)AfterCheck(CheckCount);return result;
            }
        }
    }
}
