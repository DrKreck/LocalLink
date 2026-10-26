using System;
using System.Threading;
namespace LocalLink
{
    static class ListsCli
    {
        static int Main(string[] args)
        {
            if(args.Length!=2||args[0]!="--download-snapshot")
            {Console.WriteLine("Usage: LocalLink.Lists.exe --download-snapshot <output directory>. Downloads text lists only; never visits their sites or enables filters.");return 2;}
            try
            {
                using(var token=new CancellationTokenSource(TimeSpan.FromMinutes(7)))
                {
                    var result=PublicLists.DownloadSnapshotAsync(args[1],null,token.Token).GetAwaiter().GetResult();
                    Console.WriteLine(result.Message);Console.WriteLine("No filters activated or system network settings changed.");return 0;
                }
            }
            catch(Exception){Console.Error.WriteLine("List download failed or cancelled; previous snapshots remain untouched.");return 1;}
        }
    }
}
