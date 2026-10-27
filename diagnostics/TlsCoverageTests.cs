using System;
using System.Collections.Generic;

namespace LocalLink
{
    // Offline WRAPPER-scope fixtures only. Build() generates strings; these
    // fixtures do not invoke winws2, WinDivert, DNS, sockets, file reads or writes.
    // Suffix matching below models the documented domain/subdomain include rule;
    // it is not an attestation of engine profile selection, packet reassembly,
    // an external hostlist's contents, actual traffic capture or a working bypass.
    public static class TlsCoverageTests
    {
        private static int assertions;
        private static int directCases;
        private static int vpnCases;
        private static readonly string[] ExpectedStrategies =
            { "split", "sni-split", "disorder", "fake-split", "fake-disorder" };
        private static readonly string[] ProblemHosts =
            { "discord.com", "gateway.discord.gg", "youtube.com", "www.youtube.com", "i.ytimg.com",
                "discord-attachments-uploads-prd.storage.googleapis.com" };
        private static readonly string[] OutsideInlineScope =
        {
            "www.microsoft.com", "www.cloudflare.com", "store.steampowered.com",
            "steamcommunity.com", "reforger.armaplatform.com", "www.escapefromtarkov.com",
            "discord.com.evil.example", "gateway.discord.gg.evil.example", "notdiscord.com",
            "gatewaydiscord.gg", "discord.media.evil.example", "youtube.com.evil.example",
            "notyoutube.com", "ytimg.com.evil.example", "cdn-ytimg.com", "evil.example",
            "googleapis.com", "storage.googleapis.com", "other-bucket.storage.googleapis.com",
            "notdiscord-attachments-uploads-prd.storage.googleapis.com"
        };
        private const string ExternalList = @"C:\LocalLink\fixtures\not-opened-public-domains.lst";
        private const string VoiceUdpSelector = "--filter-udp=50000-50099,19294-19344";

