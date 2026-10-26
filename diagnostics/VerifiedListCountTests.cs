using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace LocalLink
{
    // Isolated package-local snapshots. An AppDomain with its own ApplicationBase
    // routes BundledFolder to our unique fixture root, never the shipped lists,
    // current AppData snapshot, user's settings or account data. Engine files are
    // optionally copied and verified as DATA; no process/driver/filter is started.
    internal static class VerifiedListCountTests
    {
        static int assertions;
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        static int Main(string[] args)
        {
            try
            {
                if(args.Length==2&&args[0]=="--isolated") return Run(args[1]);
                string source=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"engine");
                if(args.Length==2&&args[0]=="--engine-fixture-source") source=Path.GetFullPath(args[1]);
                else if(args.Length!=0) return 2;
                string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"offline-fixtures","vc-"+Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                var setup=new AppDomainSetup{ApplicationBase=root,ConfigurationFile=Assembly.GetExecutingAssembly().Location+".config"};
                var isolated=AppDomain.CreateDomain("LocalLink verified-list fixture",null,setup);
                try{return isolated.ExecuteAssembly(Assembly.GetExecutingAssembly().Location,new[]{"--isolated",source});}
                finally{AppDomain.Unload(isolated);}
            }
            catch(Exception error){Console.Error.WriteLine(error);return 1;}
        }
        static int Run(string engineSource)
        {
            string root=AppDomain.CurrentDomain.BaseDirectory,folder=Path.Combine(root,"lists");
            Check(folder.StartsWith(root,StringComparison.OrdinalIgnoreCase),"fixture list root belongs to isolated application base");
            var first=PublicLists.SaveSnapshot(folder,new[]{"alpha.org","bravo.org","charlie.org"},new string[0],0,CancellationToken.None);
            string path=Path.Combine(folder,first.FileName);
            int count=987;
            using(var verified=PublicLists.OpenVerified(path,CancellationToken.None,out count))
            {
                Check(count==3,"fresh full validation returns actual count");
                Check(verified.Position==0,"fresh verified stream rewound");
                Check(verified.ReadByte()==(byte)'a',"returned stream points to snapshot data");
                Check(!CanWrite(path),"fresh verification retains no-write guard");
                Check(!CanExclusivelyRead(path),"fresh read guard cannot be consumed exclusively");
                using(var reader=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
                    Check(reader.Length==verified.Length,"read sharing required by engine remains allowed");
            }
            Check(CanExclusivelyRead(path),"fresh guard released by disposal");
            count=987;
            using(var verified=PublicLists.OpenVerified(path,CancellationToken.None,out count))
            {
                Check(count==3,"exact SHA/count cache returns validated count");
                Check(verified.Position==0,"cached stream rewound after mandatory new hash");
                Check(!CanWrite(path),"cached verification retains no-write guard");
            }

            string passport=path+".json";
            byte[] originalPassport=File.ReadAllBytes(passport),originalContent=File.ReadAllBytes(path);
            first.DomainCount=4;WritePassport(passport,first);count=987;
            Expect<IOException>(delegate{using(var ignored=PublicLists.OpenVerified(path,CancellationToken.None,out count)){}},"changed passport count cannot reuse SHA-only cache");
            Check(count==0,"wrong passport count never published");
            Check(CanExclusivelyRead(path),"count failure closes guard");
            WriteBytes(passport,originalPassport);first.DomainCount=3;

            WriteBytes(path,Utf8.GetBytes("alpha.org\nbravo.org\ndelta.org\n"));count=987;
            Expect<IOException>(delegate{using(var ignored=PublicLists.OpenVerified(path,CancellationToken.None,out count)){}},"cached content still requires a fresh SHA check");
            Check(count==0,"tampered SHA result count remains zero");
            Check(CanExclusivelyRead(path),"hash failure releases guard");
            WriteBytes(path,originalContent);

            WriteBytes(path,Utf8.GetBytes("alpha.org\nbravo.org\nHTTPS://evil.org/\n"));
            using(var input=File.OpenRead(path))first.Sha256=PublicLists.HashStream(input,CancellationToken.None);
            WritePassport(passport,first);count=987;
            Expect<IOException>(delegate{using(var ignored=PublicLists.OpenVerified(path,CancellationToken.None,out count)){}},"new matching SHA never skips domain content validation");
            Check(count==0,"invalid normalized content does not publish passport count");
            Check(CanExclusivelyRead(path),"content validation failure releases guard");
            WriteBytes(path,originalContent);WriteBytes(passport,originalPassport);
            using(var verified=PublicLists.OpenVerified(path,CancellationToken.None,out count))
                Check(count==3&&verified.Position==0,"restored known snapshot still validates");

            var second=PublicLists.SaveSnapshot(folder,new[]{"alpha.org","delta.org","echo.org","foxtrot.org"},new string[0],0,CancellationToken.None);
            string newer=Path.Combine(folder,second.FileName);
            using(var verified=PublicLists.OpenVerified(newer,CancellationToken.None,out count))
                Check(count==4&&verified.Position==0,"new snapshot has its own validated count");
            using(var verified=PublicLists.OpenVerified(path,CancellationToken.None,out count))
                Check(count==3,"previous snapshot count is not replaced by latest passport");

            count=987;
            Expect<OperationCanceledException>(delegate{using(var ignored=PublicLists.OpenVerified("not-a-file",new CancellationToken(true),out count)){}},"pre-cancellation precedes path/file access");
            Check(count==0,"pre-cancelled result count initialized to zero");
            count=987;
            Expect<OperationCanceledException>(delegate{using(var ignored=PublicLists.OpenVerified(path,new CancellationToken(true),out count)){}},"cached snapshot cannot bypass cancellation");
            Check(count==0&&CanExclusivelyRead(path),"cancelled cache hit publishes zero and keeps no handle");
            using(var legacy=PublicLists.OpenVerified(path))
                Check(legacy.Position==0&&!CanWrite(path),"legacy no-token overload preserves stream contract");
            using(var legacy=PublicLists.OpenVerified(path,CancellationToken.None))
                Check(legacy.Position==0&&!CanWrite(path),"legacy token overload preserves stream contract");

            string outside=Path.Combine(root,"outside","sites-foreign.lst");count=987;
            Expect<IOException>(delegate{using(var ignored=PublicLists.OpenVerified(outside,CancellationToken.None,out count)){}},"count overload retains owned-folder restriction");
            Check(count==0,"foreign-folder rejection cannot publish count");
            TestPrepared(root,engineSource,newer,4);
            Console.WriteLine("Verified list count: "+assertions+" offline assertions passed. Isolated snapshots retained; no network/filter/driver/settings.");
            return 0;
        }
        static void TestPrepared(string root,string engineSource,string snapshot,int expected)
        {
            if(!Directory.Exists(engineSource))
            {
                Console.WriteLine("SKIP prepared-start fixture: packaged engine directory unavailable; count/hash/guard suite still ran.");
                return;
            }
            string destination=Path.Combine(root,"engine");
            foreach(string file in Directory.GetFiles(engineSource,"*",SearchOption.AllDirectories))
            {
                string relative=file.Substring(Path.GetFullPath(engineSource).TrimEnd(Path.DirectorySeparatorChar).Length+1);
                string target=Path.Combine(destination,relative);Directory.CreateDirectory(Path.GetDirectoryName(target));File.Copy(file,target,false);
            }
            using(var engine=new Engine(root))
            {
                var spec=new ProfileSpec{Domains="example.org",Strategy="disorder",HostListFile=snapshot};
                using(var prepared=engine.PrepareStartAsync(spec,CancellationToken.None).GetAwaiter().GetResult())
                {
                    Check(prepared.Summary.ExternalListDomainCount==expected,"prepared evidence uses count from same verified snapshot");
                    Check(prepared.Summary.UsesExternalList&&prepared.Summary.EngineFilesVerifiedAtPreflight,"prepared evidence records verified list and engine");
                    Check(!engine.IsRunning&&engine.LastProfileEvidence==null,"preflight does not imply or start a launched filter");
                    Check(!CanWrite(snapshot),"prepared start retains the verified list handle");
                    Check(!CanExclusivelyRead(Path.Combine(destination,"winws2.exe")),"prepared start retains component guards");
                }
                Check(CanExclusivelyRead(snapshot),"unused preparation disposal releases list guard");
                Check(CanExclusivelyRead(Path.Combine(destination,"winws2.exe")),"unused preparation disposal releases component guards");
            }
        }
        static void WritePassport(string path,ListSnapshot value)
        {WriteBytes(path,Utf8.GetBytes(new JavaScriptSerializer().Serialize(value)));}
        static void WriteBytes(string path,byte[] bytes)
        {using(var stream=new FileStream(path,FileMode.Create,FileAccess.Write,FileShare.None))stream.Write(bytes,0,bytes.Length);}
        static bool CanWrite(string path)
        {try{using(var ignored=new FileStream(path,FileMode.Open,FileAccess.Write,FileShare.ReadWrite))return true;}catch(IOException){return false;}}
        static bool CanExclusivelyRead(string path)
        {try{using(var ignored=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None))return true;}catch(IOException){return false;}}
        static void Expect<T>(Action action,string name)where T:Exception
        {bool rejected=false;try{action();}catch(T){rejected=true;}Check(rejected,name);}
        static void Check(bool condition,string name){assertions++;if(!condition)throw new Exception("FAIL "+name);}
    }
}
