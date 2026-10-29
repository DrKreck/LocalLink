using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using LocalLink;

namespace LocalLink
{
    internal static class DesktopProcessTests
    {
        static int checks;
        [STAThread]static int Main(string[] args)
        {
            try
            {
                if(args.Length==2&&args[0]=="--launch-fixture")return Launch(args[1]);
                PureIdentity();Arguments();LimitedQueries();
                Console.WriteLine("Desktop process: "+checks+" assertions passed. No launches, desktop changes, network or privileges modified.");return 0;
            }
            catch(Exception error){Console.Error.WriteLine(error.GetType().Name+": "+error.Message);return 1;}
        }
        static void PureIdentity()
        {
            const string expected=@"C:\Windows\explorer.exe",sid="S-1-5-21-123-456-789-1001";
            var identity=new DesktopIdentity{ImagePath=expected,UserSid=sid,ProcessId=123,SessionId=2,Elevated=false};
            Check(DesktopProcess.IsTrustedDesktop(identity,expected,sid,2),"matching ordinary desktop accepted");
            Check(!DesktopProcess.IsTrustedDesktop(null,expected,sid,2),"missing desktop rejected");
            identity.Elevated=true;Check(!DesktopProcess.IsTrustedDesktop(identity,expected,sid,2),"elevated Explorer rejected");identity.Elevated=false;
            Check(!DesktopProcess.IsTrustedDesktop(identity,expected,"S-1-5-21-other",2),"different-user desktop rejected");
            Check(!DesktopProcess.IsTrustedDesktop(identity,expected,sid,3),"different session rejected");
            identity.ImagePath=@"C:\Downloads\explorer.exe";Check(!DesktopProcess.IsTrustedDesktop(identity,expected,sid,2),"same-name fake Explorer rejected");
            identity.ImagePath="C:\\Windows\\explorer.exe\0";Check(!DesktopProcess.IsTrustedDesktop(identity,expected,sid,2),"malformed Explorer path rejected");
            identity.ImagePath=expected;identity.ProcessId=0;Check(!DesktopProcess.IsTrustedDesktop(identity,expected,sid,2),"invalid desktop PID rejected");
        }
        static void Arguments()
        {
            string fixture=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"desktop-fixture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);
            string helper=Path.Combine(fixture,"LocalLink.UpdateHelper.exe"),plan=Path.Combine(fixture,"plan.json");
            File.WriteAllBytes(helper,new byte[]{77,90});File.WriteAllText(plan,"{}",new UTF8Encoding(false));string hash=Hash(File.ReadAllBytes(plan));
            DesktopProcess.ValidateHelperArguments(helper,plan,hash);Check(true,"fixed adjacent helper and hash-bound plan accepted");
            foreach(string bad in new[]{"relative.exe",@"\\host\share\LocalLink.UpdateHelper.exe",helper+":stream",helper+"\n",helper.Replace("LocalLink.UpdateHelper.exe","other.exe")})
                Expect<IOException>(()=>DesktopProcess.ValidateHelperArguments(bad,plan,hash),"arbitrary/ambiguous helper rejected");
            foreach(string bad in new[]{"",new string('z',64),new string('a',63),new string('A',64)})
                Expect<IOException>(()=>DesktopProcess.ValidateHelperArguments(helper,plan,bad),"noncanonical plan hash rejected");
            Expect<IOException>(()=>DesktopProcess.ValidateHelperArguments(helper,plan,new string('a',64)),"changed plan rejected");
            string different=Path.Combine(fixture,"different");Directory.CreateDirectory(different);string otherPlan=Path.Combine(different,"plan.json");File.WriteAllText(otherPlan,"{}");
            Expect<IOException>(()=>DesktopProcess.ValidateHelperArguments(helper,otherPlan,Hash(File.ReadAllBytes(otherPlan))),"plan in another folder rejected");
            string notPlan=Path.Combine(fixture,"other.json");File.WriteAllText(notPlan,"{}");
            Expect<IOException>(()=>DesktopProcess.ValidateHelperArguments(helper,notPlan,Hash(File.ReadAllBytes(notPlan))),"arbitrary plan filename rejected");
            File.WriteAllBytes(plan,new byte[32769]);Expect<IOException>(()=>DesktopProcess.ValidateHelperArguments(helper,plan,Hash(File.ReadAllBytes(plan))),"oversized plan rejected before desktop interaction");
            File.WriteAllBytes(helper,new byte[8*1024*1024+1]);File.WriteAllText(plan,"{}",new UTF8Encoding(false));
            Expect<IOException>(()=>DesktopProcess.ValidateHelperArguments(helper,plan,Hash(File.ReadAllBytes(plan))),"oversized helper rejected before launch");
        }
        static void LimitedQueries()
        {
            using(var process=Process.GetCurrentProcess())
            {
                Check(String.Equals(DesktopProcess.QueryImagePath(process.Id),Assembly.GetExecutingAssembly().Location,StringComparison.OrdinalIgnoreCase),"limited-query image path matches own test process");
                Check(DesktopProcess.QueryStartTimeUtcTicks(process.Id)==process.StartTime.ToUniversalTime().Ticks,"limited-query start time matches own test process");
                using(var observed=DesktopProcess.Observe(process.Id))
                {
                    Check(!observed.HasExited,"limited-query native observer sees current process alive");
                    Check(observed.StartedUtcTicks==process.StartTime.ToUniversalTime().Ticks,"held observer preserves start-time identity");
                    Check(String.Equals(observed.ImagePath,Assembly.GetExecutingAssembly().Location,StringComparison.OrdinalIgnoreCase),"held observer matches executable without MainModule");
                }
            }
            Expect<ArgumentOutOfRangeException>(()=>DesktopProcess.QueryImagePath(0),"invalid PID rejected");
        }
        static int Launch(string fixture)
        {
            string helper=Path.Combine(Path.GetFullPath(fixture),"LocalLink.UpdateHelper.exe"),plan=Path.Combine(Path.GetFullPath(fixture),"plan.json");
            var identity=DesktopProcess.InspectDesktop();DesktopProcess.RequireTrustedDesktop(identity);
            DesktopProcess.LaunchVerifiedHelper(helper,plan,Hash(File.ReadAllBytes(plan)));
            var clock=Stopwatch.StartNew();string marker=Path.Combine(fixture,"fixture-ready.txt");
            while(!File.Exists(marker)){if(clock.Elapsed.TotalSeconds>15)throw new TimeoutException("Desktop launch did not acknowledge fixture.");System.Threading.Thread.Sleep(100);}
            Check(File.ReadAllText(marker)=="standard-user","Explorer handoff launched fixed helper without administrator rights");
            Console.WriteLine("Desktop broker fixture: "+checks+" assertion passed. Existing same-user Explorer only; no UAC prompt or installed app changes.");return 0;
        }
        internal static string Hash(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
        static void Check(bool value,string description){checks++;if(!value)throw new Exception(description);}
        static void Expect<T>(Action action,string description)where T:Exception
        {bool thrown=false;try{action();}catch(T){thrown=true;}Check(thrown,description);}
    }
}