        private static int Main(string[] args)
        {
            try { return Run(); }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.GetType().Name + ": " + error.Message);
                return 1;
            }
        }

        public static int Run()
        {
            assertions = directCases = vpnCases = 0;
            List<string> strategies = new List<string>(Profiles.TlsStrategies());
            Check(strategies.Count == ExpectedStrategies.Length, "five canonical TLS strategies");
            for (int i = 0; i < ExpectedStrategies.Length; i++)
            {
                Check(strategies[i] == ExpectedStrategies[i], "canonical strategy order " + i);
                Check(Profiles.IsTlsStrategy(strategies[i]), "canonical strategy accepted " + strategies[i]);
            }
            foreach (string invalid in new[] { null, "", "wireguard", "SNI-SPLIT", "fake-disorder --new", "sni-split:pos=all" })
                Check(!Profiles.IsTlsStrategy(invalid), "noncanonical strategy rejected");

            string domains = ServiceCatalog.DomainsFor(new[] { "discord", "youtube" }, "");
            Check(Profiles.ValidateDomains(domains).Length == 12, "catalogue includes twelve Discord/YouTube and upload suffixes");
            foreach (string strategy in strategies)
            {
                foreach (bool quic in new[] { false, true })
                foreach (bool voice in new[] { false, true })
                foreach (bool externalList in new[] { false, true })
                    CheckDirect(domains, strategy, quic, voice, externalList);

                foreach (string endpoint in new[] { "198.51.100.7", "2001:db8::7" })
                foreach (int port in new[] { 443, 8443 })
                    CheckVpn(strategy, endpoint, port);

                Reject(delegate { Profiles.Build(new ProfileSpec { Mode = HelperMode.VpnWireGuard,
                    Endpoint = "198.51.100.7", Strategy = strategy, Port = 51820 }); },
                    "TLS strategy cannot broaden WireGuard scope: " + strategy);
            }
            Console.WriteLine("TLS coverage tests passed: " + assertions + " assertions, " + directCases +
                " Direct fixtures, " + vpnCases + " VPN fixtures (offline wrapper scope; not engine packet attestation).");
            return 0;
        }

        private static void CheckDirect(string domains, string strategy, bool quic, bool voice, bool externalList)
        {
            directCases++;
            string label = strategy + "/QUIC=" + quic + "/voice=" + voice + "/list=" + externalList;
            List<string> args = Profiles.Build(new ProfileSpec { Mode = HelperMode.Direct, Domains = domains,
                Strategy = strategy, Quic = quic, DiscordVoice = voice,
                HostListFile = externalList ? ExternalList : "" });
            List<List<string>> profiles = SplitProfiles(args);
            int expectedProfiles = 2 + (quic ? 1 : 0) + (voice ? 6 : 0);
            Check(profiles.Count == expectedProfiles, "exact separated profile count: " + label);
            Check(Count(args, "--new") == expectedProfiles - 1, "one boundary between each profile: " + label);
            Check(CountPrefix(args, "--lua-init=") == 2, "only pinned Lua initializers: " + label);
            Check(CountPrefix(args, "--wf-tcp-out=") == 1, "one kernel TCP port scope: " + label);
            Has(args, voice ? "--wf-tcp-out=80,443,2053,2083,2087,2096,8443" : "--wf-tcp-out=80,443", label);
            Check(CountPrefix(args, "--wf-udp-out=") == (quic ? 1 : 0), "no broad UDP capture: " + label);
            if (quic) Has(args, "--wf-udp-out=443", label);
            Check(CountPrefix(args, "--wf-raw-part=") == (voice ? 2 : 0), "voice signatures are explicitly optional: " + label);
            Check(CountPrefix(args, "--wf-raw-filter=") == 0, "Direct never inherits VPN endpoint filter: " + label);
            Check(CountPrefix(args, "--filter-tcp=") == 2 + (voice ? 5 : 0), "exact TCP profile scope: " + label);
            Check(CountPrefix(args, "--filter-udp=") == (quic ? 1 : 0) + (voice ? 1 : 0), "exact UDP profile scope: " + label);
            foreach (List<string> profile in profiles)
            {
                Check(CountPrefix(profile, "--filter-tcp=") + CountPrefix(profile, "--filter-udp=") == 1,
                    "one transport selector per profile: " + label);
                Check(CountPrefix(profile, "--wf-") == 0 && CountPrefix(profile, "--lua-init=") == 0,
                    "global capture/initializers do not leak into a profile: " + label);
                foreach (string arg in profile)
                    Check(arg != "--filter-l7=all" && arg != "--payload=all" && arg != "--filter-udp=*",
                        "no unscoped all-protocol fallback: " + label);
            }

            List<string> tls = FindProfile(profiles, "--filter-tcp=443");
            CheckTls(tls, strategy, label);
            CheckMainHosts(tls, externalList, label);
            Check(CountPrefix(tls, "--ipset") == 0, "web TLS is host-scoped, not a broad IP profile: " + label);
            string[] inlineHosts = InlineHosts(tls);
            foreach (string host in ProblemHosts)
                Check(InlineSuffixMatches(host, inlineHosts), "problem host falls within inline TLS fixture: " + host + "/" + label);
            foreach (string host in OutsideInlineScope)
            {
                Check(!InlineSuffixMatches(host, inlineHosts), "lookalike/control is outside INLINE suffix scope: " + host + "/" + label);
                // With an external list, include lists form a union. Its contents
                // are intentionally unknown here, so overall exclusion of these
                // hosts is NOT asserted. Kernel TCP capture is broader as well.
                if (!externalList)
                    Check(CountPrefix(tls, "--hostlist=") == 0, "no unknown external include can widen this fixture: " + host);
            }

            List<string> http = FindProfile(profiles, "--filter-tcp=80");
            Has(http, "--filter-l7=http", label); Has(http, "--payload=http_req", label);
            Has(http, "--in-range=x", label); Has(http, "--out-range=-d10", label);
            CheckMainHosts(http, externalList, label);
            Check(CountPrefix(http, "--lua-desync=") == 1, "HTTP has one real segmentation action: " + label);
            foreach (string arg in http)
                Check(!arg.Contains("fake_default_tls") && !arg.Contains("tls_client_hello") && !arg.Contains("midsld"),
                    "HTTP never inherits a TLS fake, payload or marker: " + label);

            if (quic)
            {
                List<string> udp = FindProfile(profiles, "--filter-udp=443");
                Has(udp, "--filter-l7=quic", label); Has(udp, "--payload=quic_initial", label);
                CheckMainHosts(udp, externalList, label);
                Check(CountPrefix(udp, "--lua-desync=") == 1, "QUIC has its own Initial-only action: " + label);
                foreach (string arg in udp)
                    Check(!arg.Contains("tls_client_hello") && !arg.Contains("fake_default_tls"), "TLS method does not spill into QUIC: " + label);
            }
            if (voice)
            {
                foreach (int port in new[] { 2053, 2083, 2087, 2096, 8443 })
                {
                    List<string> extraTls = FindProfile(profiles, "--filter-tcp=" + port);
                    CheckTls(extraTls, strategy, label);
                    Has(extraTls, "--hostlist-domains=discord.media", label);
                    Check(CountPrefix(extraTls, "--hostlist-domains=") == 1 && CountPrefix(extraTls, "--hostlist=") == 0 &&
                        CountPrefix(extraTls, "--ipset") == 0, "voice TLS retains only its fixed host scope: " + label);
                }
                List<string> voiceUdp = FindProfile(profiles, VoiceUdpSelector);
                Has(voiceUdp, "--filter-l7=discord,stun", label);
                Has(voiceUdp, "--payload=discord_ip_discovery,stun", label);
                Check(CountPrefix(voiceUdp, "--hostlist") == 0 && CountPrefix(voiceUdp, "--lua-desync=") == 1,
                    "voice UDP neither inherits public lists nor a TLS method: " + label);
            }
        }

        private static void CheckVpn(string strategy, string endpoint, int port)
        {
            vpnCases++;
            string label = strategy + "/VPN/" + endpoint + "/" + port;
            List<string> args = Profiles.Build(new ProfileSpec { Mode = HelperMode.VpnTls,
                Endpoint = endpoint, Port = port, Strategy = strategy });
            List<List<string>> profiles = SplitProfiles(args);
            Check(profiles.Count == 1 && Count(args, "--new") == 0, "VPN remains one profile: " + label);
            string addressField = endpoint.Contains(":") ? "ipv6.DstAddr" : "ip.DstAddr";
            Has(args, "--wf-raw-filter=" + addressField + "=" + endpoint, label);
            Has(args, "--wf-filter-lan=0", label); Has(args, "--wf-tcp-out=" + port, label);
            Has(args, "--filter-tcp=" + port, label); Has(profiles[0], "--ipset-ip=" + endpoint, label);
            Check(CountPrefix(args, "--wf-raw-filter=") == 1 && CountPrefix(args, "--ipset") == 1,
                "new TLS strategy cannot broaden VPN endpoint scope: " + label);
            Check(CountPrefix(args, "--hostlist") == 0 && CountPrefix(args, "--wf-udp") == 0 &&
                CountPrefix(args, "--wf-raw-part=") == 0 && CountPrefix(args, "--filter-udp=") == 0,
                "VPN TLS inherits no domain-list/voice/QUIC capture: " + label);
            CheckTls(profiles[0], strategy, label);
        }

        private static void CheckTls(List<string> profile, string strategy, string label)
        {
            Has(profile, "--filter-l7=tls", label); Has(profile, "--payload=tls_client_hello", label);
            Has(profile, "--in-range=x", label); Has(profile, "--out-range=-d10", label);
            Check(CountPrefix(profile, "--filter-l7=") == 1 && CountPrefix(profile, "--payload=") == 1,
                "one strict TLS/clienthello selector: " + label);
            List<string> actions = PrefixValues(profile, "--lua-desync=");
            bool fake = strategy.StartsWith("fake-", StringComparison.Ordinal);
            Check(actions.Count == (fake ? 2 : 1), "exact TLS action count: " + label);
            if (fake)
                Check(actions[0] == "--lua-desync=fake:blob=fake_default_tls:tcp_md5:repeats=2", "bounded MD5 fake precedes real segmentation: " + label);
            string action = actions[actions.Count - 1];
            bool disorder = strategy == "disorder" || strategy == "fake-disorder";
            Check(action.StartsWith(disorder ? "--lua-desync=multidisorder:pos=" : "--lua-desync=multisplit:pos=", StringComparison.Ordinal),
                "real segmentation action follows strategy: " + label);
            if (strategy == "split" || strategy == "fake-split")
                Check(action == "--lua-desync=multisplit:pos=2", "existing gentle split stays unchanged: " + label);
            else
                Check(action.Contains("midsld"), "SNI/disorder candidates retain a hostname position marker: " + label);
            foreach (string arg in profile)
                Check(!arg.Contains("tcp_ts") && !arg.Contains("tcp_seq") && !arg.Contains("tcp_ack") &&
                    !arg.Contains("ip_ttl") && !arg.Contains("ip6_ttl") && !arg.Contains("badseq") && !arg.Contains("badack"),
                    "no hidden timestamp/sequence/TTL fooling: " + label);
        }

        private static void CheckMainHosts(List<string> profile, bool externalList, string label)
        {
            Check(CountPrefix(profile, "--hostlist-domains=") == 1, "exactly one inline include: " + label);
            Check(CountPrefix(profile, "--hostlist=") == (externalList ? 1 : 0), "explicit external-list union only: " + label);
            if (externalList) Has(profile, "--hostlist=" + ExternalList, label);
            Check(CountPrefix(profile, "--hostlist-exclude") == 0 && CountPrefix(profile, "--hostlist-auto") == 0,
                "no hidden excludes/auto-learning: " + label);
            Check(InlineHosts(profile).Length == 12, "main profile retains catalogue suffix count: " + label);
        }

        private static string[] InlineHosts(List<string> profile)
        {
            List<string> values = PrefixValues(profile, "--hostlist-domains=");
            Check(values.Count == 1, "fixture needs one inline hostlist");
            return values[0].Substring("--hostlist-domains=".Length).Split(',');
        }
        private static bool InlineSuffixMatches(string host, IEnumerable<string> suffixes)
        {
            foreach (string suffix in suffixes)
                if (String.Equals(host, suffix, StringComparison.OrdinalIgnoreCase) ||
                    host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        private static List<List<string>> SplitProfiles(List<string> args)
        {
            var result = new List<List<string>>();
            List<string> current = null;
            foreach (string arg in args)
            {
                if (arg == "--new")
                {
                    Check(current != null && current.Count > 0, "no empty profile boundary");
                    result.Add(current); current = null;
                }
                else
                {
                    if (current == null && (arg.StartsWith("--filter-tcp=", StringComparison.Ordinal) ||
                        arg.StartsWith("--filter-udp=", StringComparison.Ordinal))) current = new List<string>();
                    if (current != null) current.Add(arg);
                    else Check(arg.StartsWith("--lua-init=", StringComparison.Ordinal) || arg.StartsWith("--wf-", StringComparison.Ordinal),
                        "only known global options precede profiles");
                }
            }
            Check(current != null && current.Count > 0, "nonempty final profile");
            result.Add(current); return result;
        }
        private static List<string> FindProfile(List<List<string>> profiles, string selector)
        {
            List<string> found = null; int count = 0;
            foreach (List<string> profile in profiles)
                if (profile.Contains(selector)) { found = profile; count++; }
            Check(count == 1, "exactly one profile for " + selector); return found;
        }
        private static List<string> PrefixValues(List<string> args, string prefix)
        {
            var values = new List<string>();
            foreach (string arg in args) if (arg.StartsWith(prefix, StringComparison.Ordinal)) values.Add(arg);
            return values;
        }
        private static int CountPrefix(List<string> args, string prefix) { return PrefixValues(args, prefix).Count; }
        private static int Count(List<string> args, string value)
        { int count = 0; foreach (string arg in args) if (arg == value) count++; return count; }
        private static void Has(List<string> args, string value, string label)
        { Check(args.Contains(value), value + ": " + label); }
        private static void Check(bool condition, string label)
        { assertions++; if (!condition) throw new InvalidOperationException("TLS coverage fixture failed: " + label); }
        private static void Reject(Action action, string label)
        {
            assertions++;
            try { action(); } catch (ArgumentException) { return; }
            throw new InvalidOperationException("TLS coverage fixture did not reject: " + label);
        }
    }
}
