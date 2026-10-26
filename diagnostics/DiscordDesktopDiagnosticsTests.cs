// Synthetic in-memory fixtures only. No network, driver, Discord account,
// hardware, local application installation or user preferences are accessed.
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Reflection;
using System.Collections.Generic;
using LocalLink;
using LocalLink.Diagnostics;

internal static class DiscordDesktopDiagnosticsTests
{
    static int assertions;
    static int Main(string[] args)
    {
        try
        {
            if(args.Length==1 && args[0]=="--fixed-public-network")
            {
                // Explicit opt-in harness; default tests remain wholly offline.
                // Uses only the production fixed unauthenticated endpoints.
                using(var deadline=new CancellationTokenSource(70000))
                    foreach(var row in DiscordDesktopDiagnostics.RunAsync(null,deadline.Token).GetAwaiter().GetResult())
                        Console.WriteLine(row.Key+" | "+row.Passed+" | "+row.Milliseconds+"ms | "+row.Detail);
                return 0;
            }
            if(args.Length!=0) return 2;
            try { DiscordDesktopDiagnostics.RunAsync(null,new CancellationToken(true)).GetAwaiter().GetResult(); throw new Exception("Precancel ignored"); }
            catch(OperationCanceledException) { assertions++; }
            string valid="{\"url\":\"wss://gateway.discord.gg\"}";
            Check(DiscordDesktopDiagnostics.IsGatewayPayload(Utf8(valid)),"public Gateway URL");
            Check(DiscordDesktopDiagnostics.IsGatewayPayload(Utf8(" { \"url\" : \"wss:\\/\\/gateway.discord.gg\\/\" } ")),"escaped JSON URL");
            foreach(string invalid in new[]{"", "{}", "null", "[]", "{\"url\":123}", "{\"url\":{}}",
                "{\"url\":\"wss://wrong.test\"}", "{\"url\":\"https://gateway.discord.gg\"}",
                "{\"url\":\"wss://user@gateway.discord.gg\"}", "{\"url\":\"wss://gateway.discord.gg:8443\"}",
                "{\"url\":\"wss://gateway.discord.gg/?private=data\"}", "{\"url\":\"wss://gateway.discord.gg/#secret\"}",
                "{\"url\":\"wss://gateway.discord.gg/path\"}", "{\"url\":\"wss://gateway.discord.gg.evil.test\"}",
                "{\"url\":\"wss://gateway.discord.gg\",\"extra\":1}",
                "{\"url\":\"wss://wrong.test\",\"url\":\"wss://gateway.discord.gg\"}", valid+"garbage", "{\"url\":\"\u0001\"}"})
                Check(!DiscordDesktopDiagnostics.IsGatewayPayload(Utf8(invalid)),"invalid Gateway payload");
            Check(!DiscordDesktopDiagnostics.IsGatewayPayload(null),"null Gateway payload");
            Check(!DiscordDesktopDiagnostics.IsGatewayPayload(new byte[4097]),"Gateway payload bounded");
            Check(!DiscordDesktopDiagnostics.IsGatewayPayload(new byte[]{123,34,117,114,108,34,58,34,0xff,34,125}),"invalid UTF8");
            Check(DiscordDesktopDiagnostics.StatusCategory(200)=="http-2xx","HTTP200 reachable");
            Check(DiscordDesktopDiagnostics.StatusCategory(302)=="http-redirect-not-followed","redirect not followed");
            Check(DiscordDesktopDiagnostics.StatusCategory(403)=="http-4xx-reachable","CDN forbidden is TLS reachable");
            Check(DiscordDesktopDiagnostics.StatusCategory(404)=="http-4xx-reachable","CDN missing path is reachable");
            Check(DiscordDesktopDiagnostics.StatusCategory(503)=="http-5xx-reachable","server error is reachable");
            Check(DiscordDesktopDiagnostics.StatusCategory(100)=="http-invalid-status","interim not final");
            const string redirect="https://stable.dl2.discordapp.net/distro/app/stable/win/x64/1.0.9260/DiscordSetup.exe";
            Check(DiscordDesktopDiagnostics.IsUpdaterLocation(redirect),"official updater redirect");
            foreach(string location in new[]{"", "/relative", "http://stable.dl2.discordapp.net/a", "https://user@stable.dl2.discordapp.net/a",
                "https://stable.dl2.discordapp.net:8443/a", "https://stable.dl2.discordapp.net/a#fragment", "https://stable.dl2.discordapp.net/",
                "https://stable.dl2.discordapp.net.evil.test/a", "https://evil.test/a", "https://stable.dl2.discordapp.net/a b",
                "https://stable.dl2.discordapp.net/a\tb", "https://stable.dl2.discordapp.net/a\\b", "https://stable.dl2.discordapp.net/"+new string('a',2048)})
                Check(!DiscordDesktopDiagnostics.IsUpdaterLocation(location),"unexpected updater Location refused");
            Check(!DiscordDesktopDiagnostics.IsUpdaterLocation(null),"missing Location refused");
            foreach(int status in new[]{301,302,303,307,308})
                Check(DiscordDesktopDiagnostics.UpdaterCategory(new DiscordDesktopDiagnostics.HttpHead{Status=status,Location=redirect})=="updater-redirect-valid","expected updater redirect status");
            foreach(int status in new[]{200,204,300,304,305,306,399})
                Check(DiscordDesktopDiagnostics.UpdaterCategory(new DiscordDesktopDiagnostics.HttpHead{Status=status,Location=redirect})=="updater-response-unexpected","unexpected updater status cannot pass");
            Check(DiscordDesktopDiagnostics.UpdaterCategory(new DiscordDesktopDiagnostics.HttpHead{Status=302,Location="https://evil.test/a"})=="updater-redirect-invalid","redirect destination validated without fetch");
            Check(DiscordDesktopDiagnostics.UpdaterCategory(new DiscordDesktopDiagnostics.HttpHead{Status=404})=="http-4xx-reachable","HTTP404 reachable not updater success");
            var redirectHead=DiscordDesktopDiagnostics.ParseHeaders(Ascii("HTTP/1.1 302 Found\r\nLocation: "+redirect+"\r\nContent-Length: 377\r\n\r\n"));
            Check(DiscordDesktopDiagnostics.UpdaterCategory(redirectHead)=="updater-redirect-valid","real updater response shape");
            string manifest="{\"full\":{\"host_version\":[1,0,9260]},\"modules\":{\"discord_voice\":{\"full\":{\"module_version\":1}}},\"metadata_version\":2}";
            Check(DiscordDesktopDiagnostics.IsManifestPayload(Utf8(manifest)),"public manifest structure");
            Check(!DiscordDesktopDiagnostics.IsManifestPayload(null),"null manifest");
            Check(!DiscordDesktopDiagnostics.IsManifestPayload(new byte[32769]),"manifest maximum bound");
            foreach(string invalid in new[]{"", "null", "[]", "{}", "{\"full\":{},\"modules\":{\"a\":{}}}",
                "{\"full\":{\"host_version\":[1,0]},\"modules\":{\"a\":{}}}", "{\"full\":{\"host_version\":[1,0,-1]},\"modules\":{\"a\":{}}}",
                "{\"full\":{\"host_version\":[1,0,\"9260\"]},\"modules\":{\"a\":{}}}", "{\"full\":{\"host_version\":[1,0,9260]},\"modules\":{}}",
                "{\"full\":{\"host_version\":[1,0,9260]},\"modules\":[{}]}", "{\"full\":{\"host_version\":[1,0,9260]},\"modules\":{\"a\":123}}", manifest+"garbage"})
                Check(!DiscordDesktopDiagnostics.IsManifestPayload(Utf8(invalid)),"invalid manifest shape");
            Check(!DiscordDesktopDiagnostics.IsManifestPayload(new byte[]{123,34,0xff,34,58,49,125}),"manifest strict UTF8");
            string largeManifest=manifest.Substring(0,manifest.Length-1)+",\"fixture_padding\":\""+new string('a',10000)+"\"}";
            Check(Utf8(largeManifest).Length>4096 && DiscordDesktopDiagnostics.IsManifestPayload(Utf8(largeManifest)),"manifest-specific larger cap");
            using(var input=new MemoryStream(Utf8(largeManifest)))
                Check(DiscordDesktopDiagnostics.ReadBodyAsync(input,new DiscordDesktopDiagnostics.HttpHead{Status=200,Length=Utf8(largeManifest).Length},32768,CancellationToken.None).GetAwaiter().GetResult().Length==Utf8(largeManifest).Length,"manifest read is bounded to 32KiB");
            RejectBody("",new DiscordDesktopDiagnostics.HttpHead{Status=200,Length=Utf8(largeManifest).Length},"http-body-limit");
            var head=DiscordDesktopDiagnostics.ParseHeaders(Ascii("HTTP/1.1 200 OK\r\nContent-Length: "+Utf8(valid).Length+"\r\n\r\n"));
            Check(head.Status==200 && head.Length==Utf8(valid).Length && !head.Chunked,"content length parsed");
            using(var input=new MemoryStream(Utf8(valid)))
                Check(Encoding.UTF8.GetString(DiscordDesktopDiagnostics.ReadBodyAsync(input,head,CancellationToken.None).GetAwaiter().GetResult())==valid,"length framed REST body");
            var chunked=DiscordDesktopDiagnostics.ParseHeaders(Ascii("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n"));
            using(var input=new MemoryStream(Ascii("3\r\nabc\r\n2\r\nde\r\n0\r\nX-Test: value\r\n\r\n")))
                Check(Encoding.UTF8.GetString(DiscordDesktopDiagnostics.ReadBodyAsync(input,chunked,CancellationToken.None).GetAwaiter().GetResult())=="abcde","strict chunked decode");
            using(var input=new MemoryStream(Ascii("0\r\n\r\n")))
                Check(DiscordDesktopDiagnostics.ReadBodyAsync(input,chunked,CancellationToken.None).GetAwaiter().GetResult().Length==0,"empty chunked body");
            foreach(string invalid in new[]{"HTTP/2 200 OK\r\n\r\n", "HTTP/1.1 200\nOK\r\n\r\n", "HTTP/1.1 2000\r\n\r\n",
                "HTTP/1.1 100 Continue\r\n\r\n", "HTTP/1.1 200 OK\r\nContent-Length: 1\r\nContent-Length: 1\r\n\r\n",
                "HTTP/1.1 200 OK\r\nContent-Length: 1\r\nTransfer-Encoding: chunked\r\n\r\n",
                "HTTP/1.1 200 OK\r\nContent-Length: -1\r\n\r\n", "HTTP/1.1 200 OK\r\nContent-Length: +1\r\n\r\n",
                "HTTP/1.1 200 OK\r\nTransfer-Encoding: gzip, chunked\r\n\r\n", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nTransfer-Encoding: chunked\r\n\r\n",
                "HTTP/1.1 200 OK\r\n Folded: value\r\n\r\n", "HTTP/1.1 200 OK\r\nBad Name: value\r\n\r\n",
                "HTTP/1.1 200 OK\r\nHeader: value\nprivate\r\n\r\n", "HTTP/1.1 200 OK\r\n\r\nEXTRA"})
                Reject(delegate{DiscordDesktopDiagnostics.ParseHeaders(Ascii(invalid));},"headers strict");
            Reject(delegate{DiscordDesktopDiagnostics.ParseHeaders(null);},"null headers");
            Reject(delegate{DiscordDesktopDiagnostics.ParseHeaders(new byte[16385]);},"header bound");
            Reject(delegate{DiscordDesktopDiagnostics.ParseHeaders(Ascii("HTTP/1.1 302 Found\r\nLocation: "+redirect+"\r\nLocation: "+redirect+"\r\n\r\n"));},"duplicate Location rejected");
            Reject(delegate{DiscordDesktopDiagnostics.ParseHeaders(Ascii("HTTP/1.1 302 Found\r\nLocation: "+new string('a',2049)+"\r\n\r\n"));},"Location bounded");
            using(var input=new MemoryStream())
            {
                try { DiscordDesktopDiagnostics.ReadBodyAsync(input,new DiscordDesktopDiagnostics.HttpHead{Length=32769},32768,CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("manifest cap ignored"); }
                catch(DiscordDesktopDiagnostics.ProbeFailure error) { Check(error.Category=="http-body-limit","manifest content length bound"); }
            }
            using(var input=new MemoryStream(Utf8(valid)))
            {
                try { DiscordDesktopDiagnostics.ReadBodyAsync(input,new DiscordDesktopDiagnostics.HttpHead{Length=Utf8(valid).Length},new CancellationToken(true)).GetAwaiter().GetResult(); throw new Exception("body precancel ignored"); }
                catch(OperationCanceledException) { assertions++; }
            }
            RejectBody("abc",new DiscordDesktopDiagnostics.HttpHead{Status=200,Length=4},"http-incomplete-body");
            RejectBody("",new DiscordDesktopDiagnostics.HttpHead{Status=200,Length=4097},"http-body-limit");
            RejectBody("",new DiscordDesktopDiagnostics.HttpHead{Status=200},"http-unbounded-body");
            RejectBody("",new DiscordDesktopDiagnostics.HttpHead{Status=200,Length=1,Encoding="gzip"},"http-body-encoding");
            RejectBody("",new DiscordDesktopDiagnostics.HttpHead{Status=200,Length=1,Chunked=true},"http-invalid-framing");
            RejectBody("1001\r\n",chunked,"http-body-limit");
            RejectBody("-1\r\n",chunked,"http-invalid-chunk");
            RejectBody("FFFFFFFF\r\n",chunked,"http-invalid-chunk");
            RejectBody("1\r\nxXX",chunked,"http-invalid-chunk");
            RejectBody("1\r\n",chunked,"http-incomplete-body");
            RejectBody("0\r\nContent-Length: 0\r\n\r\n",chunked,"http-invalid-framing");
            using(var input=new HeaderOnlyStream("HTTP/1.1 302 Found\r\nLocation: https://private.test/installer.exe\r\nContent-Length: 123456789\r\n\r\n"))
            {
                byte[] header=DiscordDesktopDiagnostics.ReadHeadersAsync(input,CancellationToken.None).GetAwaiter().GetResult();
                Check(DiscordDesktopDiagnostics.ParseHeaders(header).Status==302,"updater headers only");
                Check(input.ReadCount==header.Length,"no installer body access or redirect follow");
            }
            foreach(string address in new[]{"0.0.0.0","10.0.0.1","127.0.0.1","169.254.169.254","172.16.1.1","192.168.1.1","100.64.1.1","192.0.2.1","198.51.100.2","203.0.113.9","224.0.0.1","::1","::","fe80::1","fc00::1","2001:db8::1","2001::1","::ffff:8.8.8.8"})
                Check(!DiscordDesktopDiagnostics.IsPublicAddress(IPAddress.Parse(address)),"nonpublic DNS destination refused");
            Check(DiscordDesktopDiagnostics.IsPublicAddress(IPAddress.Parse("1.1.1.1")),"public v4");
            Check(DiscordDesktopDiagnostics.IsPublicAddress(IPAddress.Parse("2606:4700:4700::1111")),"public v6");
            Check(!DiscordDesktopDiagnostics.IsPublicAddress(null),"null address");
            Check(DiscordDesktopDiagnostics.Classify(new Exception("secret-token 192.168.2.2"),"http")=="http-unavailable","raw exception not exported");
            Check(DiscordDesktopDiagnostics.Classify(new AuthenticationException("secret-token"),"tls")=="certificate-or-tls","TLS sanitized");
            Check(DiscordDesktopDiagnostics.Classify(new SocketException((int)SocketError.ConnectionRefused),"tcp")=="tcp-refused","socket category");
            Check(DiscordDesktopDiagnostics.Classify(new TimeoutException("private.test"),"dns")=="dns-timeout","DNS timeout sanitized");
            var endpointType=typeof(DiscordDesktopDiagnostics).GetNestedType("Endpoint",BindingFlags.NonPublic);
            object endpoint=Activator.CreateInstance(endpointType,true);
            endpointType.GetField("Key",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(endpoint,"api");
            endpointType.GetField("Name",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(endpoint,"Fixed fixture");
            var diagnostic=(CheckRow)typeof(DiscordDesktopDiagnostics).GetMethod("Row",BindingFlags.NonPublic|BindingFlags.Static).
                Invoke(null,new object[]{endpoint,"v4","tls",(bool?)true,"ok",22L});
            Check(diagnostic.DiagnosticCode=="ok" && diagnostic.Key=="discord.desktop.api.v4.tls","structured category emitted separately");
            var progress=new CapturingProgress(); var projectedRows=new List<CheckRow>();
            typeof(DiscordDesktopDiagnostics).GetMethod("Add",BindingFlags.NonPublic|BindingFlags.Static).
                Invoke(null,new object[]{projectedRows,diagnostic,progress});
            Check(progress.Last!=diagnostic && progress.Last.DiagnosticCode=="ok","progress copy retains structured category");
            Console.WriteLine("DiscordDesktopDiagnostics offline assertions passed: "+assertions); return 0;
        }
        catch(Exception error) { Console.Error.WriteLine("Fixture failure: "+error.Message); return 1; }
    }
    static void RejectBody(string input,DiscordDesktopDiagnostics.HttpHead head,string category)
    {
        using(var stream=new MemoryStream(Ascii(input)))
        {
            try { DiscordDesktopDiagnostics.ReadBodyAsync(stream,head,CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Accepted invalid body"); }
            catch(DiscordDesktopDiagnostics.ProbeFailure error) { Check(error.Category==category,"body category: "+category); }
        }
    }
    static void Reject(Action action,string label)
    { try { action(); throw new Exception("Accepted invalid fixture: "+label); } catch(DiscordDesktopDiagnostics.ProbeFailure) { assertions++; } }
    static void Check(bool result,string label) { if(!result) throw new Exception(label); assertions++; }
    static byte[] Ascii(string text) { return Encoding.ASCII.GetBytes(text); }
    static byte[] Utf8(string text) { return Encoding.UTF8.GetBytes(text); }
    sealed class HeaderOnlyStream : MemoryStream
    {
        internal int ReadCount;
        internal HeaderOnlyStream(string header) : base(Ascii(header)) {}
        public override int Read(byte[] buffer,int offset,int count)
        {
            if(Position>=Length) throw new Exception("Installer body must not be read");
            int read=base.Read(buffer,offset,count); ReadCount+=read; return read;
        }
    }
    sealed class CapturingProgress:IProgress<CheckRow>
    {
        internal CheckRow Last;
        public void Report(CheckRow row){Last=row;}
    }
}
