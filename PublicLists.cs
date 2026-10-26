using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LocalLink
{
    public sealed class ListUpdateResult { public int DomainCount,SourceCount; public string Message; }
    public sealed class ListSnapshot
    {
        public string FileName,Sha256,UpdatedUtc;
        public int DomainCount,SourceCount,RejectedLines;
        public string[] Sources;
    }
    // Downloads DATA only. No remote scripts, URL probes, browser information,
    // executable updates or DNS/route changes. Old snapshots are never removed.
    public static class PublicLists
    {
        public static readonly string[] Sources={"https://community.antifilter.download/list/domains.lst","https://antifilter.download/list/domains.lst"};
        public const int MaxBytes=64*1024*1024, MaxDomains=2000000;
        static readonly SemaphoreSlim Updating=new SemaphoreSlim(1,1);
        static readonly object VerifiedLock=new object();
        static readonly HashSet<string> VerifiedContents=new HashSet<string>(StringComparer.Ordinal);
        static readonly string UserFolder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LocalLink","lists");
        static readonly string BundledFolder=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"lists");
        public static string NormalizeDomain(string raw)
        {
            if(raw==null||raw.Length>260)return null;
            string value=raw.Trim().TrimStart('\uFEFF');
            if(value.StartsWith("#",StringComparison.Ordinal)||value.Length==0)return null;
            if(value.StartsWith("*.",StringComparison.Ordinal))value=value.Substring(2);
            if(value.EndsWith(".",StringComparison.Ordinal))value=value.Substring(0,value.Length-1);
            if(value.Length<3)return null;
            bool unicode=false;
            foreach(char c in value)
            {
                if(c>127)unicode=true;
                if(Char.IsControl(c)||Char.IsWhiteSpace(c)||Char.GetUnicodeCategory(c)==UnicodeCategory.Format||
                    (c<128&&!(c>='a'&&c<='z')&&!(c>='A'&&c<='Z')&&!(c>='0'&&c<='9')&&c!='.'&&c!='-'))return null;
            }
            try{if(unicode)value=new IdnMapping{UseStd3AsciiRules=true}.GetAscii(value);value=value.ToLowerInvariant();}catch(ArgumentException){return null;}
            if(value.Length>253)return null;
            var labels=value.Split('.'); if(labels.Length<2)return null;
            foreach(var label in labels)
            {
                if(label.Length==0||label.Length>63||label[0]=='-'||label[label.Length-1]=='-')return null;
                foreach(char c in label)if(!(c>='a'&&c<='z')&&!(c>='0'&&c<='9')&&c!='-')return null;
            }
            IPAddress address; if(IPAddress.TryParse(value,out address))return null;
            string tld=labels[labels.Length-1];
            if(tld.All(Char.IsDigit)||tld=="local"||tld=="localhost"||tld=="internal"||tld=="lan"||tld=="invalid"||tld=="test"||tld=="example"||tld=="onion")return null;
            return value;
        }
        public static int Parse(string text,HashSet<string> into,CancellationToken token)
        {
            if(text==null||text.Length>MaxBytes)throw new ArgumentException("Список превышает лимит размера.");
            int rejected=0,lineNumber=0;
            using(var reader=new StringReader(text))
            {
                string line;
                while((line=reader.ReadLine())!=null)
                {
                    if((++lineNumber&4095)==0)token.ThrowIfCancellationRequested();
                    string domain=NormalizeDomain(line);
                    if(domain==null){if(!String.IsNullOrWhiteSpace(line)&&!line.TrimStart().StartsWith("#",StringComparison.Ordinal))rejected++;continue;}
                    into.Add(domain);if(into.Count>MaxDomains)throw new IOException("Слишком много доменов в списке.");
                }
            }
            token.ThrowIfCancellationRequested();return rejected;
        }
        public static async Task<ListUpdateResult> UpdateAsync(IProgress<string> progress,CancellationToken token)
        {return await DownloadSnapshotAsync(UserFolder,progress,token).ConfigureAwait(false);}
        // Explicit packaging/test entry point; never invoked by build or app startup.
        public static async Task<ListUpdateResult> DownloadSnapshotAsync(string folder,IProgress<string> progress,CancellationToken token)
        {
            await Updating.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var domains=new HashSet<string>(StringComparer.Ordinal);
                var succeeded=new List<string>();int rejected=0;
                foreach(string source in Sources)
                {
                    token.ThrowIfCancellationRequested();
                    if(progress!=null)progress.Report("Загрузка списка: "+new Uri(source).Host);
                    try
                    {
                        string data=await Download(source,token).ConfigureAwait(false);
                        var parsed=new HashSet<string>(StringComparer.Ordinal);
                        int bad=await Task.Run(()=>Parse(data,parsed,token),token).ConfigureAwait(false);
                        if(parsed.Count<100)throw new IOException("Ответ не похож на список доменов.");
                        foreach(var domain in parsed){domains.Add(domain);if(domains.Count>MaxDomains)throw new IOException("Лимит списка.");}
                        rejected+=bad;succeeded.Add(source);
                    }
                    catch(OperationCanceledException){throw;}
                    catch(Exception e)
                    {
                        token.ThrowIfCancellationRequested();
                        if(!(e is WebException||e is IOException||e is ArgumentException))throw;
                        if(progress!=null)progress.Report("Источник недоступен или не прошёл проверку: "+new Uri(source).Host);
                    }
                }
                if(succeeded.Count==0)throw new IOException("Источники недоступны. Предыдущий список сохранён.");
                token.ThrowIfCancellationRequested();
                var result=await Task.Run(()=>SaveSnapshot(folder,domains,succeeded.ToArray(),rejected,token),token).ConfigureAwait(false);
                return new ListUpdateResult{DomainCount=result.DomainCount,SourceCount=result.SourceCount,
                    Message=result.DomainCount.ToString("N0")+" доменов · источников "+result.SourceCount+"/"+Sources.Length+
                    (result.SourceCount<Sources.Length?" · неполное обновление":"")};
            }
            finally{Updating.Release();}
        }
        static async Task<string> Download(string source,CancellationToken token)
        {
            var request=(HttpWebRequest)WebRequest.Create(source);
            request.Method="GET";request.AllowAutoRedirect=false;request.UseDefaultCredentials=false;request.Credentials=null;
            request.CookieContainer=null;request.UserAgent="LocalLink/0.3 lists";
            request.AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate;
            request.Timeout=180000;request.ReadWriteTimeout=15000;
            using(var limit=CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                limit.CancelAfter(180000);
                using(limit.Token.Register(request.Abort))
                using(var response=(HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                {
                    if(response.StatusCode!=HttpStatusCode.OK||response.ContentLength>MaxBytes)throw new IOException("Недопустимый ответ списка.");
                    using(var input=response.GetResponseStream())using(var data=new MemoryStream())
                    {
                        byte[] block=new byte[32768];int count;
                        while((count=await input.ReadAsync(block,0,block.Length,limit.Token).ConfigureAwait(false))>0)
                        {if(data.Length+count>MaxBytes)throw new IOException("Слишком большой список.");data.Write(block,0,count);}
                        return new UTF8Encoding(false,true).GetString(data.ToArray());
                    }
                }
            }
        }
        public static ListSnapshot SaveSnapshot(string folder,IEnumerable<string> domains,string[] sources,int rejected,CancellationToken token)
        {
            // Ordered insertion replaces the monolithic Distinct/OrderBy/Any/
            // Sum chain. Each bounded domain operation observes cancellation;
            // no enormous array sort or exception-throwing comparer is needed.
            token.ThrowIfCancellationRequested();
            if(domains==null)throw new ArgumentNullException("domains");
            if(sources==null)throw new ArgumentNullException("sources");
            var data=new SortedSet<string>(StringComparer.Ordinal);long bytes=0;
            foreach(string domain in domains)
            {
                token.ThrowIfCancellationRequested();
                if(domain==null||NormalizeDomain(domain)!=domain)throw new IOException("Недопустимые домены.");
                if(data.Add(domain))
                {
                    if(data.Count>MaxDomains)throw new IOException("Недопустимые домены.");
                    bytes+=(long)domain.Length+1;
                    if(bytes>MaxBytes)throw new IOException("Объединённый список превышает лимит размера.");
                }
                token.ThrowIfCancellationRequested();
            }
            token.ThrowIfCancellationRequested();
            if(data.Count<1)throw new IOException("Недопустимые домены.");
            folder=Path.GetFullPath(folder);SafePath(folder);token.ThrowIfCancellationRequested();Directory.CreateDirectory(folder);
            string name="sites-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")+".lst";
            string path=Path.Combine(folder,name);SafePath(path);token.ThrowIfCancellationRequested();
            using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            using(var writer=new StreamWriter(output,new UTF8Encoding(false)))
                foreach(var domain in data){token.ThrowIfCancellationRequested();writer.Write(domain);writer.Write('\n');}
            var info=new ListSnapshot{FileName=name,Sha256=Hash(path,token),DomainCount=data.Count,SourceCount=sources.Length,Sources=sources,RejectedLines=rejected,UpdatedUtc=DateTime.UtcNow.ToString("o")};
            string json=new JavaScriptSerializer().Serialize(info);
            token.ThrowIfCancellationRequested();
            string snapshot=path+".json";File.WriteAllText(snapshot,json,new UTF8Encoding(false));
            string pointer=Path.Combine(folder,"current.json"),pending=Path.Combine(folder,"current-"+Guid.NewGuid().ToString("N")+".json");
            SafePath(pointer);token.ThrowIfCancellationRequested();File.WriteAllText(pending,json,new UTF8Encoding(false));
            token.ThrowIfCancellationRequested();
            if(File.Exists(pointer))File.Replace(pending,pointer,Path.Combine(folder,"previous-"+Guid.NewGuid().ToString("N")+".json"));
            else File.Move(pending,pointer);
            return info;
        }
        static string Hash(string path,CancellationToken token){using(var input=File.OpenRead(path))return HashStream(input,token);}
        // Incremental hashing keeps a large local snapshot cancellable. This
        // reads only the supplied stream and never starts a process or request.
        public static string HashStream(Stream stream,CancellationToken token)
        {
            if(stream==null)throw new ArgumentNullException("stream");
            token.ThrowIfCancellationRequested();
            using(var sha=SHA256.Create())
            {
                byte[] block=new byte[65536];int count;
                while((count=stream.Read(block,0,block.Length))>0)
                {token.ThrowIfCancellationRequested();sha.TransformBlock(block,0,count,block,0);}
                token.ThrowIfCancellationRequested();sha.TransformFinalBlock(new byte[0],0,0);
                return BitConverter.ToString(sha.Hash).Replace("-","").ToLowerInvariant();
            }
        }
        public static void SafePath(string path)
        {
            for(string p=Path.GetFullPath(path);!String.IsNullOrEmpty(p);p=Path.GetDirectoryName(p))
                if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("Путь списка содержит перенаправление.");
        }
        static string ActiveFolder(){return File.Exists(Path.Combine(UserFolder,"current.json"))?UserFolder:BundledFolder;}
        static ListSnapshot ReadInfo(string file)
        {
            SafePath(file);if(!File.Exists(file)||new FileInfo(file).Length>8192)throw new IOException("Список ещё не загружен.");
            var info=new JavaScriptSerializer{MaxJsonLength=8192}.Deserialize<ListSnapshot>(File.ReadAllText(file,Encoding.UTF8));
            if(info==null||String.IsNullOrEmpty(info.FileName)||Path.GetFileName(info.FileName)!=info.FileName||
                !info.FileName.StartsWith("sites-",StringComparison.Ordinal)||!info.FileName.EndsWith(".lst",StringComparison.Ordinal)||
                info.FileName.IndexOfAny(Path.GetInvalidFileNameChars())>=0||info.DomainCount<1||info.DomainCount>MaxDomains||
                info.Sha256==null||info.Sha256.Length!=64||info.Sha256.Any(c=>!Uri.IsHexDigit(c)))throw new IOException("Некорректный паспорт списка.");
            return info;
        }
        public static string Summary()
        {
            try{var i=ReadInfo(Path.Combine(ActiveFolder(),"current.json"));return i.DomainCount.ToString("N0")+" доменов · источников "+i.SourceCount+"/"+Sources.Length;}
            catch{return "Список не загружен";}
        }
        public static string GetVerifiedPath()
        {
            string path=GetActivePath(CancellationToken.None);using(var input=OpenVerified(path)){}return path;
        }
        // UI capture may read the small local passport, but content hashing and
        // line validation belong to Engine.PrepareStartAsync's worker. The path
        // is NOT a verification result until OpenVerified succeeds.
        public static string GetActivePath(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string folder=ActiveFolder();var info=ReadInfo(Path.Combine(folder,"current.json"));
            string path=Path.Combine(folder,info.FileName);SafePath(path);
            token.ThrowIfCancellationRequested();return path;
        }
        // Caller holds this read handle during engine lifetime, preventing edits
        // to a snapshot after validation. This is input validation, not DRM.
        public static FileStream OpenVerified(string path)
        {return OpenVerified(path,CancellationToken.None);}
        public static FileStream OpenVerified(string path,CancellationToken token)
        {int count;return OpenVerified(path,token,out count);}
        public static FileStream OpenVerified(string path,CancellationToken token,out int verifiedDomainCount)
        {
            verifiedDomainCount=0;
            token.ThrowIfCancellationRequested();
            path=Path.GetFullPath(path);string parent=Path.GetDirectoryName(path);
            if(!String.Equals(parent,Path.GetFullPath(UserFolder),StringComparison.OrdinalIgnoreCase)&&
                !String.Equals(parent,Path.GetFullPath(BundledFolder),StringComparison.OrdinalIgnoreCase))throw new IOException("Список должен принадлежать LocalLink.");
            SafePath(path);var info=ReadInfo(path+".json");
            if(Path.GetFileName(path)!=info.FileName)throw new IOException("Паспорт не соответствует списку.");
            var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            try
            {
                if(input.Length>MaxBytes||HashStream(input,token)!=info.Sha256)throw new IOException("Список повреждён. Обновите его.");
                string verificationKey=info.Sha256+":"+info.DomainCount;
                lock(VerifiedLock)
                    if(VerifiedContents.Contains(verificationKey)){token.ThrowIfCancellationRequested();input.Position=0;verifiedDomainCount=info.DomainCount;return input;}
                input.Position=0;int count=0;
                using(var reader=new StreamReader(input,new UTF8Encoding(false,true),false,8192,true))
                {
                    string line;while((line=reader.ReadLine())!=null)
                    {if((count&255)==0)token.ThrowIfCancellationRequested();if(NormalizeDomain(line)!=line||line.Length==0)throw new IOException("Недопустимая строка списка.");count++;}
                }
                token.ThrowIfCancellationRequested();
                if(count!=info.DomainCount)throw new IOException("Неверное число строк списка.");
                lock(VerifiedLock)VerifiedContents.Add(verificationKey);
                input.Position=0;verifiedDomainCount=count;return input;
            }
            catch{input.Dispose();throw;}
        }
    }
}
