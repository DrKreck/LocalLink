using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace LocalLink
{
    // Offline regression only. All filenames below are invented strings: no
    // user settings, hostlists, hardware IDs or activation stores are read.
    // The only child process is this test executable in argv-receiver mode;
    // Engine.Start/PrepareStartAsync and winws2 are deliberately never called.
    static class HostListPathTests
    {
        static int assertions;
        static int Main(string[] args)
        {
            if(args.Length>0&&args[0]=="--benign-argv-receiver")return Receive(args);
            if(args.Length==1&&args[0]=="--engine-syntax-only")return EngineSyntaxOnly();
            if(args.Length!=0)return 2;
            try
            {
                Check(Profiles.ValidateHostListFile(null)==String.Empty,"null means no hostlist");
                Check(Profiles.ValidateHostListFile("")==String.Empty,"empty means no hostlist");
                string[] valid={
                    @"C:\LocalLink\lists\sites-20261004.lst",
                    @"C:\Users\Player (1)\AppData\Local\LocalLink\lists\sites-test.lst",
                    @"C:\Users\A&B\AppData\Local\LocalLink\lists\sites-test.lst",
                    @"C:\Users\Player+Test\AppData\Local\LocalLink\lists\sites-test.lst",
                    @"C:\Users\[Player]\AppData\Local\LocalLink\lists\sites-test.lst",
                    @"C:\Users\O'Brien\AppData\Local\LocalLink\lists\sites-test.lst",
                    @"C:\Users\Player@Test\AppData\Local\LocalLink\lists\sites-test.lst",
                    @"C:\Users\100% Player\AppData\Local\LocalLink\lists\sites-test.lst",
                    @"C:\Users\Dollar$\AppData\Local\LocalLink\lists\sites-test.lst",
                    @"C:\Users\Comma,Semicolon;Equals=\AppData\Local\LocalLink\lists\sites-test.lst",
                    @"C:\Users\Игрок\AppData\Local\LocalLink\lists\sites-test.lst",
                    "C:\\Users\\Cafe\u0301\\AppData\\Local\\LocalLink\\lists\\sites-test.lst",
                    "C:\\Users\\Player\uD83C\uDFAE\\AppData\\Local\\LocalLink\\lists\\sites-test.lst",
                    @"C:\Users\--new\AppData\Local\LocalLink\lists\sites-test.lst",
                    @"C:\Users\COM10\AppData\Local\LocalLink\lists\sites-test.lst",
                    @"c:\locallink\lists\sites-test.lst",
                    "C:\\"+new string('a',231)+"\\x.lst"
                };
                for(int i=0;i<valid.Length;i++)
                {
                    string path=valid[i];
                    Check(Profiles.ValidateHostListFile(path)==path,"valid path round-trip case "+i);
                    foreach(string method in Profiles.TlsStrategies())
                    {
                        var generated=Profiles.Build(new ProfileSpec{Domains="example.org",HostListFile=path,
                            Strategy=method,Quic=true,DiscordVoice=true,TelegramNative=true});
                        int count=0;foreach(string value in generated)if(value=="--hostlist="+path)count++;
                        Check(count==3,"exact one hostlist per TLS/HTTP/QUIC profile case "+i+" "+method);
                    }
                }
                string[] invalid={
                    "sites-test.lst",@"C:sites-test.lst",@"\sites-test.lst",@"\\server\share\sites-test.lst",
                    @"\\?\C:\LocalLink\lists\sites-test.lst",@"\\.\C:\LocalLink\lists\sites-test.lst",
                    @"C:/LocalLink/lists/sites-test.lst",@"C:\\LocalLink\lists\sites-test.lst",
                    @"C:\LocalLink\..\sites-test.lst",@"C:\LocalLink\.\sites-test.lst",
                    @"C:\LocalLink\lists\",@"C:\LocalLink\lists\sites-test.lst.",
                    @"C:\LocalLink\lists\sites-test.lst ",@"C:\LocalLink.\lists\sites-test.lst",
                    @"C:\LocalLink \lists\sites-test.lst",@"C:\LocalLink\lists\sites-test.lst:stream",
                    @"C:\LocalLink\lists\sites*.lst",@"C:\LocalLink\lists\sites?.lst",
                    "C:\\LocalLink\\lists\\sites\".lst",@"C:\LocalLink\lists\sites|.lst",
                    @"C:\LocalLink\lists\sites<.lst",@"C:\LocalLink\lists\sites>.lst",
                    "C:\\LocalLink\\lists\\sites\n.lst","C:\\LocalLink\\lists\\sites\r.lst",
                    "C:\\LocalLink\\lists\\sites\t.lst","C:\\LocalLink\\lists\\sites\0.lst",
                    "C:\\LocalLink\\lists\\sites\u007f.lst","C:\\LocalLink\\lists\\sites\u202e.lst",
                    "C:\\LocalLink\\lists\\sites\u200d.lst","C:\\LocalLink\\lists\\sites\ufeff.lst",
                    "C:\\LocalLink\\lists\\sites\ud800.lst","C:\\LocalLink\\lists\\sites\udc00.lst",
                    "C:\\LocalLink\\lists\\sites\ud800\ud800.lst","C:\\LocalLink\\lists\\sites\udc00\ud800.lst",
                    @"C:\CON\sites-test.lst",@"C:\LocalLink\lists\con.txt",@"C:\PRN.txt\sites-test.lst",
                    @"C:\aux\sites-test.lst",@"C:\LocalLink\lists\NUL.lst",@"C:\COM1\sites-test.lst",
                    @"C:\LocalLink\lists\com9.lst",@"C:\LPT1.txt\sites-test.lst",@"C:\LocalLink\lists\lpt9.lst",
                    @"C:\CONIN$\sites-test.lst",@"C:\CONOUT$.txt\sites-test.lst",
                    "C:\\COM\u00b9\\sites-test.lst","C:\\LPT\u00b2.lst",
                    "C:\\LocalLink\\lists\\sites\udb40\udc01.lst",
                    "C:\\"+new string('a',232)+"\\x.lst"
                };
                for(int i=0;i<invalid.Length;i++)
                {
                    string path=invalid[i];bool rejected=false;
                    try{Profiles.ValidateHostListFile(path);}catch(ArgumentException){rejected=true;}
                    Check(rejected,"invalid path rejected case "+i);
                }
                // Exercise the actual production CreateProcessW + Quote + Job
                // path, but substitute only our harmless own argv receiver.
                using(var engine=new Engine(AppDomain.CurrentDomain.BaseDirectory))
                {
                    for(int i=0;i<valid.Length;i++)
                    {
                        var values=Profiles.Build(new ProfileSpec{Domains="example.org",HostListFile=valid[i],
                            Strategy="fake-disorder",Quic=true,DiscordVoice=true,TelegramNative=true});
                        CheckBenignReceiver(engine,values,"production argv preserved case "+i);
                    }
                    CheckBenignReceiver(engine,new List<string>{"", "quotes\"inside", "tail\\", "two\\\\\"quotes", "() & + [] '", "\uD83C\uDFAE", "e\u0301"},
                        "generic quoting preserves whitespace quotes slashes and Unicode");
                    engine.Stop();Check(!engine.IsRunning,"benign receiver cleanup complete");
                }
                Console.WriteLine("PASSED "+assertions+" offline hostlist/argv assertions. No hostlist reads, filter, driver, network requests or deletion.");
                return 0;
            }
            catch(Exception error){Console.Error.WriteLine(error);return 1;}
        }
        static void CheckBenignReceiver(Engine engine,List<string> values,string label)
        {
            var parameters=new List<string>{"--benign-argv-receiver",values.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),Digest(values)};
            parameters.AddRange(values);
            var launch=typeof(Engine).GetMethod("LaunchOwnedProcess",BindingFlags.NonPublic|BindingFlags.Instance);
            launch.Invoke(engine,new object[]{Assembly.GetExecutingAssembly().Location,parameters,AppDomain.CurrentDomain.BaseDirectory});
            var child=(Process)typeof(Engine).GetField("child",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(engine);
            Check(child!=null&&child.WaitForExit(10000),"benign receiver exited for "+label);
            Check(child.ExitCode==0,label);
            engine.Stop();
        }
        static int Receive(string[] args)
        {
            int count;if(args.Length<3||!Int32.TryParse(args[1],out count)||count!=args.Length-3)return 7;
            var actual=new List<string>();for(int i=3;i<args.Length;i++)actual.Add(args[i]);
            return String.Equals(Digest(actual),args[2],StringComparison.Ordinal)?0:8;
        }
        static int EngineSyntaxOnly()
        {
            // The bundled EXE declares requireAdministrator. Never request
            // elevation; an ordinary run creates no fixture and no process.
            if(!Engine.IsAdmin())
            {
                Console.WriteLine("SKIPPED engine syntax-only: pinned EXE requires administrator. No elevation, fixture or engine launch attempted.");
                return 0;
            }
            try
            {
                string root=AppDomain.CurrentDomain.BaseDirectory;
                using(var verifier=new Engine(root))verifier.VerifyFiles();
                string folder=Path.Combine(root,"argv-data-"+Guid.NewGuid().ToString("N").Substring(0,8));
                string[] names={"Player (1)","A&B","Player+Test","[Player]","O'Brien","Player@Test",
                    "100% Player","Dollar$","Comma,Semicolon;Equals=","Игрок","Cafe\u0301","Player\ud83c\udfae"};
                int cases=0;
                foreach(string name in names)
                {
                    string directory=Path.Combine(folder,name),path=Path.Combine(directory,"sites-fixture.lst");
                    Profiles.ValidateHostListFile(path);PublicLists.SafePath(directory);
                    Directory.CreateDirectory(directory);PublicLists.SafePath(directory);
                    using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                    {byte[] bytes=Encoding.UTF8.GetBytes("example.org\n");output.Write(bytes,0,bytes.Length);}
                    PublicLists.SafePath(path);
                    foreach(string method in Profiles.TlsStrategies())
                    {
                        // --dry-run is FIRST and cannot be supplied by fixture.
                        // --wf-dup-check=0 avoids a global mutex clash with an
                        // already running app. No autohostlist/debug-file/Lua
                        // writable directory flags are present in this spec.
                        var command=new StringBuilder("--dry-run --wf-dup-check=0");
                        foreach(string value in Profiles.Build(new ProfileSpec{Domains="example.org",Strategy=method,HostListFile=path}))
                            command.Append(' ').Append(Engine.Quote(value));
                        var start=new ProcessStartInfo(Path.Combine(root,"engine","winws2.exe"),command.ToString());
                        start.WorkingDirectory=Path.Combine(root,"engine");start.UseShellExecute=false;start.CreateNoWindow=true;
                        start.RedirectStandardOutput=true;start.RedirectStandardError=true;
                        using(var child=Process.Start(start))
                        {
                            var stdout=child.StandardOutput.ReadToEndAsync();var stderr=child.StandardError.ReadToEndAsync();
                            if(!child.WaitForExit(15000))
                            {child.Kill();child.WaitForExit(3000);throw new Exception("Syntax-only child exceeded timeout; only the spawned child was stopped.");}
                            if(!stdout.Wait(3000)||!stderr.Wait(3000))throw new Exception("Syntax-only output did not finish.");
                            string combined=stdout.Result+stderr.Result;
                            if(child.ExitCode!=0||combined.IndexOf("command line parameters verified",StringComparison.Ordinal)<0)
                                throw new Exception("Syntax-only case "+cases+" "+method+" failed, exit "+child.ExitCode+". No filter launch requested.");
                        }
                        Console.WriteLine("PASS pinned engine --dry-run fixture "+cases+" "+method);cases++;
                    }
                }
                Console.WriteLine("PASSED "+cases+" explicit syntax-only engine cases. Synthetic fixtures retained; no filter/driver load requested, no files deleted.");
                return 0;
            }
            catch(Exception error)
            {Console.Error.WriteLine("Syntax-only audit failed: "+error.GetType().Name+": "+error.Message);return 1;}
        }
        static string Digest(List<string> values)
        {
            using(var data=new MemoryStream())
            {
                using(var writer=new BinaryWriter(data,new UTF8Encoding(false,true),true))
                {writer.Write(values.Count);foreach(string value in values)writer.Write(value);writer.Flush();}
                using(var sha=SHA256.Create())return Convert.ToBase64String(sha.ComputeHash(data.ToArray()));
            }
        }
        static void Check(bool pass,string label)
        {if(!pass)throw new Exception("FAIL "+label);assertions++;Console.WriteLine("PASS "+label);}
    }
}
