using System;
using System.Collections.Generic;

namespace LocalLink
{
    // Offline tests only: never starts the engine, loads a driver or opens a socket.
    public static class ProfileTests
    {
        private static int assertions;

        public static int Run()
        {
            assertions = 0;
            string[] normalized = Profiles.ValidateDomains(" Example.ORG.\r\nexample.org\nпример.рф\n\n");
            Check(normalized.Length == 2, "duplicate domains are removed");
            Check(normalized[0] == "example.org", "domain case and final dot normalize");
            Check(normalized[1] == "xn--e1afmkfd.xn--p1ai", "IDN normalizes to ASCII");
            foreach (string bad in new string[] { null, "", " \r\n", "com", "localhost", "https://example.org",
                "example.org:443", "*.example.org", "^example.org", "#example.org", "@domains.txt",
                "example.org,other.org", "example.org --new", "example.org\"", "example.org&calc",
                "example.org/anything", "example.org\\anything", "bad..example.org", ".example.org",
                "-bad.example.org", "bad-.example.org", "under_score.example.org", "192.0.2.1", "127.1",
                "example.org\t", "exa\0mple.org", "ex\u200bample.org", new String('a', 64) + ".org",
                new String('a', 8193) })
                Reject(delegate { Profiles.ValidateDomains(bad); }, "domain rejected: " + Printable(bad));
            List<string> tooMany = new List<string>();
            for (int i = 0; i < 129; i++) tooMany.Add("host" + i + ".example.org");
            Reject(delegate { Profiles.ValidateDomains(String.Join("\n", tooMany.ToArray())); }, "domain count bounded");

            // Exercise defaults without a service catalogue or explicit strategy.
            ProfileSpec direct = new ProfileSpec { Domains = "example.org" };
            Check(direct.Mode == HelperMode.Direct && direct.Strategy == "split" &&
                direct.Port == 443 && !direct.Quic && !direct.DiscordVoice && !direct.TelegramNative &&
                direct.HostListFile == "", "defaults remain gentle HTTP/TLS-only");
            List<string> args = Profiles.Build(direct);
            Contains(args, "--lua-init=@lua/zapret-lib.lua");
            Contains(args, "--lua-init=@lua/zapret-antidpi.lua");
            Contains(args, "--wf-tcp-out=80,443");
            Contains(args, "--filter-tcp=443");
            Contains(args, "--filter-l7=tls");
            Contains(args, "--payload=tls_client_hello");
            Contains(args, "--hostlist-domains=example.org");
            Contains(args, "--lua-desync=multisplit:pos=2");
            Contains(args, "--filter-tcp=80");
            Contains(args, "--filter-l7=http");
            Contains(args, "--payload=http_req");
            Check(CountPrefix(args, "--new") == 1 && CountPrefix(args, "--wf-udp") == 0, "direct default is HTTP/TLS without UDP");
            Check(CountPrefix(args, "--lua-desync=") == 2, "one gentle split per HTTP/TLS profile");
            direct.Strategy = "disorder";
            Contains(Profiles.Build(direct), "--lua-desync=multidisorder:pos=1,midsld");
            direct.Strategy = "fake-split";
            args = Profiles.Build(direct);
            CheckFakeSplit(args);
            Check(CountPrefix(args, "--lua-desync=") == 3 && CountPrefix(args, "--new") == 1, "TLS fallback plus real HTTP split");
            Check(CountPrefix(args, "--wf-udp") == 0 && CountPrefix(args, "--filter-udp") == 0,
                "TLS fallback does not enable voice or other UDP");
            Contains(args, "--hostlist-domains=example.org");
            direct.Quic = true;
            args = Profiles.Build(direct);
            Check(CountPrefix(args, "--new") == 2, "QUIC adds exactly one profile");
            Check(CountPrefix(args, "--hostlist-domains=example.org") == 3, "HTTP and QUIC keep the same allowlist");
            Contains(args, "--wf-udp-out=443");
            Contains(args, "--filter-udp=443");
            Contains(args, "--filter-l7=quic");
            Contains(args, "--payload=quic_initial");
            Contains(args, "--lua-desync=fake:blob=fake_default_quic:repeats=11");
            CheckFakeSplit(args);
            Check(CountPrefix(args, "--lua-desync=") == 4, "QUIC adds only its explicit Initial fake");
            Check(CountPrefix(args, "--wf-raw") == 0, "Direct has no hidden raw UDP or STUN filter");
            foreach (string method in new string[] { "split", "disorder", "fake-split" })
            {
                direct.Strategy = method;
                foreach (bool quic in new bool[] { false, true })
                {
                    direct.Quic = quic;
                    args = Profiles.Build(direct);
                    Check(CountPrefix(args, "--filter-tcp=") == 2 && args.Contains("--filter-tcp=443") && args.Contains("--filter-tcp=80"),
                        "Direct TCP scope stays HTTP80/TLS443: " + method);
                    Check(CountPrefix(args, "--filter-udp=") == (quic ? 1 : 0) &&
                        (!quic || args.Contains("--filter-udp=443")), "UDP is opt-in QUIC/443 only: " + method);
                    Check(CountPrefix(args, "--hostlist-domains=") == (quic ? 3 : 2),
                        "each Direct profile retains the allowlist: " + method);
                    Check(CountPrefix(args, "--new") == (quic ? 2 : 1), "no unscoped fallback profile: " + method);
                    CheckHttpProfile(args, method, null);
                }
            }
            direct.Strategy = "split:pos=2 --new";
            Reject(delegate { Profiles.Build(direct); }, "strategy injection rejected");
            foreach (string bad in new string[] { null, "", "fake", "Fake-Split", "fake-split:tcp_ts=-1000", "fake-split --new" })
            {
                direct.Strategy = bad;
                Reject(delegate { Profiles.Build(direct); }, "unknown TLS strategy rejected: " + Printable(bad));
            }
            direct.Strategy = "wireguard";
            Reject(delegate { Profiles.Build(direct); }, "wrong direct strategy rejected");
            direct.Strategy = "split";
            direct.Domains = "";
            Reject(delegate { Profiles.Build(direct); }, "empty direct allowlist cannot broaden scope");
            Reject(delegate { Profiles.Build(null); }, "null spec rejected");
            Reject(delegate { Profiles.Build(new ProfileSpec { Mode = (HelperMode)999 }); }, "unknown mode rejected");

            ProfileSpec native = new ProfileSpec { Domains = "example.org", DiscordVoice = true, TelegramNative = true, Quic = true,
                HostListFile = @"C:\LocalLink\lists\public-domains.txt" };
            args = Profiles.Build(native);
            Contains(args, "--wf-tcp-out=80,443,5222,2053,2083,2087,2096,8443");
            Contains(args, "--filter-l7=mtproto,unknown");
            Contains(args, "--payload=mtproto_initial,unknown");
            Contains(args, "--out-range=-d1");
            Contains(args, "--ipset-ip=" + Profiles.TelegramNetworks);
            Contains(args, "--lua-desync=multisplit:pos=2:payload=mtproto_initial,unknown");
            Contains(args, "--filter-l7=discord,stun");
            Contains(args, "--payload=discord_ip_discovery,stun");
            Contains(args, "--filter-udp=50000-50099,19294-19344");
            Check(CountPrefix(args, "--new") == 9, "HTTP, QUIC, five voice TLS, voice UDP and native TCP are separate profiles");
            Check(CountPrefix(args, "--hostlist=") == 3 && CountPrefix(args, "--hostlist-domains=") == 8,
                "public hostlist remains only in HTTP, TLS443 and QUIC; voice TLS has fixed domains");
            CheckHttpProfile(args, native.Strategy, native.HostListFile);
            CheckDiscordVoiceProfiles(args, native.Strategy);
            string discovery = args.Find(delegate(string arg) { return arg.StartsWith("--wf-raw-part=", StringComparison.Ordinal); });
            Check(discovery.Contains("outbound and ip and") && discovery.Contains("udp.PayloadLength=74") &&
                discovery.Contains("udp.Payload32[0]=0x00010046") && discovery.Contains("udp.Payload32[17]=0"), "discovery capture exact IPv4 signature");
            for (int i = 2; i <= 17; i++) Check(discovery.Contains("udp.Payload32[" + i + "]=0"), "discovery zero tail " + i);
            foreach (string arg in args) Check(arg != "--filter-l7=all" && arg != "--payload=all" &&
                arg != "--filter-udp=*", "native modes never blanket all protocols or UDP ports");
            native.Domains = "";
            args = Profiles.Build(native);
            Check(CountPrefix(args, "--hostlist-domains=") == 5 && CountPrefix(args, "--hostlist=") == 3,
                "file-only main allowlist supported; voice TLS still has fixed domains");
            CheckDiscordVoiceProfiles(args, native.Strategy);
            foreach (string method in new[] { "split", "disorder", "fake-split" })
            {
                var voice = new ProfileSpec { Domains = "example.org", Strategy = method, DiscordVoice = true };
                args = Profiles.Build(voice);
                Contains(args, "--wf-tcp-out=80,443,2053,2083,2087,2096,8443");
                Check(CountPrefix(args, "--new") == 7 && CountPrefix(args, "--wf-udp") == 0,
                    "voice adds five scoped TLS profiles and signature-only UDP: " + method);
                CheckDiscordVoiceProfiles(args, method);
                voice.DiscordVoice = false;
                args = Profiles.Build(voice);
                Contains(args, "--wf-tcp-out=80,443");
                Check(CountPrefix(args, "--filter-tcp=") == 2 && CountPrefix(args, "--filter-udp=") == 0 &&
                    CountPrefix(args, "--wf-raw") == 0 && !args.Contains("--hostlist-domains=discord.media"),
                    "turning voice off removes all extra ports and UDP capture: " + method);
            }
            foreach (string invalidPath in new[] { "public.txt", @"\\server\share\public.txt", @"C:public.txt", @"C:\..\public.txt",
                @"C:\folder.\public.txt", @"C:\folder \public.txt", @"C:\folder\public.txt:stream", @"C:\folder\--new\x\..\y.txt",
                "C:\\folder\\a\n.txt", "C:\\folder\\\"a.txt", @"C:\\public.txt", @"C:/public.txt" })
            {
                native.HostListFile = invalidPath;
                Reject(delegate { Profiles.Build(native); }, "hostlist path rejected");
            }
            Check(Profiles.ValidateHostListFile(@"C:\Local Link\списки\domains.txt") == @"C:\Local Link\списки\domains.txt", "spaces and Unicode local paths accepted");

            ProfileSpec vpn = new ProfileSpec { Mode = HelperMode.VpnTls, Endpoint = "198.51.100.7", Port = 8443, Strategy = "split" };
            args = Profiles.Build(vpn);
            Contains(args, "--wf-tcp-out=8443");
            Contains(args, "--wf-raw-filter=ip.DstAddr=198.51.100.7");
            Contains(args, "--filter-tcp=8443");
            Contains(args, "--ipset-ip=198.51.100.7");
            Contains(args, "--filter-l7=tls");
            Contains(args, "--payload=tls_client_hello");
            Check(CountPrefix(args, "--hostlist") == 0 && CountPrefix(args, "--wf-udp") == 0, "TLS VPN targets endpoint without UDP");
            vpn.Strategy = "fake-split";
            args = Profiles.Build(vpn);
            CheckFakeSplit(args);
            Contains(args, "--wf-raw-filter=ip.DstAddr=198.51.100.7");
            Contains(args, "--ipset-ip=198.51.100.7");
            Contains(args, "--filter-tcp=8443");
            Check(CountPrefix(args, "--wf-udp") == 0 && !args.Contains("--new"), "VPN fallback cannot broaden endpoint scope");
            vpn.Endpoint = "2001:db8::7";
            args = Profiles.Build(vpn);
            Contains(args, "--wf-raw-filter=ipv6.DstAddr=2001:db8::7");
            CheckFakeSplit(args);
            vpn.Endpoint = "10.1.2.3";
            Contains(Profiles.Build(vpn), "--wf-filter-lan=0");
            vpn.Endpoint = "fd00::7";
            Contains(Profiles.Build(vpn), "--ipset-ip=fd00::7");
            foreach (string bad in new string[] { null, "", "vpn.example.org", "https://198.51.100.7", "198.51.100.7:8443",
                "198.51.100.7 or true", "198.51.100.7\n--new", " 198.51.100.7", "198.51.100.7 ", "[2001:db8::7]",
                "127.0.0.1", "127.1", "2130706433", "0x7f000001", "010.0.0.1", "0.0.0.0", "0.1.2.3", "224.0.0.1",
                "255.255.255.255", "169.254.1.1", "::", "::1", "ff02::1", "fe80::1", "fe80::1%1", "::ffff:127.0.0.1",
                "::ffff:198.51.100.7", "::127.0.0.1", "1.2.3.256" })
            {
                vpn.Endpoint = bad;
                Reject(delegate { Profiles.Build(vpn); }, "endpoint rejected: " + Printable(bad));
            }
            vpn.Endpoint = "198.51.100.7";
            vpn.Port = 0;
            Reject(delegate { Profiles.Build(vpn); }, "port zero rejected");
            vpn.Port = 65536;
            Reject(delegate { Profiles.Build(vpn); }, "oversized port rejected");
            vpn.Port = 65535;
            Contains(Profiles.Build(vpn), "--filter-tcp=65535");
            vpn.Port = 1;
            Contains(Profiles.Build(vpn), "--filter-tcp=1");
            vpn.Quic = true;
            Reject(delegate { Profiles.Build(vpn); }, "VPN QUIC rejected");
            vpn.Quic = false;
            vpn.DiscordVoice = true;
            Reject(delegate { Profiles.Build(vpn); }, "VPN rejects discovery capture");
            vpn.DiscordVoice = false;
            vpn.TelegramNative = true;
            Reject(delegate { Profiles.Build(vpn); }, "VPN rejects native Telegram capture");
            vpn.TelegramNative = false;
            vpn.HostListFile = @"C:\LocalLink\list.txt";
            Reject(delegate { Profiles.Build(vpn); }, "VPN rejects public-domain list");
            vpn.HostListFile = "";

            vpn.Mode = HelperMode.VpnWireGuard;
            vpn.Strategy = "wireguard";
            vpn.Port = 51820;
            args = Profiles.Build(vpn);
            Contains(args, "--wf-raw-filter=ip.DstAddr=198.51.100.7");
            Contains(args, "--wf-raw-part=outbound and udp.DstPort=51820 and udp.PayloadLength=148 and udp.Payload32[0]=0x01000000");
            Contains(args, "--filter-udp=51820");
            Contains(args, "--ipset-ip=198.51.100.7");
            Contains(args, "--filter-l7=wireguard");
            Contains(args, "--payload=wireguard_initiation");
            Contains(args, "--lua-desync=fake:blob=0x00000000000000000000000000000000:repeats=2");
            Check(CountPrefix(args, "--lua-desync=") == 1 && !args.Contains("--new"), "WireGuard has one handshake fake only");
            Check(CountPrefix(args, "--wf-tcp") == 0 && CountPrefix(args, "--filter-tcp") == 0, "WireGuard never captures TCP");
            vpn.Strategy = "split";
            Reject(delegate { Profiles.Build(vpn); }, "WireGuard cannot accept a TLS strategy");
            vpn.Strategy = "fake-split";
            Reject(delegate { Profiles.Build(vpn); }, "WireGuard cannot accept the TLS fake fallback");
            Console.WriteLine("Profile tests passed: " + assertions + " assertions (offline; no engine or network changes).");
            return 0;
        }

