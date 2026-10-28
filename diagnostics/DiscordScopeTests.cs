using System;
using System.Collections.Generic;

namespace LocalLink
{
    // Offline argument fixtures only. No engine, driver, DNS, socket, process,
    // account, packet capture, user's settings or hostlist file is opened.
    // The matching model below checks generated wrapper order, not observed
    // winws2 packet behavior or a successful bidirectional Discord call.
    public static class DiscordScopeTests
    {
        static int assertions;
        static int fixtures;
        const string HostList = @"C:\LocalLink\fixtures\not-opened-public-list.lst";
        const string VoiceSelector = "--filter-udp=50000-50099,19294-19344";
        static readonly int[] VoicePorts = { 2053, 2083, 2087, 2096, 8443 };
        static readonly string[] RequiredDiscordHosts = {
            "discord.com", "updates.discord.com", "gateway.discord.gg",
            "cdn.discordapp.com", "media.discordapp.net", "voice.discord.media",
            "discord-attachments-uploads-prd.storage.googleapis.com"
        };
        static readonly string[] OtherHosts = {
            "youtube.com", "www.youtube.com", "googlevideo.com", "i.ytimg.com",
            "instagram.com", "telegram.org", "store.steampowered.com",
            "www.microsoft.com", "www.cloudflare.com", "example.org",
            "discord.com.evil.example", "notdiscord.com", "gatewaydiscord.gg",
            "discord.media.evil.example", "discordapp.com.attacker.example",
            "storage.googleapis.com", "googleapis.com", "other-bucket.storage.googleapis.com",
            "notdiscord-attachments-uploads-prd.storage.googleapis.com",
            "discord-attachments-uploads-prd.storage.googleapis.com.evil.example"
        };

