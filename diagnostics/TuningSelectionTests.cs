using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LocalLink
{
    // Pure matrices and a fake runtime only: no Engine construction, native
    // process/adapter observations, UAC, WinDivert, DNS or network requests.
    internal static class TuningSelectionTests
    {
        static int checks;
        static readonly string[] Methods={"split","sni-split","disorder","fake-split","fake-disorder"};
        static int Main()
        {
            try{Run();Console.WriteLine("TUNING OFFLINE TESTS PASSED: "+checks+". Fake runtime only; no filter/network.");return 0;}
            catch(Exception error){Console.Error.WriteLine(error);return 1;}
        }
        internal static void Run()
        {
            List<CheckRow> baseline=Rows(true,true,false,false,null);
            List<CheckRow> partial=Rows(true,true,true,false,null);
            List<CheckRow> all=Rows(true,true,true,true,null);
            Check(Tuning.IsImprovement(baseline,partial),"partial known improvement allowed");
            Check(!Tuning.IsImprovement(baseline,Rows(false,true,true,true,null)),"Microsoft regression rejects candidate");
            Check(!Tuning.IsImprovement(baseline,Rows(true,false,true,true,null)),"Cloudflare regression rejects candidate");
            Check(!Tuning.IsImprovement(baseline,Rows(null,true,true,true,null)),"previous success becoming unknown rejects candidate");
            Check(!Tuning.IsImprovement(baseline,Rows(true,true,false,false,true)),"unknown manual success cannot establish improvement");
            Check(!Tuning.IsImprovement(baseline,Rows(true,true,null,null,null)),"unknown failed checks are not wins");
            Check(!Tuning.IsImprovement(baseline,baseline),"identical matrix not improvement");
            List<CheckRow> failedControl=Rows(false,true,false,false,null);
            Check(!Tuning.IsImprovement(failedControl,baseline),"control-only recovery cannot choose a target TLS method");
            Check(Tuning.IsImprovement(failedControl,partial),"target gain with no control regression accepted");
            Check(Tuning.IsImprovement(new List<CheckRow>{new CheckRow{Key="legacy",Passed=false}},new List<CheckRow>{new CheckRow{Key="legacy",Passed=true}}),"legacy missing service ID remains target evidence");
            Check(Tuning.Score(failedControl,baseline).Gains==1&&Tuning.Score(failedControl,baseline).TargetGains==0,"control gain tracked but never sole selection gain");
            Check(Tuning.IsImprovement(baseline,all.AsEnumerable().Reverse().ToList()),"matrix key ordering does not change evidence");
            Check(!Tuning.IsImprovement(baseline,all.Take(4).ToList()),"missing matrix row rejected");
            var extra=all.ToList();extra.Add(new CheckRow{Key="extra",Passed=true});
            Check(!Tuning.IsImprovement(baseline,extra),"extra fabricated success rejected");
            var duplicate=all.ToList();duplicate[1]=duplicate[0];
            Check(!Tuning.IsImprovement(baseline,duplicate),"duplicate matrix key rejected");
            var missingKey=all.ToList();missingKey[1]=new CheckRow{Passed=true};
            Check(!Tuning.IsImprovement(baseline,missingKey),"missing key rejected");
            Check(!Tuning.IsImprovement(null,all),"null baseline rejected");
            Check(!Tuning.IsImprovement(baseline,null),"null candidate rejected");
            Check(!Tuning.IsImprovement(new List<CheckRow>(),new List<CheckRow>()),"empty matrix rejected");
            Tuning.MatrixScore unknown=Tuning.Score(baseline,Rows(true,true,true,true,true));
            Check(unknown.Valid&&unknown.Passed==4&&unknown.Failed==0&&unknown.Unknown==1&&unknown.Gains==2,"unknown remains uncounted when candidate claims success");
            Check(unknown.Services.Single(x=>x.ServiceId=="discord").Unknown==1,"unknown preserved in per-service counts");
            var privateRow=Rows(true,true,true,true,null);privateRow[4].ServiceId="private-domain.test";
            Check(!Tuning.Score(baseline,privateRow).Services.Any(x=>x.ServiceId=="private-domain.test"),"metadata IDs come from fixed baseline whitelist");

            FakeRuntime best=new FakeRuntime(baseline,partial,all,Rows(false,true,true,true,null),Rows(true,true,false,true,null),baseline,all);
            TuningResult result=Execute(best);
            Check(result.Enabled&&result.Verified&&result.Strategy=="sni-split","best full matrix wins, not first partial");
            Check(best.Starts.SequenceEqual(new[]{"split","sni-split","disorder","fake-split","fake-disorder","sni-split"}),"every method checked before winner verification");
            Check(best.CheckCount==7&&best.StopCount==5&&best.IsRunning,"all trials stopped and verified winner left running");
            Check(result.Trials.Count==6&&result.Trials.Count(x=>x.Verified)==1,"trial history retained including verified row");
            Check(result.Trials.Take(5).All(x=>x.Stage=="trial")&&result.Trials.Last().Stage=="verification","safe fixed metadata stages");
            Check(result.BaselinePassed==2&&result.BaselineFailed==2&&result.BaselineUnknown==1,"baseline aggregate preserved");
            Check(result.BaselineServices.Single(x=>x.ServiceId=="control").Passed==2,"baseline controls retained");
            Check(result.Limits.Contains("Telegram")&&result.Limits.Contains("не доказывает"),"limitations explicit including coarse route guard");
            string json=new JavaScriptSerializer().Serialize(result);
            Check(!json.Contains("private-token")&&!json.Contains("private-domain.test")&&!json.Contains("Detail")&&!json.Contains("127.0.0.1"),"export metadata omits raw probe details and custom domain");
            best.Stop();

            FakeRuntime stableTie=new FakeRuntime(baseline,all,all,baseline,baseline,baseline,all);
            result=Execute(stableTie);
            Check(result.Strategy=="split"&&stableTie.Starts.Last()=="split","equal matrices prefer stable method order");
            stableTie.Stop();

            FakeRuntime lowerFailures=new FakeRuntime(baseline,Rows(true,true,true,null,null),partial,baseline,baseline,baseline,partial);
            result=Execute(lowerFailures);
            Check(result.Enabled&&result.Strategy=="sni-split","equal pass counts prefer fewer known-unknown results after failures tie rule");
            lowerFailures.Stop();

            FakeRuntime unstable=new FakeRuntime(baseline,partial,all,baseline,baseline,baseline,partial,partial);
            result=Execute(unstable);
            Check(result.Enabled&&result.Verified&&result.Strategy=="split","failed best repeat falls back to next ranked candidate");
            Check(unstable.Starts.Count==7&&unstable.StopCount==6,"fallback bounded and failed verification stopped");
            Check(!result.Trials[5].Verified&&result.Trials[5].Improved&&result.Trials[6].Verified,"partial best repeat not incorrectly verified");
            Check(result.Summary.Contains("Частичное"),"partial outcome honestly labeled");
            unstable.Stop();

            FakeRuntime none=new FakeRuntime(baseline,baseline,baseline,baseline,baseline,baseline);
            result=Execute(none,"fake-split");
            Check(!result.Enabled&&!result.Verified&&result.Strategy=="fake-split"&&!none.IsRunning,"no improvement keeps original strategy and filter off");
            Check(none.Starts.Count==5&&none.StopCount==5&&result.Trials.Count==5,"no early partial stop and no pointless verification");

            FakeRuntime controlRecovery=new FakeRuntime(failedControl,baseline,baseline,baseline,baseline,baseline);
            result=Execute(controlRecovery,"disorder");
            Check(!result.Enabled&&!result.Verified&&result.Strategy=="disorder"&&!controlRecovery.IsRunning,"control-only recovery leaves filter off and original method");
            Check(controlRecovery.Starts.Count==5&&controlRecovery.StopCount==5&&result.Trials.Count==5&&result.Trials.All(x=>!x.Improved),"control-only gains are neither ranked nor verified nor reported improved");

            FakeRuntime noRepeat=new FakeRuntime(baseline,partial,baseline,baseline,baseline,baseline,baseline);
            result=Execute(noRepeat,"disorder");
            Check(!result.Enabled&&!result.Verified&&result.Strategy=="disorder"&&!noRepeat.IsRunning,"no repeatable winner stays off");
            Check(noRepeat.Starts.Count==6&&noRepeat.StopCount==6,"failed repeat cleaned up");

            FakeRuntime unknownOnly=new FakeRuntime(Rows(true,true,null,null,null));
            result=Execute(unknownOnly);
            Check(!result.Enabled&&unknownOnly.Starts.Count==0,"no known failures does not start trials");

            FakeRuntime vpn=new FakeRuntime();vpn.Vpn=true;
            result=Execute(vpn);
            Check(!result.Enabled&&vpn.CheckCount==0&&vpn.Starts.Count==0&&vpn.StopCount==0,"initial VPN hint never mutates engine");

            FakeRuntime routeBaseline=new FakeRuntime(baseline);routeBaseline.AfterCheck=delegate(int count){routeBaseline.Route="changed";};
            result=Execute(routeBaseline);
            Check(!result.Enabled&&routeBaseline.Starts.Count==0&&result.Summary.Contains("Изменились"),"changed coarse route after baseline aborts before start");

            FakeRuntime routeTrial=new FakeRuntime(baseline,all);routeTrial.AfterCheck=delegate(int count){if(count==2)routeTrial.Route="changed";};
            result=Execute(routeTrial,"disorder");
            Check(!result.Enabled&&!routeTrial.IsRunning&&routeTrial.Starts.Count==1&&routeTrial.StopCount==1&&result.Strategy=="disorder","route change during trial cleans up, no winner");
            Check(result.Trials.Count==0,"route-changed sample not attributed to strategy");

            FakeRuntime vpnStart=new FakeRuntime(baseline);vpnStart.AfterStart=delegate{vpnStart.Vpn=true;};
            result=Execute(vpnStart);
            Check(!result.Enabled&&!vpnStart.IsRunning&&vpnStart.CheckCount==1&&vpnStart.StopCount==1,"VPN appearing after start stops before trial probes");

            using(CancellationTokenSource cancelled=new CancellationTokenSource())
            {
                cancelled.Cancel();FakeRuntime initial=new FakeRuntime();
                result=Execute(initial,"disorder",cancelled.Token);
                Check(result.Cancelled&&!result.Enabled&&result.Strategy=="disorder","pre-cancelled returns explicit safe result");
                Check(initial.CheckCount==0&&initial.Starts.Count==0&&initial.StopCount==0,"pre-cancelled makes no runtime mutation");
            }
            using(CancellationTokenSource cancelled=new CancellationTokenSource())
            {
                FakeRuntime duringStart=new FakeRuntime(baseline);duringStart.AfterStart=delegate{cancelled.Cancel();};
                result=Execute(duringStart,"fake-split",cancelled.Token);
                Check(result.Cancelled&&!result.Enabled&&!duringStart.IsRunning&&result.Strategy=="fake-split","cancellation after start cannot leave filter enabled");
                Check(duringStart.StopCount==1&&duringStart.CheckCount==1,"cancelled trial cleanup before second matrix");
            }
            using(CancellationTokenSource cancelled=new CancellationTokenSource())
            {
                FakeRuntime duringCheck=new FakeRuntime(baseline,partial,all);duringCheck.AfterCheck=delegate(int count){if(count==3)cancelled.Cancel();};
                result=Execute(duringCheck,"disorder",cancelled.Token);
                Check(result.Cancelled&&!result.Enabled&&!duringCheck.IsRunning&&duringCheck.StopCount==2,"cancellation in a later trial stops all owned work");
                Check(result.Trials.Count==1&&result.Trials[0].Strategy=="split","cancellation retains only completed prior trial evidence");
            }
            using(CancellationTokenSource cancelled=new CancellationTokenSource())
            {
                FakeRuntime duringVerification=new FakeRuntime(baseline,partial,baseline,baseline,baseline,baseline,partial);
                duringVerification.AfterCheck=delegate(int count){if(count==7)cancelled.Cancel();};
                result=Execute(duringVerification,"fake-split",cancelled.Token);
                Check(result.Cancelled&&!result.Enabled&&!result.Verified&&!duringVerification.IsRunning&&result.Strategy=="fake-split","verification cancellation cannot commit winner");
                Check(result.Trials.Count==5&&duringVerification.StopCount==6,"cancelled verification preserves history and cleanup");
            }
            FakeRuntime crashed=new FakeRuntime(baseline);crashed.AfterStart=delegate{crashed.Running=false;};
            bool rejected=false;
            try{Execute(crashed);}catch(InvalidOperationException){rejected=true;}
            Check(rejected&&!crashed.IsRunning&&crashed.StopCount==1,"failed engine start cleaned up on error");

            FakeRuntime failingProbe=new FakeRuntime(baseline);failingProbe.CheckOverride=delegate(int count,CancellationToken token)
            {if(count==2)throw new InvalidOperationException("private-token");return Task.FromResult(baseline);};
            rejected=false;try{Execute(failingProbe);}catch(InvalidOperationException){rejected=true;}
            Check(rejected&&!failingProbe.IsRunning&&failingProbe.StopCount==1,"probe error cleans up trial");

            FakeRuntime timeout=new FakeRuntime(baseline);timeout.CheckOverride=delegate(int count,CancellationToken token)
            {return count==1?Task.FromResult(baseline):Wait(token);};
            result=Tuning.RunWithRuntimeAsync(timeout,"disorder",Methods,null,CancellationToken.None,1000,15).GetAwaiter().GetResult();
            Check(result.TimedOut&&!result.Cancelled&&!result.Enabled&&!timeout.IsRunning&&result.Strategy=="disorder","matrix timeout returns safe off partial result");
            Check(timeout.StopCount==1&&result.Trials.Count==0,"incomplete timeout matrix never selected");

            FakeRuntime deadline=new FakeRuntime();deadline.CheckOverride=delegate(int count,CancellationToken token){return Wait(token);};
            result=Tuning.RunWithRuntimeAsync(deadline,"split",Methods,null,CancellationToken.None,15,1000).GetAwaiter().GetResult();
            Check(result.TimedOut&&!result.Cancelled&&!result.Enabled&&deadline.Starts.Count==0,"global deadline bounded before trials");

            foreach(string[] invalid in new[]{new string[0],new[]{"split","split"},new[]{"private-token"},new[]{"split","sni-split","disorder","fake-split","fake-disorder","split"}})
            {
                FakeRuntime untouched=new FakeRuntime();rejected=false;
                try{Tuning.RunWithRuntimeAsync(untouched,"split",invalid,null,CancellationToken.None).GetAwaiter().GetResult();}catch(ArgumentException){rejected=true;}
                Check(rejected&&untouched.Starts.Count==0&&untouched.CheckCount==0,"invalid methods rejected before runtime");
            }
        }
        static async Task<List<CheckRow>> Wait(CancellationToken token)
        {await Task.Delay(60000,token);return new List<CheckRow>();}
        static TuningResult Execute(FakeRuntime runtime,string original="split",CancellationToken token=default(CancellationToken))
        {return Tuning.RunWithRuntimeAsync(runtime,original,Methods,null,token).GetAwaiter().GetResult();}
        static List<CheckRow> Rows(bool? microsoft,bool? cloudflare,bool? discord,bool? youtube,bool? voice)
        {
            return new List<CheckRow>{Row("control.microsoft","control",microsoft),Row("control.cloudflare","control",cloudflare),
                Row("discord.gateway","discord",discord),Row("youtube.web","youtube",youtube),Row("discord.voice","discord",voice)};
        }
        static CheckRow Row(string key,string service,bool? passed)
        {return new CheckRow{Key=key,ServiceId=service,Passed=passed,Detail="private-token https://private-domain.test/ 127.0.0.1",Name="private-token",State="private-token"};}
        static void Check(bool value,string text)
        {checks++;if(!value)throw new Exception("FAIL: "+text);}
        sealed class FakeRuntime:Tuning.Runtime
        {
            readonly Queue<List<CheckRow>> matrices;
            internal readonly List<string> Starts=new List<string>();
            internal bool Running,Vpn;
            internal string Route="stable coarse summary";
            internal int StopCount,CheckCount;
            internal Action AfterStart;
            internal Action<int> AfterCheck;
            internal Func<int,CancellationToken,Task<List<CheckRow>>> CheckOverride;
            internal FakeRuntime(params List<CheckRow>[] matrices){this.matrices=new Queue<List<CheckRow>>(matrices);}
            internal override bool IsRunning{get{return Running;}}
            internal override bool HasLikelyVpn{get{return Vpn;}}
            internal override string RouteSummary{get{return Route;}}
            internal override Task StartAsync(string method,CancellationToken token)
            {token.ThrowIfCancellationRequested();Starts.Add(method);Running=true;if(AfterStart!=null)AfterStart();return Task.FromResult(0);}
            internal override void Stop(){StopCount++;Running=false;}
            internal override Task SettleAsync(CancellationToken token){token.ThrowIfCancellationRequested();return Task.FromResult(0);}
            internal override Task<List<CheckRow>> CheckAsync(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();CheckCount++;
                Task<List<CheckRow>> result;
                if(CheckOverride!=null)result=CheckOverride(CheckCount,token);
                else
                {
                    if(matrices.Count==0)throw new InvalidOperationException("Unexpected extra fake probe.");
                    result=Task.FromResult(matrices.Dequeue());
                }
                if(AfterCheck!=null)AfterCheck(CheckCount);
                return result;
            }
        }
    }
}