        private static void Contains(List<string> args, string value) { Check(args.Contains(value), value); }
        private static void CheckDiscordVoiceProfiles(List<string> args, string method)
        {
            const string stun = "--wf-raw-part=outbound and udp and (udp.DstPort>=50000 and udp.DstPort<=50099 or udp.DstPort>=19294 and udp.DstPort<=19344) and udp.PayloadLength>=20 and udp.Payload32[1]=0x2112A442 and udp.Payload[0]<0x40";
            Contains(args, stun);
            Check(CountPrefix(args, "--wf-raw-part=") == 2, "voice captures only discovery and bounded STUN signatures");
            foreach (int port in new[] { 2053, 2083, 2087, 2096, 8443 })
            {
                List<string> profile = ProfileAt(args, "--filter-tcp=" + port);
                Contains(profile, "--filter-l7=tls");
                Contains(profile, "--hostlist-domains=discord.media");
                Contains(profile, "--payload=tls_client_hello");
                Contains(profile, "--in-range=x");
                Contains(profile, "--out-range=-d10");
                Check(CountPrefix(profile, "--hostlist-domains=") == 1 && CountPrefix(profile, "--hostlist=") == 0 &&
                    CountPrefix(profile, "--ipset") == 0, "voice TLS cannot inherit public/custom hosts or broad IP ranges");
                Contains(profile, method == "disorder" ? "--lua-desync=multidisorder:pos=1,midsld" : "--lua-desync=multisplit:pos=2");
                Check(CountPrefix(profile, "--lua-desync=") == (method == "fake-split" ? 2 : 1),
                    "voice TLS retains only the selected TLS strategy");
                if (method == "fake-split") Contains(profile, "--lua-desync=fake:blob=fake_default_tls:tcp_md5:repeats=2");
            }
            List<string> udp = ProfileAt(args, "--filter-udp=50000-50099,19294-19344");
            Contains(udp, "--filter-l7=discord,stun");
            Contains(udp, "--payload=discord_ip_discovery,stun");
            Contains(udp, "--in-range=x");
            Contains(udp, "--lua-desync=fake:blob=0x00000000000000000000000000000000:repeats=2");
            Check(udp.Count == 5, "voice UDP has only its exact port/protocol/payload scope and two-fake action");
        }
        private static List<string> ProfileAt(List<string> args, string selector)
        {
            int index = args.IndexOf(selector);
            Check(index >= 0, "profile exists: " + selector);
            List<string> profile = new List<string>();
            for (int i = index; i < args.Count && args[i] != "--new"; i++) profile.Add(args[i]);
            return profile;
        }
        private static void CheckHttpProfile(List<string> args, string method, string hostFile)
        {
            int index = args.IndexOf("--filter-tcp=80");
            Check(index >= 0, "HTTP80 profile exists");
            List<string> profile = new List<string>();
            for (int i = index; i < args.Count && args[i] != "--new"; i++) profile.Add(args[i]);
            Contains(profile, "--filter-l7=http");
            Contains(profile, "--payload=http_req");
            Contains(profile, "--in-range=x");
            Contains(profile, "--out-range=-d10");
            Contains(profile, "--hostlist-domains=example.org");
            if (hostFile != null) Contains(profile, "--hostlist=" + hostFile);
            Contains(profile, method == "disorder" ? "--lua-desync=multidisorder:pos=2" : "--lua-desync=multisplit:pos=2");
            Check(CountPrefix(profile, "--lua-desync=") == 1, "HTTP never gains TLS fake actions");
            foreach (string arg in profile)
                Check(!arg.Contains("fake_default_tls") && !arg.Contains("tls_client_hello") && !arg.Contains("midsld"),
                    "HTTP action has no TLS-only payload or position");
        }
        private static void CheckFakeSplit(List<string> args)
        {
            const string fake = "--lua-desync=fake:blob=fake_default_tls:tcp_md5:repeats=2";
            const string split = "--lua-desync=multisplit:pos=2";
            Contains(args, "--filter-l7=tls");
            Contains(args, "--payload=tls_client_hello");
            Contains(args, "--in-range=x");
            Contains(args, "--out-range=-d10");
            Contains(args, fake);
            Contains(args, split);
            Check(args.IndexOf(split) == args.IndexOf(fake) + 1, "MD5 fake immediately precedes real split");
            Check(CountPrefix(args, "--lua-init=") == 2, "fallback needs no additional Lua initializer or payload file");
            foreach (string arg in args)
                Check(arg.IndexOf("tcp_ts", StringComparison.Ordinal) < 0 &&
                    arg.IndexOf("tcp_seq", StringComparison.Ordinal) < 0 &&
                    arg.IndexOf("tcp_ack", StringComparison.Ordinal) < 0,
                    "fallback does not require timestamp/sequence/ack fooling: " + arg);
        }
        private static int CountPrefix(List<string> args, string prefix)
        {
            int count = 0;
            foreach (string arg in args) if (arg.StartsWith(prefix, StringComparison.Ordinal)) count++;
            return count;
        }
        private static void Check(bool condition, string name)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException("Profile test failed: " + name);
        }
        private static void Reject(Action action, string name)
        {
            assertions++;
            try { action(); }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException("Profile test did not reject: " + name);
        }
        private static string Printable(string value)
        {
            if (value == null) return "<null>";
            if (value.Length > 80) return "<long input>";
            return value.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\0", "\\0");
        }
    }
}
