using System;
using System.IO;
using System.Threading;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
namespace LocalLink
{
    static class TestMain
    {
        static int Main(string[] args)
        {
            if(args.Length==1&&args[0]=="--wait-for-lifetime-test") { Thread.Sleep(60000); return 0; }
            if(args.Length==1&&args[0]=="--engine-arguments")
            {try{ValidateEngineArguments();return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
            try
            {
                int result=ProfileTests.Run(); if(result!=0)return result;
                result=ServiceTests.Run();if(result!=0)return result;
                result=PublicListTests.Run();if(result!=0)return result;
                var baseline=new List<CheckRow>{new CheckRow{Key="a",Passed=true},new CheckRow{Key="b",Passed=false},new CheckRow{Key="voice",Passed=null}};
                Check(Tuning.IsImprovement(baseline,new List<CheckRow>{new CheckRow{Key="a",Passed=true},new CheckRow{Key="b",Passed=true},new CheckRow{Key="voice",Passed=null}}),"tuning improvement");
                Check(!Tuning.IsImprovement(baseline,new List<CheckRow>{new CheckRow{Key="a",Passed=false},new CheckRow{Key="b",Passed=true},new CheckRow{Key="voice",Passed=null}}),"tuning regression rejected");
                Check(!Tuning.IsImprovement(baseline,baseline),"same scores are not improvement");
                Check(!Tuning.IsImprovement(baseline,new List<CheckRow>{new CheckRow{Key="a",Passed=true},new CheckRow{Key="b",Passed=false},new CheckRow{Key="voice",Passed=true}}),"manual result cannot select a method");
                Check(!Tuning.IsImprovement(baseline,new List<CheckRow>{new CheckRow{Key="b",Passed=true}}),"missing checks rejected");
                var manualOnly=new List<CheckRow>{new CheckRow{Key="voice",Passed=null},new CheckRow{Key="telegram-session",Passed=null}};
                Check(!Tuning.IsImprovement(manualOnly,new List<CheckRow>{new CheckRow{Key="voice",Passed=true},new CheckRow{Key="telegram-session",Passed=true}}),"unknown sessions cannot establish an improvement");
                var vpnBlocked=Tuning.BaselineOutcome(baseline,true,"disorder");
                Check(vpnBlocked!=null&&!vpnBlocked.Enabled&&vpnBlocked.Strategy=="disorder"&&vpnBlocked.Summary.Contains("VPN"),"VPN hints block trials even with failed checks");
                Check(Tuning.BaselineOutcome(baseline,false,"split")==null,"failed control checks without VPN hints permit trials");
                var controlsPassed=new List<CheckRow>{new CheckRow{Key="https",Passed=true},new CheckRow{Key="voice",Passed=null}};
                var accessible=Tuning.BaselineOutcome(controlsPassed,false,"split");
                Check(accessible!=null&&!accessible.Enabled&&accessible.Summary.Contains("не подтверждены"),"accessible controls do not confirm manual sessions");
                var unknown=Tuning.BaselineOutcome(manualOnly,false,"split");
                Check(unknown!=null&&!unknown.Enabled&&unknown.Summary.Contains("автоматических результатов нет"),"manual-only checks do not start trials");
                Check(Tuning.BaselineOutcome(controlsPassed,true,"split").Summary.Contains("VPN"),"VPN warning takes priority over accessible controls");
                Check(Tuning.BaselineOutcome(manualOnly,true,"split").Summary.Contains("VPN"),"VPN warning takes priority over unknown sessions");
                Check(Tuning.BaselineOutcome(null,false,"split")!=null,"missing baseline cannot start trials");
                var normalized=AppSettings.Normalize(new AppSettings{Mode=99,Port=0,Strategy="bad",Services=new List<string>{"discord","discord","bad"},CustomDomains="--cmd=bad"});
                Check(normalized.Mode==0&&normalized.Port==443&&normalized.Strategy=="split"&&normalized.Services.Count==1&&normalized.CustomDomains=="","invalid preferences normalize without disk writes");
                foreach(string appearance in new[]{"system","light","dark"})
                    Check(AppSettings.Normalize(new AppSettings{Appearance=appearance}).Appearance==appearance,"valid appearance preference preserved: "+appearance);
                Check(AppSettings.Normalize(new AppSettings{Appearance="bad"}).Appearance=="system","invalid appearance falls back to Windows");
                Check(AppSettings.Normalize(new AppSettings{Appearance=null}).Appearance=="system","missing appearance falls back to Windows");
                Check(!new AppSettings().UsePublicLists,"fresh installation does not require a downloaded public list");
                Check(AppSettings.Normalize(new AppSettings{UsePublicLists=true}).UsePublicLists,"existing explicit public-list preference preserved");
                var serializer=new System.Web.Script.Serialization.JavaScriptSerializer();
                Check(AppSettings.Normalize(serializer.Deserialize<AppSettings>("{\"Mode\":0,\"Strategy\":\"split\"}")).Appearance=="system","old settings schema preserves Windows appearance default");
                Check(AppSettings.Normalize(serializer.Deserialize<AppSettings>(serializer.Serialize(new AppSettings{Appearance="dark"}))).Appearance=="dark","appearance JSON roundtrip without disk writes");
                Check(NetworkInfo.ClassifyFailure(new System.Net.WebException("private",System.Net.WebExceptionStatus.NameResolutionFailure)).StartsWith("DNS:"),"DNS classification without private message");
                Check(NetworkInfo.ClassifyFailure(new Exception("private" )).IndexOf("private",StringComparison.Ordinal)<0,"exception details not disclosed");
                Check(Engine.Quote("hello")=="\"hello\"","simple quoting");
                Check(Engine.Quote("a b")=="\"a b\"","space quoting");
                Check(Engine.Quote("C:\\x\\")=="\"C:\\x\\\\\"","trailing slash quoting");
                Check(Engine.Quote("a\"b")=="\"a\\\"b\"","embedded quote escaping");
                using(var e=new Engine(AppDomain.CurrentDomain.BaseDirectory))
                {
                    Check(!e.IsRunning,"initially stopped"); e.Stop(); e.Stop();Console.WriteLine(e.VerifyFiles());
                    var launch=typeof(Engine).GetMethod("LaunchOwnedProcess",BindingFlags.NonPublic|BindingFlags.Instance);
                    launch.Invoke(e,new object[]{Assembly.GetExecutingAssembly().Location,new List<string>{"--wait-for-lifetime-test"},AppDomain.CurrentDomain.BaseDirectory});
                    Check(e.IsRunning,"job owns benign test child");
                    var owned=(Process)typeof(Engine).GetField("child",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(e);
                    int pid=owned.Id;e.Stop();bool exited;
                    try{using(var p=Process.GetProcessById(pid))exited=p.HasExited||p.WaitForExit(3000);}catch(ArgumentException){exited=true;}
                    Check(exited,"Stop terminates own benign child");Check(!e.IsRunning,"stopped state");e.Stop();
                }
                var cancelled=new CancellationToken(true);
                var cancellationResult = NetworkInfo.ProbeAsync("example.com",cancelled).GetAwaiter().GetResult();
                Check(!cancellationResult.TlsReached && cancellationResult.Error == "Cancelled.", "pre-cancelled probe / no network");
                foreach(string host in new[]{"https://example.com/path","example.com;cmd","127.0.0.1","user:pass@example.com"})
                {
                    bool rejected=false;
                    try{var r=NetworkInfo.ProbeAsync(host,CancellationToken.None).GetAwaiter().GetResult();rejected=!r.TlsReached;}
                    catch(ArgumentException){rejected=true;}catch(OperationCanceledException){rejected=true;}
                    Check(rejected,"invalid host / no network");
                }
                Console.WriteLine("ALL OFFLINE TESTS PASSED. No filters, driver starts or HTTP requests."); return 0;
            }
            catch(Exception e){Console.Error.WriteLine(e);return 1;}
        }
        static void Check(bool condition,string name){if(!condition)throw new Exception("FAIL "+name);Console.WriteLine("PASS "+name);}
        static void ValidateEngineArguments()
        {
            string root=AppDomain.CurrentDomain.BaseDirectory;
            using(var wrapper=new Engine(root))Console.WriteLine(wrapper.VerifyFiles());
            if(!Engine.IsAdmin())throw new InvalidOperationException("Upstream winws2.exe requires elevation even for --dry-run. No elevation is requested automatically. Argument validation was NOT run; no driver/filter was started.");
            var cases=new List<ProfileSpec>();
            foreach(string method in Profiles.TlsStrategies())
            {
                foreach(bool quic in new[]{false,true})cases.Add(new ProfileSpec{Mode=HelperMode.Direct,Domains="example.org\nexample.com",Strategy=method,Quic=quic});
                foreach(string ip in new[]{"192.0.2.7","2001:db8::7"})cases.Add(new ProfileSpec{Mode=HelperMode.VpnTls,Endpoint=ip,Port=443,Strategy=method});
            }
            foreach(string ip in new[]{"192.0.2.7","2001:db8::7"})cases.Add(new ProfileSpec{Mode=HelperMode.VpnWireGuard,Endpoint=ip,Port=51820,Strategy="wireguard"});
            foreach(var spec in cases)
            {
                var command=new System.Text.StringBuilder("--dry-run");
                foreach(string value in Profiles.Build(spec))command.Append(' ').Append(Engine.Quote(value));
                var info=new ProcessStartInfo(Path.Combine(root,"engine","winws2.exe"),command.ToString());
                info.WorkingDirectory=Path.Combine(root,"engine");info.UseShellExecute=false;info.CreateNoWindow=true;info.RedirectStandardOutput=true;info.RedirectStandardError=true;
                using(var p=Process.Start(info))
                {
                    var stdout=p.StandardOutput.ReadToEndAsync();var stderr=p.StandardError.ReadToEndAsync();
                    if(!p.WaitForExit(10000)){p.Kill();throw new Exception("Argument-only validation timed out.");}
                    string output=stdout.GetAwaiter().GetResult()+stderr.GetAwaiter().GetResult();
                    if(p.ExitCode!=0)throw new Exception("FAIL "+spec.Mode+" "+spec.Strategy+"\n"+output);
                    Console.WriteLine("PASS engine --dry-run: "+spec.Mode+" "+spec.Strategy+" QUIC="+spec.Quic+" "+spec.Endpoint);
                }
            }
            Console.WriteLine("All "+cases.Count+" argument-only checks passed. No traffic interception requested.");
        }
    }
}
