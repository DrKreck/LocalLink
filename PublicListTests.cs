using System;
using System.Collections.Generic;
using System.Threading;

namespace LocalLink
{
    public static class PublicListTests
    {
        public static int Run()
        {
            foreach(string invalid in new[]{"--lua-init=evil","^example.com","https://example.com/path","example.com:443","127.0.0.1","192.168.1.1","foo.local","foo.onion","bad\t.example.com","a..org","-a.org","a-.org","a.org/evil","a.org\0","*","com","x.123","example.com #comment","# comment","<html>oops</html>"})
                Check(PublicLists.NormalizeDomain(invalid)==null,"reject unsafe list input");
            Check(PublicLists.NormalizeDomain(" EXAMPLE.ORG. ")=="example.org","canonical domain");
            Check(PublicLists.NormalizeDomain("*.example.org")=="example.org","suffix wildcard canonicalization");
            Check(PublicLists.NormalizeDomain("пример.рф")=="xn--e1afmkfd.xn--p1ai","IDN canonicalization");
            var set=new HashSet<string>(StringComparer.Ordinal);
            int rejected=PublicLists.Parse("#comment\nEXAMPLE.ORG\n*.example.org\nother.org\nhttp://bad.org/\n",set,CancellationToken.None);
            Check(set.Count==2&&rejected==1,"deduplication and rejected count");
            bool cancelled=false;try{PublicLists.Parse("example.org",new HashSet<string>(),new CancellationToken(true));}catch(OperationCanceledException){cancelled=true;}
            Check(cancelled,"list cancellation");
            Console.WriteLine("Public list tests passed (offline; no downloads or writes).");return 0;
        }
        static void Check(bool pass,string message){if(!pass)throw new Exception("List test failed: "+message);}
    }
}