        static int Main(string[] args)
        {
            if (args.Length != 0) return 2;
            try { return Run(); }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.GetType().Name + ": " + error.Message);
                return 1;
            }
        }

        public static int Run()
        {
            assertions = fixtures = 0;
            foreach (string general in Profiles.TlsStrategies())
            {
                foreach (bool voice in new[] { false, true })
                foreach (bool quic in new[] { false, true })
                foreach (bool list in new[] { false, true })
                {
                    var inherited = Spec(general, "", voice, quic, list);
                    var inheritedArguments = Profiles.Build(inherited);
                    CheckInheritedUpload(inheritedArguments, general, quic);
                    Check(Equal(inheritedArguments, Profiles.Build(Spec(general, "", voice, quic, list))),
                        "empty override retains deterministic inherited arguments");
                    foreach (string discord in Profiles.TlsStrategies())
                        CheckDirect(general, discord, voice, quic, list, inheritedArguments);
                }
            }
            foreach (string invalid in new[] { "inherit", "wireguard", "DISORDER", "disorder ",
                "fake-disorder --new", "disorder:pos=all", "\n--filter-l7=all" })
                Reject(delegate { Profiles.Build(Spec("split", invalid, false, false, false)); },
                    "unrecognized Discord override rejected");

            foreach (string discord in Profiles.TlsStrategies())
            foreach (string endpoint in new[] { "198.51.100.7", "2001:db8::7" })
            {
                Reject(delegate { Profiles.Build(new ProfileSpec { Mode = HelperMode.VpnTls,
                    Endpoint = endpoint, Strategy = "split", DiscordStrategy = discord }); },
                    "Discord domain override cannot broaden VPN TLS scope");
                Reject(delegate { Profiles.Build(new ProfileSpec { Mode = HelperMode.VpnWireGuard,
                    Endpoint = endpoint, Port = 51820, Strategy = "wireguard", DiscordStrategy = discord }); },
                    "Discord domain override cannot broaden VPN WireGuard scope");
            }
            Console.WriteLine("Discord scope tests passed: " + assertions + " assertions, " + fixtures +
                " fixtures (offline generated scope/order; not engine or call proof).");
            return 0;
        }

        static ProfileSpec Spec(string general, string discord, bool voice, bool quic, bool list)
        {
            return new ProfileSpec { Mode = HelperMode.Direct,
                Domains = ServiceCatalog.DomainsFor(new[] { "discord", "youtube" }, "example.org"),
                Strategy = general, DiscordStrategy = discord, DiscordVoice = voice,
                Quic = quic, HostListFile = list ? HostList : "" };
        }

        static void CheckInheritedUpload(List<string> arguments, string strategy, bool quic)
        {
            const string upload="discord-attachments-uploads-prd.storage.googleapis.com";
            var profiles=Split(arguments);
            foreach(string selector in new[]{"--filter-tcp=443","--filter-tcp=80"})
                Check(FirstMatching(profiles,selector,upload)!=null,"upload host covered by inherited Discord method: "+strategy+"/"+selector);
            if(quic)
                Check(FirstMatching(profiles,"--filter-udp=443",upload)!=null,"upload QUIC host has the existing scoped profile: "+strategy);
            string[] discord=Profiles.ValidateDomains(ServiceCatalog.DomainsFor(new[]{"discord"},""));
            Check(Array.IndexOf(discord,upload)>=0,"upload is part of the canonical Discord catalogue");
            foreach(string other in new[]{"googleapis.com","storage.googleapis.com","other-bucket.storage.googleapis.com",
                "notdiscord-attachments-uploads-prd.storage.googleapis.com","discord-attachments-uploads-prd.storage.googleapis.com.evil.example"})
                Check(!Matches(other,discord),"upload addition does not include other cloud hosts: "+other);
            foreach(string service in new[]{"youtube","telegram","steam","arma","tarkov","dcs"})
                Check(!Matches(upload,Profiles.ValidateDomains(ServiceCatalog.DomainsFor(new[]{service},""))),"upload host not added to "+service);
        }

        static void CheckDirect(string general, string discord, bool voice, bool quic,
            bool list, List<string> inheritedArguments)
        {
            fixtures++;
            string label = general + "/Discord=" + discord + "/voice=" + voice + "/QUIC=" + quic + "/list=" + list;
            var args = Profiles.Build(Spec(general, discord, voice, quic, list));
            var profiles = Split(args);
            var inherited = Split(inheritedArguments);
            Check(profiles.Count == inherited.Count + 2, "two separate Discord web profiles: " + label);
            Check(Equal(GlobalArguments(args), GlobalArguments(inheritedArguments)),
                "override does not widen/change kernel capture or Lua initializers: " + label);

            var tls = FindAll(profiles, "--filter-tcp=443");
            var http = FindAll(profiles, "--filter-tcp=80");
            Check(tls.Count == 2 && http.Count == 2, "dedicated and general TLS/HTTP only: " + label);
            Check(profiles.IndexOf(tls[0]) < profiles.IndexOf(tls[1]),
                "Discord TLS wins before first-match general union: " + label);
            Check(profiles.IndexOf(http[0]) < profiles.IndexOf(http[1]),
                "Discord HTTP wins before first-match general union: " + label);
            Check(Equal(tls[1], FindAll(inherited, "--filter-tcp=443")[0]),
                "general TLS strategy and exact hostlist arguments unchanged: " + label);
            Check(Equal(http[1], FindAll(inherited, "--filter-tcp=80")[0]),
                "general HTTP strategy and exact hostlist arguments unchanged: " + label);
            CheckDiscordHosts(tls[0], label);
            CheckDiscordHosts(http[0], label);
            CheckTlsActions(tls[0], discord, label);
            CheckHttpActions(http[0], discord, label);

            foreach (string host in RequiredDiscordHosts)
            {
                Check(Matches(host, InlineHosts(tls[0])), "Discord desktop/voice host covered: " + host + "/" + label);
                Check(FirstMatching(profiles, "--filter-tcp=443", host) == tls[0],
                    "Discord generated TLS profile selected first: " + host + "/" + label);
                Check(FirstMatching(profiles, "--filter-tcp=80", host) == http[0],
                    "Discord generated HTTP profile selected first: " + host + "/" + label);
            }
            foreach (string host in OtherHosts)
            {
                Check(!Matches(host, InlineHosts(tls[0])) && !Matches(host, InlineHosts(http[0])),
                    "unrelated/lookalike host outside dedicated Discord scope: " + host + "/" + label);
                // A public include list can cover unrelated hosts in GENERAL
                // profiles. We never pretend to know its contents here.
            }

            if (quic)
                Check(Equal(FindAll(profiles, "--filter-udp=443")[0], FindAll(inherited, "--filter-udp=443")[0]),
                    "QUIC Initial profile does not inherit Discord TLS actions: " + label);
            if (voice)
            {
                foreach (int port in VoicePorts)
                {
                    var extra = FindAll(profiles, "--filter-tcp=" + port)[0];
                    CheckTlsActions(extra, discord, label + "/voiceport=" + port);
                    Check(extra.Contains("--hostlist-domains=discord.media"), "voice TLS keeps fixed media suffix: " + label);
                    Check(CountPrefix(extra, "--hostlist=") == 0 && CountPrefix(extra, "--ipset") == 0,
                        "no general/public-IP scope leaks into voice TLS: " + label);
                }
                var udp = FindAll(profiles, VoiceSelector);
                Check(udp.Count == 1 && Equal(udp[0], FindAll(inherited, VoiceSelector)[0]),
                    "existing discovery/STUN UDP profile byte-for-byte unchanged: " + label);
                Check(udp[0].Contains("--payload=discord_ip_discovery,stun") &&
                    udp[0].Contains("--lua-desync=fake:blob=0x00000000000000000000000000000000:repeats=2"),
                    "no hidden new audio-UDP transformation: " + label);
            }
            else
            {
                Check(FindAll(profiles, VoiceSelector).Count == 0, "disabled voice remains disabled: " + label);
                foreach (int port in VoicePorts)
                    Check(FindAll(profiles, "--filter-tcp=" + port).Count == 0,
                        "override does not implicitly enable extra voice ports: " + label);
            }
            foreach (var profile in profiles)
                foreach (string argument in profile)
                    Check(argument != "--filter-l7=all" && argument != "--payload=all" &&
                        argument != "--filter-udp=*" && !argument.StartsWith("--wf-", StringComparison.Ordinal),
                        "no all-protocol fallback/global capture spill: " + label);
        }

        static void CheckDiscordHosts(List<string> profile, string label)
        {
            string[] actual = InlineHosts(profile);
            string[] catalogue = Profiles.ValidateDomains(ServiceCatalog.DomainsFor(new[] { "discord" }, ""));
            Check(actual.Length == catalogue.Length, "dedicated profile uses only canonical Discord catalogue: " + label);
            foreach (string domain in catalogue)
                Check(Array.IndexOf(actual, domain) >= 0, "canonical Discord suffix preserved: " + label);
            Check(CountPrefix(profile, "--hostlist-domains=") == 1 &&
                CountPrefix(profile, "--hostlist=") == 0 && CountPrefix(profile, "--hostlist-exclude") == 0 &&
                CountPrefix(profile, "--hostlist-auto") == 0 && CountPrefix(profile, "--ipset") == 0,
                "dedicated Discord profile never inherits external/custom/IP/auto scope: " + label);
        }

        static void CheckTlsActions(List<string> profile, string strategy, string label)
        {
            Check(profile.Contains("--filter-l7=tls") && profile.Contains("--payload=tls_client_hello") &&
                profile.Contains("--in-range=x") && profile.Contains("--out-range=-d10"),
                "dedicated TLS stays ClientHello/cutoff limited: " + label);
            var actions = PrefixValues(profile, "--lua-desync=");
            bool fake = strategy == "fake-split" || strategy == "fake-disorder";
            Check(actions.Count == (fake ? 2 : 1), "exact dedicated TLS action count: " + label);
            if (fake)
                Check(actions[0] == "--lua-desync=fake:blob=fake_default_tls:tcp_md5:repeats=2",
                    "existing bounded MD5 fake precedes real segmentation: " + label);
            string last = strategy == "disorder" || strategy == "fake-disorder" ?
                "--lua-desync=multidisorder:pos=1,midsld" : strategy == "sni-split" ?
                "--lua-desync=multisplit:pos=1,midsld" : "--lua-desync=multisplit:pos=2";
            Check(actions[actions.Count - 1] == last, "Discord override exact selected TLS method: " + label);
        }

        static void CheckHttpActions(List<string> profile, string strategy, string label)
        {
            var actions = PrefixValues(profile, "--lua-desync=");
            Check(profile.Contains("--filter-l7=http") && profile.Contains("--payload=http_req") &&
                actions.Count == 1, "dedicated HTTP remains one plaintext-request action: " + label);
            Check(actions[0] == (strategy == "disorder" || strategy == "fake-disorder" ?
                "--lua-desync=multidisorder:pos=2" : "--lua-desync=multisplit:pos=2"),
                "HTTP never uses TLS fakes or SNI marker: " + label);
        }

        static List<List<string>> Split(IEnumerable<string> arguments)
        {
            var result = new List<List<string>>();
            List<string> current = null;
            foreach (string argument in arguments)
            {
                if (argument == "--new")
                {
                    Check(current != null && current.Count > 0, "no empty profile boundary");
                    result.Add(current); current = null;
                }
                else if (!argument.StartsWith("--wf-", StringComparison.Ordinal) &&
                    !argument.StartsWith("--lua-init=", StringComparison.Ordinal))
                {
                    if (current == null) current = new List<string>();
                    current.Add(argument);
                }
            }
            Check(current != null && current.Count > 0, "last profile nonempty");
            result.Add(current);
            return result;
        }

        static List<string> GlobalArguments(IEnumerable<string> arguments)
        {
            var result = new List<string>();
            foreach (string argument in arguments)
                if (argument.StartsWith("--wf-", StringComparison.Ordinal) ||
                    argument.StartsWith("--lua-init=", StringComparison.Ordinal)) result.Add(argument);
            return result;
        }

        static List<List<string>> FindAll(IEnumerable<List<string>> profiles, string selector)
        {
            var result = new List<List<string>>();
            foreach (var profile in profiles) if (profile.Contains(selector)) result.Add(profile);
            return result;
        }

        static List<string> FirstMatching(IEnumerable<List<string>> profiles, string selector, string host)
        {
            foreach (var profile in profiles)
                if (profile.Contains(selector) && Matches(host, InlineHosts(profile))) return profile;
            return null;
        }

        static string[] InlineHosts(IEnumerable<string> profile)
        {
            foreach (string argument in profile)
                if (argument.StartsWith("--hostlist-domains=", StringComparison.Ordinal))
                    return argument.Substring("--hostlist-domains=".Length).Split(',');
            return new string[0];
        }

        static bool Matches(string host, IEnumerable<string> suffixes)
        {
            foreach (string suffix in suffixes)
                if (String.Equals(host, suffix, StringComparison.OrdinalIgnoreCase) ||
                    host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static List<string> PrefixValues(IEnumerable<string> values, string prefix)
        {
            var result = new List<string>();
            foreach (string value in values)
                if (value.StartsWith(prefix, StringComparison.Ordinal)) result.Add(value);
            return result;
        }
        static int CountPrefix(IEnumerable<string> values, string prefix) { return PrefixValues(values, prefix).Count; }
        static bool Equal(List<string> left, List<string> right)
        {
            if (left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++) if (left[i] != right[i]) return false;
            return true;
        }
        static void Reject(Action action, string label)
        {
            bool rejected = false;
            try { action(); } catch (ArgumentException) { rejected = true; }
            Check(rejected, label);
        }
        static void Check(bool condition, string label)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException("Assertion failed: " + label);
        }
    }
}
