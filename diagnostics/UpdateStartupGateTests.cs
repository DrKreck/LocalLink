using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace LocalLink
{
    internal static class UpdateStartupGateTests
    {
        static int checks;
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        [return:MarshalAs(UnmanagedType.U1)]static extern bool CreateSymbolicLink(string link,string target,int flags);
        static int Main()
        {
            try
            {
                Check(AppUpdateHealth.CreateStartupGate(null)==null,"ordinary startup has no updater gate");
                Check(AppUpdateHealth.CreateStartupGate(new string[0])==null&&!AppUpdateHealth.Acknowledge(new string[0]),"non-health args never acknowledge or gate an update");
                Check(!AppUpdateHealth.SupportsStartupGate("0.8.5"),"legacy 0.8.5 uses visible-window health");
                Check(AppUpdateHealth.SupportsStartupGate("0.9.0")&&AppUpdateHealth.SupportsStartupGate("0.10.0")&&AppUpdateHealth.SupportsStartupGate("1.0.0"),"modern updates and rollbacks use the health protocol");
                ValidateMarkers();ValidateBinding();ValidateReparse();
                Console.WriteLine("Update startup gate: "+checks+" offline assertions passed. Own files/locks only; no processes, update installation, UAC, network or user paths.");
                return 0;
            }
            catch(Exception error){Console.Error.WriteLine(error);return 1;}
        }
        sealed class Fixture
        {
            internal string Root,Target,Directory,PlanPath,PlanHash;
            internal AppUpdatePlan Plan;
            internal byte[] PlanBytes,Executable;
            internal AppUpdateStartupGate Gate;
            internal string PathOf(string name){return Path.Combine(Directory,name);}
            internal void Write(string name,string text){File.WriteAllText(PathOf(name),text,new UTF8Encoding(false));}
        }
        static Fixture NewFixture()
        {
            string folder=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"sg",Guid.NewGuid().ToString("N").Substring(0,10));
            var fixture=new Fixture{Root=Path.Combine(folder,"updates"),Target=Path.Combine(folder,"installed"),Executable=Encoding.UTF8.GetBytes("OWN SYNTHETIC INCOMING IMAGE - NEVER EXECUTE")};
            fixture.Directory=Path.Combine(fixture.Root,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture.Directory);Directory.CreateDirectory(fixture.Target);
            File.WriteAllBytes(Path.Combine(fixture.Target,"LocalLink.exe"),fixture.Executable);
            string hash=AppUpdateFiles.Hash(fixture.Executable);
            var states=AppUpdateFiles.Names.Select(name=>new AppUpdateFileState{Name=name,Exists=name=="LocalLink.exe",Bytes=name=="LocalLink.exe"?fixture.Executable.Length:0,Sha256=name=="LocalLink.exe"?hash:""}).ToArray();
            fixture.Plan=new AppUpdatePlan{Schema=1,Directory=fixture.Directory,Target=fixture.Target,Version="0.9.0",PreviousVersion="0.8.5",ParentId=Process.GetCurrentProcess().Id,
                ParentStartedUtcTicks=DateTime.UtcNow.Ticks,ParentSha256=hash,HealthToken=Guid.NewGuid().ToString("N"),Incoming=states,Previous=states};
            fixture.PlanBytes=AppUpdateFiles.Json(fixture.Plan);fixture.PlanPath=fixture.PathOf("plan.json");fixture.PlanHash=AppUpdateFiles.Hash(fixture.PlanBytes);
            File.WriteAllBytes(fixture.PlanPath,fixture.PlanBytes);fixture.Gate=new AppUpdateStartupGate(fixture.Plan);
            return fixture;
        }
        static void Ready(Fixture fixture)
        {fixture.Write("helper.lock","");fixture.Write("healthy",fixture.Plan.HealthToken);fixture.Write("status.installed","installed");}
        static void ValidateMarkers()
        {
            var fixture=NewFixture();
            Check(!File.Exists(fixture.PathOf("healthy")),"constructing a gate does not acknowledge health");
            Check(!fixture.Gate.CanResume(),"startup cannot resume before any helper markers exist");
            Check(!File.Exists(fixture.PathOf("helper.lock")),"gate never creates a missing helper lock");
            fixture.Write("healthy",fixture.Plan.HealthToken);fixture.Write("status.installed","installed");
            Check(!fixture.Gate.CanResume(),"healthy and installed markers alone do not imply helper exit");
            using(var held=new FileStream(fixture.PathOf("helper.lock"),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None))
                Check(!fixture.Gate.CanResume(),"exclusive helper lock prevents automatic connection and elevation");
            Check(fixture.Gate.CanResume(),"valid health and released helper lock permit normal startup");
            using(var exclusive=new FileStream(fixture.PathOf("helper.lock"),FileMode.Open,FileAccess.Read,FileShare.None))
                Check(exclusive.Length==0,"CanResume does not hold the helper lock beyond its call");
            fixture.Write("healthy",new string('a',32));Check(!fixture.Gate.CanResume(),"wrong health token keeps startup blocked");
            fixture.Write("healthy",new string('a',129));Check(!fixture.Gate.CanResume(),"oversized health marker fails closed");
            fixture.Write("healthy",fixture.Plan.HealthToken);File.Delete(fixture.PathOf("status.installed"));
            Check(!fixture.Gate.CanResume(),"released helper without an installed marker is not enough");
            Directory.CreateDirectory(fixture.PathOf("status.installed"));Check(!fixture.Gate.CanResume(),"a directory cannot stand in for the installed marker");
            Directory.Delete(fixture.PathOf("status.installed"));fixture.Write("status.installed","");Check(!fixture.Gate.CanResume(),"empty installed marker is invalid");
            fixture.Write("status.installed","installed");
            using(var metadata=new FileStream(fixture.PathOf("status.committed"),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None))
                Check(fixture.Gate.CanResume(),"locked commit metadata never blocks a healthy installed child after helper exit");
            File.Delete(fixture.PathOf("status.committed"));Check(fixture.Gate.CanResume(),"committed marker is intentionally not required");
            var ack=NewFixture();
            Check(ack.Gate.Acknowledge()&&File.ReadAllText(ack.PathOf("healthy"))==ack.Plan.HealthToken,"acknowledgment writes only the own bound health token");
            Check(ack.Gate.Acknowledge(),"repeated correct acknowledgment is idempotent");
            ack.Write("healthy",new string('b',32));Check(!ack.Gate.Acknowledge()&&File.ReadAllText(ack.PathOf("healthy"))==new string('b',32),"wrong existing health marker is not overwritten");
        }
        static void ValidateBinding()
        {
            var fixture=NewFixture();Ready(fixture);Check(fixture.Gate.CanResume(),"valid fixture starts ready");
            File.WriteAllBytes(fixture.PlanPath,Encoding.UTF8.GetBytes("{}"));Check(!fixture.Gate.CanResume(),"changed plan bytes fail closed without throwing on the UI timer");
            File.WriteAllBytes(fixture.PlanPath,fixture.PlanBytes);Check(fixture.Gate.CanResume(),"restoring exact bound plan restores readiness");
            File.WriteAllText(Path.Combine(fixture.Target,"LocalLink.exe"),"CHANGED OWN FIXTURE");Check(!fixture.Gate.CanResume(),"changed target image cannot resume automatic startup");
            File.WriteAllBytes(Path.Combine(fixture.Target,"LocalLink.exe"),fixture.Executable);
            fixture.Write("healthy",fixture.Plan.HealthToken);fixture.Plan.HealthToken=Guid.NewGuid().ToString("N");
            Check(fixture.Gate.CanResume(),"mutating the caller's plan object does not change the gate's captured token");
            File.Delete(fixture.PlanPath);Check(!fixture.Gate.CanResume(),"missing plan fails closed");
        }
        static void ValidateReparse()
        {
            var fixture=NewFixture();Ready(fixture);File.Delete(fixture.PathOf("healthy"));
            string other=Path.Combine(Path.GetDirectoryName(fixture.Target),"owned-health.txt");File.WriteAllText(other,fixture.Plan.HealthToken);
            string link=fixture.PathOf("healthy");
            if(CreateSymbolicLink(link,other,2)||CreateSymbolicLink(link,other,0))
                Check(!fixture.Gate.CanResume(),"health-marker symlink cannot satisfy the startup gate");
            else Console.WriteLine("SKIP: fixture symlink unavailable; no privileges changed.");
        }
        static void Check(bool passed,string name){if(!passed)throw new InvalidOperationException("FAIL: "+name);checks++;}
    }
}
