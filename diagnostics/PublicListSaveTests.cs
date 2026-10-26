using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace LocalLink
{
    // Unique package-local fixtures only. No downloads, filters, drivers or
    // deletion: successful snapshots are deliberately left for inspection.
    static class PublicListSaveTests
    {
        static int assertions;
        static int Main()
        {
            try
            {
                string fixture=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"offline-fixtures","list-save-"+Guid.NewGuid().ToString("N"));
                var sources=new[]{"https://example.invalid/domains.lst"};
                string beforeFolder=Path.Combine(fixture,"pre-cancelled");var never=new TrackingEnumerable();
                Expect<OperationCanceledException>(()=>PublicLists.SaveSnapshot(beforeFolder,never,sources,0,new CancellationToken(true)),"pre-cancelled save stops before preparation");
                Check(!never.Enumerated,"pre-cancelled save does not enumerate domains");
                Check(!Directory.Exists(beforeFolder),"pre-cancelled save does not create folder");

                string duringFolder=Path.Combine(fixture,"enumeration-cancelled");
                using(var source=new CancellationTokenSource())
                    Expect<OperationCanceledException>(()=>PublicLists.SaveSnapshot(duringFolder,CancelDuringEnumeration(source),sources,0,source.Token),"cancellation during enumeration stops ordered insertion");
                Check(!Directory.Exists(duringFolder),"enumeration cancellation does not create folder");

                string saved=Path.Combine(fixture,"saved");
                var domains=new[]{"zeta.org","abc.org","xn--e1afmkfd.xn--p1ai","x.y.org","abc.org","alpha.org","zeta.org"};
                var info=PublicLists.SaveSnapshot(saved,domains,sources,3,CancellationToken.None);
                string data=File.ReadAllText(Path.Combine(saved,info.FileName),Encoding.UTF8);
                Check(data=="abc.org\nalpha.org\nx.y.org\nxn--e1afmkfd.xn--p1ai\nzeta.org\n","stable ordinal order and duplicate removal");
                Check(info.DomainCount==5&&info.SourceCount==1&&info.RejectedLines==3,"snapshot metadata preserves unique counts");
                using(var input=File.OpenRead(Path.Combine(saved,info.FileName)))
                    Check(info.Sha256==PublicLists.HashStream(input,CancellationToken.None),"saved digest matches incremental cancellable SHA256");
                string pointer=Path.Combine(saved,"current.json");
                byte[] previous=File.ReadAllBytes(pointer);int count=Directory.GetFiles(saved).Length;
                using(var source=new CancellationTokenSource())
                    Expect<OperationCanceledException>(()=>PublicLists.SaveSnapshot(saved,CancelDuringEnumeration(source),sources,0,source.Token),"existing snapshot update cancels during preparation");
                Check(Equal(previous,File.ReadAllBytes(pointer)),"current.json remains byte-identical after cancelled update");
                Check(Directory.GetFiles(saved).Length==count,"cancelled preparation writes no snapshot or pointer files");

                foreach(var invalid in new[]{new string[]{null},new[]{"EXAMPLE.ORG"},new[]{"--bad.org"},new[]{"https://example.org"},new string[0]})
                {
                    string folder=Path.Combine(fixture,"invalid-"+Guid.NewGuid().ToString("N"));
                    Expect<IOException>(()=>PublicLists.SaveSnapshot(folder,invalid,sources,0,CancellationToken.None),"invalid or empty domain set is rejected");
                    Check(!Directory.Exists(folder),"invalid preparation creates no folder");
                }
                var latest=new JavaScriptSerializer().Deserialize<ListSnapshot>(File.ReadAllText(pointer,Encoding.UTF8));
                Check(latest.FileName==info.FileName&&latest.Sha256==info.Sha256,"cancelled update preserves active metadata and snapshot");
                Console.WriteLine("PASSED "+assertions+" offline list-save assertions. Unique workspace fixtures retained; no network or deletion.");return 0;
            }
            catch(Exception e){Console.Error.WriteLine(e);return 1;}
        }
        static IEnumerable<string> CancelDuringEnumeration(CancellationTokenSource source)
        {yield return "first.org";yield return "second.org";source.Cancel();yield return "third.org";}
        static bool Equal(byte[] a,byte[] b)
        {if(a.Length!=b.Length)return false;for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;return true;}
        static void Expect<T>(Action action,string name) where T:Exception
        {bool rejected=false;try{action();}catch(T){rejected=true;}Check(rejected,name);}
        static void Check(bool pass,string name){if(!pass)throw new Exception("FAIL "+name);assertions++;Console.WriteLine("PASS "+name);}
        sealed class TrackingEnumerable:IEnumerable<string>
        {
            public bool Enumerated;
            public IEnumerator<string> GetEnumerator(){Enumerated=true;yield return "example.org";}
            IEnumerator IEnumerable.GetEnumerator(){return GetEnumerator();}
        }
    }
}
