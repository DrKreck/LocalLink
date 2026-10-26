using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LocalLink.Diagnostics
{
    // Fixed, unauthenticated public bootstrap targets. No Discord process,
    // account, cookies, credentials, local installation or user data is read.
    // Direct sockets deliberately bypass HTTP/PAC proxies, NOT VPN/OS routes.
    public static class DiscordDesktopDiagnostics
    {
        internal const int MaxHeaderBytes = 16384;
        internal const int MaxBodyBytes = 4096;
        internal const int MaxManifestBytes = 32768;
        private const int StageTimeoutMs = 8000;
        private sealed class Endpoint
        {
            internal string Key, Name, Host, Path;
            internal bool ReadGateway, ReadManifest;
        }
        private static readonly Endpoint[] Targets = {
            new Endpoint { Key="api", Name="Discord Desktop — API", Host="discord.com", Path="/api/v10/gateway", ReadGateway=true },
            new Endpoint { Key="updater", Name="Discord Desktop — обновление", Host="discord.com", Path="/api/downloads/distributions/app/installers/latest?channel=stable&platform=win&arch=x64" },
            new Endpoint { Key="cdn", Name="Discord Desktop — CDN", Host="stable.dl2.discordapp.net", Path="/" },
            new Endpoint { Key="manifest", Name="Discord Desktop — манифест обновления", Host="updates.discord.com", Path="/distributions/app/manifests/latest?channel=stable&platform=win&arch=x64", ReadManifest=true }
        };
        internal sealed class ProbeFailure : IOException
        {
            internal readonly string Category;
            internal ProbeFailure(string category) : base(category) { Category=category; }
        }
        internal sealed class HttpHead
        {
            internal int Status;
            internal int? Length;
            internal bool Chunked;
            internal string Encoding, Location;
        }

        public static async Task<List<CheckRow>> RunAsync(IProgress<CheckRow> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            // Four DNS operations at most; cancelled OS DNS may finish in the
            // background. No retries/address scans are spawned after timeout.
            var tasks=new List<Task<List<CheckRow>>>();
            foreach (var target in Targets) tasks.Add(ProbeEndpointAsync(target, progress, token));
            var groups=await Task.WhenAll(tasks).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var rows=new List<CheckRow>(32);
            foreach(var group in groups) rows.AddRange(group);
            return rows;
        }

        private static async Task<List<CheckRow>> ProbeEndpointAsync(Endpoint target, IProgress<CheckRow> progress, CancellationToken token)
        {
            var rows=new List<CheckRow>(8); IPAddress[] addresses=null;
            string dnsCategory="ok"; var dnsTimer=Stopwatch.StartNew();
            try { addresses=await Bounded(Dns.GetHostAddressesAsync(target.Host),token,null).ConfigureAwait(false); }
            catch(Exception error) { token.ThrowIfCancellationRequested(); dnsCategory=Classify(error,"dns"); }
            dnsTimer.Stop();
            foreach(var family in new[]{AddressFamily.InterNetwork,AddressFamily.InterNetworkV6})
            {
                token.ThrowIfCancellationRequested();
                string suffix=family==AddressFamily.InterNetwork ? "v4" : "v6";
                IPAddress selected=null; bool familyPresent=false;
                if(addresses!=null) foreach(var address in addresses)
                {
                    if(address==null || address.AddressFamily!=family) continue;
                    familyPresent=true;
                    if(IsPublicAddress(address)) { selected=address; break; }
                }
                string category=addresses==null ? dnsCategory : selected!=null ? "ok" : familyPresent ? "dns-nonpublic-address" : "dns-no-address";
                Add(rows,Row(target,suffix,"dns",selected!=null ? (bool?)true : null,category,dnsTimer.ElapsedMilliseconds),progress);
                if(selected==null)
                {
                    foreach(string stage in new[]{"tcp","tls","http"}) Add(rows,Row(target,suffix,stage,null,"not-tested-dns",0),progress);
                    continue;
                }
                await ProbeAddressAsync(target,suffix,selected,rows,progress,token).ConfigureAwait(false);
            }
            return rows;
        }

        private static async Task ProbeAddressAsync(Endpoint target,string family,IPAddress address,List<CheckRow> rows,IProgress<CheckRow> progress,CancellationToken token)
        {
            TcpClient socket;
            try { socket=new TcpClient(address.AddressFamily); }
            catch(Exception error)
            {
                token.ThrowIfCancellationRequested();
                Add(rows,Row(target,family,"tcp",false,Classify(error,"tcp"),0),progress);
                Add(rows,Row(target,family,"tls",null,"not-tested-tcp",0),progress);
                Add(rows,Row(target,family,"http",null,"not-tested-tcp",0),progress); return;
            }
            using(var client=socket)
            using(token.Register(delegate { SafeClose(client); }))
            {
                var timer=Stopwatch.StartNew(); string category="ok"; bool passed=false;
                try { await Bounded(client.ConnectAsync(address,443),token,delegate{SafeClose(client);}).ConfigureAwait(false); passed=true; }
                catch(Exception error) { token.ThrowIfCancellationRequested(); category=Classify(error,"tcp"); }
                Add(rows,Row(target,family,"tcp",passed,category,timer.ElapsedMilliseconds),progress);
                if(!passed) { Add(rows,Row(target,family,"tls",null,"not-tested-tcp",0),progress); Add(rows,Row(target,family,"http",null,"not-tested-tcp",0),progress); return; }
                timer.Restart(); passed=false; category="ok";
                // No permissive validation callback. Trust/hostname validation,
                // platform protocol selection and revocation checks remain on.
                using(var tls=new SslStream(client.GetStream(),false))
                {
                    try
                    {
                        await Bounded(tls.AuthenticateAsClientAsync(target.Host,new X509CertificateCollection(),SslProtocols.None,true),token,delegate{SafeClose(client);}).ConfigureAwait(false);
                        passed=tls.IsAuthenticated && tls.IsEncrypted && tls.IsSigned;
                        if(!passed) category="certificate-or-tls";
                    }
                    catch(Exception error) { token.ThrowIfCancellationRequested(); category=Classify(error,"tls"); }
                    Add(rows,Row(target,family,"tls",passed,category,timer.ElapsedMilliseconds),progress);
                    if(!passed) { Add(rows,Row(target,family,"http",null,"not-tested-tls",0),progress); return; }
                    timer.Restart(); bool? httpPassed=null; category="http-unavailable";
                    try
                    {
                        category=await Bounded(RequestAsync(tls,target,token),token,delegate{SafeClose(client);}).ConfigureAwait(false);
                        httpPassed=category=="gateway-url-valid" || category=="manifest-payload-valid" ||
                            category=="updater-redirect-valid" || category=="http-2xx" ? (bool?)true : null;
                        if(category=="gateway-payload-invalid" || category=="manifest-payload-invalid" ||
                            category=="updater-redirect-invalid") httpPassed=false;
                    }
                    catch(Exception error) { token.ThrowIfCancellationRequested(); category=Classify(error,"http"); httpPassed=false; }
                    Add(rows,Row(target,family,"http",httpPassed,category,timer.ElapsedMilliseconds),progress);
                }
            }
        }

        private static async Task<string> RequestAsync(SslStream stream,Endpoint target,CancellationToken token)
        {
            // HEAD for updater/CDN, bounded public JSON GET for Gateway and
            // manifest. No installer/CDN body is read or retained.
            string method=target.ReadGateway || target.ReadManifest ? "GET" : "HEAD";
            byte[] request=Encoding.ASCII.GetBytes(method+" "+target.Path+" HTTP/1.1\r\nHost: "+target.Host+
                "\r\nUser-Agent: LocalLink-Diagnostics/1.0\r\nAccept: application/json\r\nAccept-Encoding: identity\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(request,0,request.Length,token).ConfigureAwait(false);
            HttpHead head=ParseHeaders(await ReadHeadersAsync(stream,token).ConfigureAwait(false));
            // Never follow Location or fetch installer/media bodies, including
            // errors/redirects from the bounded REST endpoint.
            if(target.Key=="updater") return UpdaterCategory(head);
            if(!target.ReadGateway && !target.ReadManifest) return StatusCategory(head.Status);
            if(head.Status!=200) return head.Status>=200 && head.Status<300 ? "http-status-unexpected" : StatusCategory(head.Status);
            byte[] body=await ReadBodyAsync(stream,head,target.ReadManifest ? MaxManifestBytes : MaxBodyBytes,token).ConfigureAwait(false);
            return target.ReadManifest ? (IsManifestPayload(body) ? "manifest-payload-valid" : "manifest-payload-invalid") :
                (IsGatewayPayload(body) ? "gateway-url-valid" : "gateway-payload-invalid");
        }

        internal static async Task<byte[]> ReadHeadersAsync(Stream stream,CancellationToken token)
        {
            var bytes=new byte[MaxHeaderBytes]; int used=0;
            while(used<bytes.Length)
            {
                token.ThrowIfCancellationRequested();
                if(await stream.ReadAsync(bytes,used,1,token).ConfigureAwait(false)!=1) throw new ProbeFailure("http-incomplete-headers");
                used++;
                if(used>=4 && bytes[used-4]==13 && bytes[used-3]==10 && bytes[used-2]==13 && bytes[used-1]==10)
                { var result=new byte[used]; Buffer.BlockCopy(bytes,0,result,0,used); return result; }
            }
            throw new ProbeFailure("http-header-limit");
        }

        internal static HttpHead ParseHeaders(byte[] bytes)
        {
            if(bytes==null || bytes.Length<12 || bytes.Length>MaxHeaderBytes) throw new ProbeFailure("http-invalid-headers");
            foreach(byte b in bytes) if(b>126 || (b<32 && b!=9 && b!=13 && b!=10)) throw new ProbeFailure("http-invalid-headers");
            string text=Encoding.ASCII.GetString(bytes);
            if(!text.EndsWith("\r\n\r\n",StringComparison.Ordinal)) throw new ProbeFailure("http-incomplete-headers");
            string[] lines=text.Substring(0,text.Length-4).Split(new[]{"\r\n"},StringSplitOptions.None);
            string line=lines[0]; int status;
            if(line.Length<12 || line.IndexOf('\r')>=0 || line.IndexOf('\n')>=0 || (line.Substring(0,9)!="HTTP/1.1 " && line.Substring(0,9)!="HTTP/1.0 ") ||
                !Int32.TryParse(line.Substring(9,3),NumberStyles.None,CultureInfo.InvariantCulture,out status) ||
                status<200 || status>599 || (line.Length>12 && line[12]!=' ')) throw new ProbeFailure("http-invalid-status");
            var head=new HttpHead{Status=status}; bool lengthSeen=false,transferSeen=false,encodingSeen=false,locationSeen=false;
            for(int i=1;i<lines.Length;i++)
            {
                string name,value; ParseHeaderLine(lines[i],out name,out value);
                if(name=="content-length")
                {
                    int length;
                    if(lengthSeen || !Int32.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out length) || length<0) throw new ProbeFailure("http-invalid-framing");
                    head.Length=length; lengthSeen=true;
                }
                else if(name=="transfer-encoding")
                { if(transferSeen || !value.Equals("chunked",StringComparison.OrdinalIgnoreCase)) throw new ProbeFailure("http-invalid-framing"); head.Chunked=true; transferSeen=true; }
                else if(name=="content-encoding")
                { if(encodingSeen) throw new ProbeFailure("http-invalid-framing"); head.Encoding=value; encodingSeen=true; }
                else if(name=="location")
                { if(locationSeen || value.Length>2048) throw new ProbeFailure("http-invalid-redirect"); head.Location=value; locationSeen=true; }
            }
            if(head.Chunked && head.Length.HasValue) throw new ProbeFailure("http-invalid-framing");
            return head;
        }

        private static void ParseHeaderLine(string line,out string name,out string value)
        {
            int colon=line.IndexOf(':');
            if(colon<1 || line.IndexOf('\r')>=0 || line.IndexOf('\n')>=0) throw new ProbeFailure("http-invalid-headers");
            for(int i=0;i<colon;i++) if(!IsToken(line[i])) throw new ProbeFailure("http-invalid-headers");
            name=line.Substring(0,colon).ToLowerInvariant(); value=line.Substring(colon+1).Trim(' ','\t');
        }
        private static bool IsToken(char c)
        { return (c>='0'&&c<='9')||(c>='a'&&c<='z')||(c>='A'&&c<='Z')||"!#$%&'*+-.^_`|~".IndexOf(c)>=0; }

        internal static async Task<byte[]> ReadBodyAsync(Stream stream,HttpHead head,CancellationToken token)
        { return await ReadBodyAsync(stream,head,MaxBodyBytes,token).ConfigureAwait(false); }

        internal static async Task<byte[]> ReadBodyAsync(Stream stream,HttpHead head,int limit,CancellationToken token)
        {
            if(limit<1 || limit>MaxManifestBytes) throw new ProbeFailure("http-body-limit");
            if(head==null) throw new ProbeFailure("http-invalid-framing");
            if(!String.IsNullOrEmpty(head.Encoding) && !head.Encoding.Equals("identity",StringComparison.OrdinalIgnoreCase)) throw new ProbeFailure("http-body-encoding");
            if(head.Chunked && head.Length.HasValue) throw new ProbeFailure("http-invalid-framing");
            if(!head.Chunked)
            {
                if(!head.Length.HasValue) throw new ProbeFailure("http-unbounded-body");
                if(head.Length.Value<0 || head.Length.Value>limit) throw new ProbeFailure("http-body-limit");
                return await ReadExactAsync(stream,head.Length.Value,token).ConfigureAwait(false);
            }
            using(var body=new MemoryStream())
            {
                int chunks=0;
                while(true)
                {
                    if(++chunks>limit+1) throw new ProbeFailure("http-chunk-limit");
                    string line=await ReadLineAsync(stream,128,token).ConfigureAwait(false); int length;
                    if(line.Length<1 || line.Length>8 || !Int32.TryParse(line,NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out length) || length<0)
                        throw new ProbeFailure("http-invalid-chunk");
                    if(length==0)
                    {
                        int trailerBytes=0;
                        while(true)
                        {
                            string trailer=await ReadLineAsync(stream,1024,token).ConfigureAwait(false); trailerBytes+=trailer.Length+2;
                            if(trailerBytes>limit) throw new ProbeFailure("http-trailer-limit");
                            if(trailer.Length==0) return body.ToArray();
                            string name,value; ParseHeaderLine(trailer,out name,out value);
                            if(name=="content-length" || name=="transfer-encoding" || name=="content-encoding") throw new ProbeFailure("http-invalid-framing");
                        }
                    }
                    if(body.Length+length>limit) throw new ProbeFailure("http-body-limit");
                    byte[] chunk=await ReadExactAsync(stream,length,token).ConfigureAwait(false); body.Write(chunk,0,chunk.Length);
                    byte[] end=await ReadExactAsync(stream,2,token).ConfigureAwait(false);
                    if(end[0]!=13 || end[1]!=10) throw new ProbeFailure("http-invalid-chunk");
                }
            }
        }
        private static async Task<string> ReadLineAsync(Stream stream,int limit,CancellationToken token)
        {
            var bytes=new List<byte>(); var one=new byte[1];
            while(bytes.Count<=limit)
            {
                if(await stream.ReadAsync(one,0,1,token).ConfigureAwait(false)!=1) throw new ProbeFailure("http-incomplete-body");
                if(one[0]==13)
                {
                    if(await stream.ReadAsync(one,0,1,token).ConfigureAwait(false)!=1 || one[0]!=10) throw new ProbeFailure("http-invalid-chunk");
                    return Encoding.ASCII.GetString(bytes.ToArray());
                }
                if(one[0]<32 || one[0]>126) throw new ProbeFailure("http-invalid-chunk");
                bytes.Add(one[0]);
            }
            throw new ProbeFailure("http-line-limit");
        }
        private static async Task<byte[]> ReadExactAsync(Stream stream,int length,CancellationToken token)
        {
            var bytes=new byte[length]; int offset=0;
            while(offset<length)
            {
                token.ThrowIfCancellationRequested(); int read=await stream.ReadAsync(bytes,offset,length-offset,token).ConfigureAwait(false);
                if(read<1) throw new ProbeFailure("http-incomplete-body"); offset+=read;
            }
            return bytes;
        }
        internal static bool IsGatewayPayload(byte[] bytes)
        {
            if(bytes==null || bytes.Length<2 || bytes.Length>MaxBodyBytes) return false;
            try
            {
                string text=new UTF8Encoding(false,true).GetString(bytes);
                // Shape check prevents duplicate properties silently collapsed
                // by JavaScriptSerializer. Only the one public URL is needed.
                if(!Regex.IsMatch(text,@"\A\s*\{\s*""url""\s*:\s*""(?:[^""\\\x00-\x1F]|\\(?:[""\\/bfnrt]|u[0-9a-fA-F]{4}))*""\s*\}\s*\z",RegexOptions.CultureInvariant)) return false;
                var json=new JavaScriptSerializer{MaxJsonLength=MaxBodyBytes,RecursionLimit=4}.DeserializeObject(text) as Dictionary<string,object>;
                object raw; Uri uri;
                return json!=null && json.Count==1 && json.TryGetValue("url",out raw) && raw is string &&
                    Uri.TryCreate((string)raw,UriKind.Absolute,out uri) && uri.Scheme=="wss" &&
                    uri.Host.Equals("gateway.discord.gg",StringComparison.OrdinalIgnoreCase) && uri.Port==443 &&
                    uri.UserInfo.Length==0 && uri.AbsolutePath=="/" && uri.Query.Length==0 && uri.Fragment.Length==0;
            }
            catch(ArgumentException) { return false; }
            catch(InvalidOperationException) { return false; }
        }

        internal static string StatusCategory(int status)
        {
            if(status>=200 && status<300) return "http-2xx";
            if(status>=300 && status<400) return "http-redirect-not-followed";
            if(status>=400 && status<500) return "http-4xx-reachable";
            if(status>=500 && status<600) return "http-5xx-reachable";
            return "http-invalid-status";
        }
        internal static string UpdaterCategory(HttpHead head)
        {
            if(head==null) return "updater-redirect-invalid";
            // Current stable Windows endpoint redirects to this fixed official
            // CDN. Treat only that expected response as updater success. Never
            // connect to the Location, even when it is valid.
            if(head.Status==301 || head.Status==302 || head.Status==303 || head.Status==307 || head.Status==308)
                return IsUpdaterLocation(head.Location) ? "updater-redirect-valid" : "updater-redirect-invalid";
            return head.Status>=400 && head.Status<=599 ? StatusCategory(head.Status) : "updater-response-unexpected";
        }
        internal static bool IsUpdaterLocation(string value)
        {
            if(String.IsNullOrEmpty(value) || value.Length>2048) return false;
            foreach(char c in value) if(Char.IsWhiteSpace(c) || Char.IsControl(c) || c=='\\') return false;
            Uri uri;
            return Uri.TryCreate(value,UriKind.Absolute,out uri) && uri.Scheme=="https" && uri.Port==443 &&
                uri.Host.Equals("stable.dl2.discordapp.net",StringComparison.OrdinalIgnoreCase) && uri.UserInfo.Length==0 &&
                uri.Fragment.Length==0 && uri.AbsolutePath.Length>1;
        }
        internal static bool IsManifestPayload(byte[] bytes)
        {
            if(bytes==null || bytes.Length<2 || bytes.Length>MaxManifestBytes) return false;
            try
            {
                string text=new UTF8Encoding(false,true).GetString(bytes);
                var json=new JavaScriptSerializer{MaxJsonLength=MaxManifestBytes,RecursionLimit=12}.DeserializeObject(text) as Dictionary<string,object>;
                object fullRaw,modulesRaw,versionRaw;
                if(json==null || !json.TryGetValue("full",out fullRaw) || !json.TryGetValue("modules",out modulesRaw)) return false;
                var full=fullRaw as Dictionary<string,object>;
                var modules=modulesRaw as Dictionary<string,object>;
                if(full==null || modules==null || modules.Count<1 || modules.Count>256 || !full.TryGetValue("host_version",out versionRaw)) return false;
                var version=versionRaw as object[];
                if(version==null || version.Length!=3) return false;
                foreach(object part in version) if(!(part is int) || (int)part<0) return false;
                foreach(var module in modules) if(String.IsNullOrEmpty(module.Key) || module.Key.Length>128 || !(module.Value is Dictionary<string,object>)) return false;
                return true;
            }
            catch(ArgumentException) { return false; }
            catch(InvalidOperationException) { return false; }
        }
        internal static bool IsPublicAddress(IPAddress address)
        {
            if(address==null || IPAddress.IsLoopback(address)) return false;
            var b=address.GetAddressBytes();
            if(address.AddressFamily==AddressFamily.InterNetwork)
            {
                if(b[0]==0 || b[0]==10 || b[0]==127 || b[0]>=224 || (b[0]==100 && b[1]>=64 && b[1]<=127) ||
                    (b[0]==169 && b[1]==254) || (b[0]==172 && b[1]>=16 && b[1]<=31) ||
                    (b[0]==192 && (b[1]==168 || (b[1]==0 && (b[2]==0 || b[2]==2)))) ||
                    (b[0]==198 && (b[1]==18 || b[1]==19 || (b[1]==51 && b[2]==100))) ||
                    (b[0]==203 && b[1]==0 && b[2]==113)) return false;
                return true;
            }
            // Only global unicast 2000::/3, excluding documentation and Teredo;
            // no LAN, loopback, mapped IPv4, scoped or multicast destinations.
            return address.AddressFamily==AddressFamily.InterNetworkV6 && address.ScopeId==0 && (b[0]&0xe0)==0x20 &&
                !(b[0]==0x20 && b[1]==1 && ((b[2]==0x0d && b[3]==0xb8) || (b[2]==0 && b[3]==0)));
        }
        internal static string Classify(Exception error,string stage)
        {
            var failure=error as ProbeFailure; if(failure!=null) return failure.Category;
            if(error is TimeoutException || error is OperationCanceledException) return stage+"-timeout";
            if(error is AuthenticationException) return "certificate-or-tls";
            var socket=error as SocketException;
            if(socket!=null)
            {
                if(socket.SocketErrorCode==SocketError.ConnectionRefused) return "tcp-refused";
                if(socket.SocketErrorCode==SocketError.ConnectionReset) return "tcp-reset";
                if(socket.SocketErrorCode==SocketError.NetworkUnreachable || socket.SocketErrorCode==SocketError.HostUnreachable || socket.SocketErrorCode==SocketError.AddressFamilyNotSupported) return "network-unreachable";
                if(socket.SocketErrorCode==SocketError.TimedOut) return stage+"-timeout";
                return stage=="dns" ? "dns-failed" : "socket-failed";
            }
            if(error is IOException && error.InnerException!=null) return Classify(error.InnerException,stage);
            if(error is IOException || error is ObjectDisposedException) return stage+"-closed";
            return stage+"-unavailable";
        }
        private static async Task<T> Bounded<T>(Task<T> pending,CancellationToken token,Action abort)
        {
            using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                Task delay=Task.Delay(StageTimeoutMs,deadline.Token);
                if(await Task.WhenAny(pending,delay).ConfigureAwait(false)!=pending)
                {
                    if(abort!=null) abort();
                    Observe(pending); token.ThrowIfCancellationRequested(); throw new TimeoutException();
                }
                deadline.Cancel(); token.ThrowIfCancellationRequested();
                return await pending.ConfigureAwait(false);
            }
        }
        private static async Task Bounded(Task pending,CancellationToken token,Action abort)
        { await Bounded(AsBoolean(pending),token,abort).ConfigureAwait(false); }
        private static async Task<bool> AsBoolean(Task pending) { await pending.ConfigureAwait(false); return true; }
        private static void Observe(Task task) { task.ContinueWith(delegate(Task t){var ignored=t.Exception;},TaskContinuationOptions.OnlyOnFaulted); }
        private static void SafeClose(TcpClient client) { try{client.Close();}catch(ObjectDisposedException){} }
        private static CheckRow Row(Endpoint target,string family,string stage,bool? passed,string category,long ms)
        {
            string stageKey=stage=="http" ? "" : "."+stage;
            string extra=category=="http-4xx-reachable" || category=="http-5xx-reachable" || category=="http-redirect-not-followed" ?
                " Получен HTTP-ответ по проверенному TLS; это не отсутствие связи. Переходы не выполняются." :
                category=="updater-redirect-valid" ? " Получен ожидаемый HTTPS-адрес CDN; переход и загрузка установщика не выполняются." :
                category=="dns-no-address" ? " Для семейства адресов нет DNS-ответа; это не доказательство блокировки." : "";
            return new CheckRow { ServiceId="discord", Key="discord.desktop."+target.Key+"."+family+stageKey,
                Name=target.Name+" — "+family.ToUpperInvariant()+" "+stage.ToUpperInvariant(),
                DiagnosticCode=category,
                Passed=passed, State=passed==true ? "Доступно" : passed==false ? "Не подтверждено" : "Не определено", Milliseconds=ms,
                Detail="direct-socket; proxy=not-used; family="+family+"; stage="+stage+"; category="+category+"."+extra+
                    " Проверяет публичный bootstrap, не вход, голос, сообщения или работоспособность приложения. VPN/маршрут ОС сохраняется." };
        }
        private static void Add(List<CheckRow> rows,CheckRow row,IProgress<CheckRow> progress)
        {
            rows.Add(row);
            if(progress!=null) progress.Report(new CheckRow { ServiceId=row.ServiceId,Key=row.Key,Name=row.Name,State=row.State,Detail=row.Detail,Passed=row.Passed,Milliseconds=row.Milliseconds,DiagnosticCode=row.DiagnosticCode });
        }
    }
}
