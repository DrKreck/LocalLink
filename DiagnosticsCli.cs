using System;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;

namespace LocalLink
{
    static class DiagnosticsCli
    {
        static int Main(string[] args)
        {
            if(args.Length==1&&args[0]=="--verify-lists")
            {
                try
                {
                    var clock=System.Diagnostics.Stopwatch.StartNew();
                    string path=PublicLists.GetVerifiedPath();
                    using(var guard=PublicLists.OpenVerified(path))
                    {
                        bool blocked=false;
                        try{using(var writer=new System.IO.FileStream(path,System.IO.FileMode.Open,System.IO.FileAccess.Write,System.IO.FileShare.ReadWrite)){} }
                        catch(System.IO.IOException){blocked=true;}
                        if(!blocked)throw new System.IO.IOException("Snapshot write lock not active.");
                    }
                    Console.WriteLine("List validated and protected against in-place writes while held. "+PublicLists.Summary()+". Elapsed: "+clock.ElapsedMilliseconds+" ms.");return 0;
                }
                catch(Exception){Console.WriteLine("List validation failed; no filter started.");return 1;}
            }
            // Explicit invocation only. Builds and UI startup never call this.
            if(args.Length!=1||args[0]!="--check-services")
            {Console.WriteLine("Usage: LocalLink.Check.exe --check-services (public network requests; no filters)");return 2;}
            try
            {
                using(var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(5)))
                {
                    var rows=ServiceDiagnostics.RunAsync(ServiceCatalog.All().Select(x=>x.Id),null,timeout.Token).GetAwaiter().GetResult();
                    var report=new {CheckedAtUtc=DateTime.UtcNow.ToString("o"),Route=NetworkInfo.Summary(),FilterActivatedByCheck=false,Checks=rows};
                    Console.WriteLine(new JavaScriptSerializer().Serialize(report));
                }
                return 0;
            }
            catch(OperationCanceledException){Console.WriteLine("Checks cancelled or timed out; no filters started.");return 1;}
            catch(Exception){Console.WriteLine("Diagnostics failed; no filters started. Use UI details for each check.");return 1;}
        }
    }
}
