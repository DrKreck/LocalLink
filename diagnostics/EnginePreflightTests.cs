using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace LocalLink
{
    // Offline regression harness: the ONLY launched process is this executable
    // in harmless sleep/exit modes. No filter, driver, network or UAC requests.
    static class EnginePreflightTests
    {
        static int assertions;
        static int Main(string[] args)
        {
            if(args.Length==1&&args[0]=="--benign-wait"){Thread.Sleep(60000);return 0;}
            if(args.Length==1&&args[0]=="--benign-exit-seven"){Thread.Sleep(150);return 7;}
            try
            {
                string root=AppDomain.CurrentDomain.BaseDirectory;
                var cancelled=new CancellationToken(true);
                using(var e=new Engine(root))
                {
                    Expect<OperationCanceledException>(()=>e.PrepareStartAsync(Spec(),cancelled),"pre-cancelled prepare never starts work");
                    Expect<OperationCanceledException>(()=>e.VerifyFiles(cancelled),"pre-cancelled component verification");
                    Check(!e.IsRunning&&e.TakeUnexpectedExit()==null,"cancelled verification has no child or exit event");
                    e.Stop();e.Stop();Check(e.TakeUnexpectedExit()==null,"repeated intentional Stop has no exit event");
                    using(var prepared=e.PrepareStartAsync(Spec(),CancellationToken.None).GetAwaiter().GetResult())
                    {
                        Check(!e.IsRunning,"completed preflight never launches a filter");
                        string component=Path.Combine(root,"engine","winws2.exe");
                        Check(!CanExclusivelyRead(component),"preflight retains engine component read guard");
                        using(var other=new Engine(root))
                            ExpectMessage(()=>other.StartPrepared(prepared),"другому","prepared result is bound to its owner before admin check");
                        Check(!CanExclusivelyRead(component),"wrong owner cannot consume or release original guard");
                        e.Stop();
                        ExpectMessage(()=>e.StartPrepared(prepared),"устарела","Stop invalidates preparation before admin or process launch");
                        Check(CanExclusivelyRead(component),"stale preparation releases all component guards");
                        ExpectMessage(()=>e.StartPrepared(prepared),"использована","preparation can be attempted only once");
                    }
                    using(var source=new CancellationTokenSource())
                    using(var prepared=e.PrepareStartAsync(Spec(),source.Token).GetAwaiter().GetResult())
                    {
                        source.Cancel();Expect<OperationCanceledException>(()=>e.StartPrepared(prepared),"cancel after preflight rejects launch before admin check");
                        Check(CanExclusivelyRead(Path.Combine(root,"engine","winws2.exe")),"late cancellation releases component guards");
                    }
                    var closed=e.PrepareStartAsync(Spec(),CancellationToken.None).GetAwaiter().GetResult();closed.Dispose();
                    ExpectMessage(()=>e.StartPrepared(closed),"закрыта","disposed preparation cannot launch");
                    Check(CanExclusivelyRead(Path.Combine(root,"engine","winws2.exe")),"disposing unused preflight releases component guards");

                    TestBundledListGuard(root,e);

                    LaunchBenign(e,"--benign-exit-seven");
                    var deadline=DateTime.UtcNow.AddSeconds(5);
                    while(e.IsRunning&&DateTime.UtcNow<deadline)Thread.Sleep(20);
                    Check(!e.IsRunning,"benign child natural exit detected");
                    var exit=e.TakeUnexpectedExit();
                    Check(exit!=null&&exit.ExitCode==7&&exit.ObservedUtc.Kind==DateTimeKind.Utc,"unexpected exit exposes only nullable code and UTC observation");
                    Check(e.TakeUnexpectedExit()==null,"unexpected exit metadata is delivered once");
                    e.Stop();Check(e.TakeUnexpectedExit()==null,"cleanup cannot manufacture a second exit event");

                    LaunchBenign(e,"--benign-wait");Check(e.IsRunning,"job owns only benign test child");
                    var child=(Process)typeof(Engine).GetField("child",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(e);
                    int pid=child.Id;e.Stop();
                    Check(IsExited(pid),"Stop terminates own benign job child");
                    Check(!e.IsRunning&&e.TakeUnexpectedExit()==null,"intentional stop never reports unexpected exit");
                }
                var disposed=new Engine(root);disposed.Dispose();
                Expect<ObjectDisposedException>(()=>disposed.PrepareStartAsync(Spec(),CancellationToken.None),"disposed engine rejects new preparation");
                Expect<OperationCanceledException>(()=>PublicLists.GetActivePath(cancelled),"active passport read is pre-cancellable");
                Expect<OperationCanceledException>(()=>PublicLists.OpenVerified("not-read",cancelled),"list verification cancels before file access");
                using(var input=new MemoryStream(Encoding.UTF8.GetBytes("abc")))
                    Check(PublicLists.HashStream(input,CancellationToken.None)=="ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad","incremental SHA256 preserves known digest");
                using(var source=new CancellationTokenSource())
                using(var input=new CancellingStream(source))
                {
                    Expect<OperationCanceledException>(()=>PublicLists.HashStream(input,source.Token),"hashing observes mid-stream cancellation");
                    Check(input.ReadCount==1,"cancelled hashing does not read the remaining stream");
                }
                Console.WriteLine("PASSED "+assertions+" offline preflight/exit assertions. No filters, driver starts, HTTP requests or deletion.");return 0;
            }
            catch(Exception e){Console.Error.WriteLine(e);return 1;}
        }
        static ProfileSpec Spec(){return new ProfileSpec{Mode=HelperMode.Direct,Domains="example.org",Strategy="split"};}
        static void TestBundledListGuard(string root,Engine e)
        {
            string pointer=Path.Combine(root,"lists","current.json");
            if(!File.Exists(pointer))throw new IOException("Test package must contain its bundled list fixture.");
            var info=new JavaScriptSerializer().Deserialize<ListSnapshot>(File.ReadAllText(pointer,Encoding.UTF8));
            string path=Path.Combine(root,"lists",info.FileName);var spec=Spec();spec.HostListFile=path;
            using(var prepared=e.PrepareStartAsync(spec,CancellationToken.None).GetAwaiter().GetResult())
            {
                Check(!CanExclusivelyRead(path),"worker verifies and retains bundled hostlist guard");
                Check(!e.IsRunning,"verified public list never starts a filter");
            }
            Check(CanExclusivelyRead(path),"disposing preflight releases list guard without changing snapshot");
            using(var source=new CancellationTokenSource())
            using(var prepared=e.PrepareStartAsync(spec,source.Token).GetAwaiter().GetResult())
            {
                source.Cancel();Expect<OperationCanceledException>(()=>e.StartPrepared(prepared),"cancelled list preparation cannot launch");
            }
            Check(CanExclusivelyRead(path),"cancelled commit releases list guard");
        }
        static void LaunchBenign(Engine e,string mode)
        {
            var launch=typeof(Engine).GetMethod("LaunchOwnedProcess",BindingFlags.NonPublic|BindingFlags.Instance);
            launch.Invoke(e,new object[]{Assembly.GetExecutingAssembly().Location,new List<string>{mode},AppDomain.CurrentDomain.BaseDirectory});
        }
        static bool CanExclusivelyRead(string path)
        {try{using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None))return true;}catch(IOException){return false;}}
        static bool IsExited(int pid)
        {try{using(var p=Process.GetProcessById(pid))return p.HasExited||p.WaitForExit(3000);}catch(ArgumentException){return true;}}
        static void Expect<T>(Action action,string name) where T:Exception
        {bool rejected=false;try{action();}catch(T){rejected=true;}Check(rejected,name);}
        static void ExpectMessage(Action action,string message,string name)
        {bool rejected=false;try{action();}catch(InvalidOperationException e){rejected=e.Message.IndexOf(message,StringComparison.Ordinal)>=0;}Check(rejected,name);}
        static void Check(bool pass,string name){if(!pass)throw new Exception("FAIL "+name);assertions++;Console.WriteLine("PASS "+name);}
        sealed class CancellingStream:MemoryStream
        {
            readonly CancellationTokenSource source;
            public int ReadCount;
            public CancellingStream(CancellationTokenSource cancellation):base(new byte[200000]){source=cancellation;}
            public override int Read(byte[] buffer,int offset,int count)
            {ReadCount++;int n=base.Read(buffer,offset,count);source.Cancel();return n;}
        }
    }
}
