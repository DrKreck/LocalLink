using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace LocalLink
{
    public enum HelperMode { Direct, VpnTls, VpnWireGuard }

    public sealed class ProfileSpec
    {
        public HelperMode Mode;
        public string Domains = "";
        public string Endpoint = "";
        public int Port = 443;
        public string Strategy = "split";
        // Empty inherits the general TLS method. A separate Discord method
        // never changes the YouTube/public-list profile or voice UDP handling.
        public string DiscordStrategy = "";
        public bool Quic;
        public bool DiscordVoice;
        public bool TelegramNative;
        public string HostListFile = "";
    }

    // Pure argument generation: no DNS lookup, file writes, engine execution or
    // network changes. The caller must quote each argument independently and set
    // the engine directory as the working directory (never invoke a shell).
    // Syntax verified against bol-van/zapret-win-bundle commit
    // 6eb463a6758fb48cd101bc55dfd057e6e9d98af1, preset2_example.cmd,
    // blockcheck/zapret2/blockcheck2.d/standard/20-multi.sh,
    // 50-fake-multi.sh, def.inc and bundled Lua. This is static syntax/source
    // verification only; no strategy is a connectivity or IP-block guarantee.
    public static class Profiles
    {
        internal const string DiscordDomains = "discord.com,discord.gg,discordapp.com,discordapp.net,discord.media,discord-attachments-uploads-prd.storage.googleapis.com";
        // Fresh copies prevent UI/test callers from mutating the shared order.
        // Mild real segmentation first; fake techniques remain opt-in trials.
        public static string[] TlsStrategies()
        { return new[] { "split", "sni-split", "disorder", "fake-split", "fake-disorder" }; }

        public static bool IsTlsStrategy(string value)
        {
            return value == "split" || value == "sni-split" || value == "disorder" ||
                value == "fake-split" || value == "fake-disorder";
        }

        private const int MaxDomainCount = 32;
        private const int MaxDomainTextLength = 8192;
        // Telegram's published networks, checked 2026-09-27. Not an all-IP bypass.
        internal const string TelegramNetworks = "91.108.56.0/22,91.108.4.0/22,91.108.8.0/22,91.108.16.0/22,91.108.12.0/22,149.154.160.0/20,91.105.192.0/23,91.108.20.0/22,185.76.151.0/24,2001:b28:f23d::/48,2001:b28:f23f::/48,2001:67c:4e8::/48,2001:b28:f23c::/48,2a0a:f280::/32";

        public static List<string> Build(ProfileSpec spec)
        {
            if (spec == null) throw new ArgumentNullException("spec");
            if (spec.Mode != HelperMode.Direct && spec.Mode != HelperMode.VpnTls &&
                spec.Mode != HelperMode.VpnWireGuard)
                throw new ArgumentException("Unknown helper mode.", "spec");

            bool wireguard = spec.Mode == HelperMode.VpnWireGuard;
            if (wireguard ? spec.Strategy != "wireguard" : !IsTlsStrategy(spec.Strategy))
                throw new ArgumentException("Choose a supported TLS method, or wireguard for WireGuard.", "spec");
            if (!String.IsNullOrEmpty(spec.DiscordStrategy) &&
                (spec.Mode != HelperMode.Direct || !IsTlsStrategy(spec.DiscordStrategy)))
                throw new ArgumentException("Separate Discord TLS methods are supported only in Direct mode.", "spec");
            if (spec.Mode != HelperMode.Direct && spec.Quic)
                throw new ArgumentException("The QUIC option is available only in Direct mode.", "spec");
            if (spec.Mode != HelperMode.Direct && (spec.DiscordVoice || spec.TelegramNative || !String.IsNullOrEmpty(spec.HostListFile)))
                throw new ArgumentException("Native service and public-list profiles are available only in Direct mode.", "spec");

            List<string> args = new List<string>();
            args.Add("--lua-init=@lua/zapret-lib.lua");
            args.Add("--lua-init=@lua/zapret-antidpi.lua");

            if (spec.Mode == HelperMode.Direct)
            {
                string hostFile = ValidateHostListFile(spec.HostListFile);
                string domains = String.IsNullOrWhiteSpace(spec.Domains) && hostFile.Length > 0 ?
                    null : String.Join(",", ValidateDomains(spec.Domains));
                string tcpPorts = spec.TelegramNative ? "80,443,5222" : "80,443";
                if (spec.DiscordVoice) tcpPorts += ",2053,2083,2087,2096,8443";
                args.Add("--wf-tcp-out=" + tcpPorts);
                if (spec.Quic) args.Add("--wf-udp-out=443");
                if (spec.DiscordVoice)
                {
                    args.Add("--wf-raw-part=" + DiscordDiscoveryFilter());
                    args.Add("--wf-raw-part=" + DiscordStunFilter());
                }
                string discordMethod = String.IsNullOrEmpty(spec.DiscordStrategy) ? spec.Strategy : spec.DiscordStrategy;
                if (!String.IsNullOrEmpty(spec.DiscordStrategy))
                {
                    // The pinned engine uses FIRST matching profile. Never add
                    // the public list here: include lists are a UNION and would
                    // silently apply Discord's method to unrelated services.
                    AddTlsProfile(args, 443, DiscordDomains, null, discordMethod, "");
                    args.Add("--new");
                    AddHttpProfile(args, DiscordDomains, "", discordMethod);
                    args.Add("--new");
                }
                AddTlsProfile(args, 443, domains, null, spec.Strategy, hostFile);
                args.Add("--new");
                AddHttpProfile(args, domains, hostFile, spec.Strategy);
                if (spec.Quic)
                {
                    // QUIC Initial only, with the SAME host allowlist. There is
                    // deliberately no fallback all-QUIC or all-UDP profile.
                    args.Add("--new");
                    args.Add("--filter-udp=443");
                    args.Add("--filter-l7=quic");
                    AddHosts(args, domains, hostFile);
                    args.Add("--in-range=x");
                    args.Add("--payload=quic_initial");
                    args.Add("--lua-desync=fake:blob=fake_default_quic:repeats=11");
                }
                if (spec.DiscordVoice)
                {
                    // Voice control uses additional TLS ports. Keep these
                    // profiles limited to discord.media, never the public list
                    // or the general/custom domain allowlist. The port set is
                    // observed in Flowseal's official ALT13 strategy; it is not
                    // an exhaustive or permanent Discord port guarantee.
                    foreach (int voicePort in new[] { 2053, 2083, 2087, 2096, 8443 })
                    {
                        args.Add("--new");
                        AddTlsProfile(args, voicePort, "discord.media", null, discordMethod, "");
                    }
                    args.Add("--new");
                    args.Add("--filter-udp=50000-50099,19294-19344");
                    args.Add("--filter-l7=discord,stun");
                    args.Add("--in-range=x");
                    args.Add("--payload=discord_ip_discovery,stun");
                    args.Add("--lua-desync=fake:blob=0x00000000000000000000000000000000:repeats=2");
                }
                if (spec.TelegramNative)
                {
                    args.Add("--new");
                    args.Add("--filter-tcp=80,443,5222");
                    args.Add("--filter-l7=mtproto,unknown");
                    args.Add("--ipset-ip=" + TelegramNetworks);
                    args.Add("--in-range=x");
                    args.Add("--out-range=-d1");
                    args.Add("--payload=mtproto_initial,unknown");
                    // Unknown first payload is allowed only in Telegram's IP
                    // scope. Splitting changes segmentation, not payload bytes.
                    args.Add("--lua-desync=multisplit:pos=2:payload=mtproto_initial,unknown");
                }
                return args;
            }

            if (spec.Port < 1 || spec.Port > 65535)
                throw new ArgumentException("VPN port must be between 1 and 65535.", "spec");
            IPAddress endpoint = ValidateEndpoint(spec.Endpoint);
            string address = endpoint.ToString();
            string port = spec.Port.ToString(CultureInfo.InvariantCulture);
            string addressField = endpoint.AddressFamily == AddressFamily.InterNetwork ? "ip.DstAddr" : "ipv6.DstAddr";

            // Kernel-level AND restriction, in addition to profile-level ipset.
            // Private unicast endpoints are intentionally supported, but neither
            // loopback nor multicast/link-local/unspecified endpoints are allowed.
            args.Add("--wf-raw-filter=" + addressField + "=" + address);
            args.Add("--wf-filter-lan=0");
            if (wireguard)
            {
                // Initiation signature from the pinned official WinDivert filter.
                // No application data, response/cookie, STUN or arbitrary UDP.
                args.Add("--wf-raw-part=outbound and udp.DstPort=" + port +
                    " and udp.PayloadLength=148 and udp.Payload32[0]=0x01000000");
                args.Add("--filter-udp=" + port);
                args.Add("--filter-l7=wireguard");
                args.Add("--ipset-ip=" + address);
                args.Add("--in-range=x");
                args.Add("--payload=wireguard_initiation");
                // Minimal fake from the official preset2_example.cmd. This does
                // not turn WireGuard into TLS or require a modified VPN server.
                args.Add("--lua-desync=fake:blob=0x00000000000000000000000000000000:repeats=2");
            }
            else
            {
                args.Add("--wf-tcp-out=" + port);
                AddTlsProfile(args, spec.Port, null, address, spec.Strategy, "");
            }
            return args;
        }

        private static void AddTlsProfile(List<string> args, int port, string domains, string address, string strategy, string hostFile)
        {
            args.Add("--filter-tcp=" + port.ToString(CultureInfo.InvariantCulture));
            args.Add("--filter-l7=tls");
            AddHosts(args, domains, hostFile);
            if (address != null) args.Add("--ipset-ip=" + address);
            args.Add("--in-range=x");
            args.Add("--out-range=-d10");
            args.Add("--payload=tls_client_hello");
            // Explicit, opt-in fallback: two fake ClientHellos with TCP MD5
            // fooling, then segmentation/reordering of the real ClientHello.
            // The fake-only MD5 option does not alter the real TLS bytes,
            // use a timestamp prerequisite, or change system TCP settings.
            // Keep it inside the SAME domain/endpoint and payload scope.
            if (strategy == "fake-split" || strategy == "fake-disorder")
                args.Add("--lua-desync=fake:blob=fake_default_tls:tcp_md5:repeats=2");
            // The SNI-aware real split is an upstream blockcheck candidate,
            // not a proven cure on the user's route. It preserves TLS bytes,
            // host/endpoint allowlists, first-ClientHello payload and cutoff.
            // Existing saved strategies retain their previous arguments.
            args.Add(strategy == "disorder" || strategy == "fake-disorder" ?
                "--lua-desync=multidisorder:pos=1,midsld" : strategy == "sni-split" ?
                "--lua-desync=multisplit:pos=1,midsld" : "--lua-desync=multisplit:pos=2");
        }

        private static void AddHosts(List<string> args, string domains, string hostFile)
        {
            if (domains != null) args.Add("--hostlist-domains=" + domains);
            // Pinned hostlist.c implements union of include lists, not intersection.
            if (hostFile.Length > 0) args.Add("--hostlist=" + hostFile);
        }

        private static void AddHttpProfile(List<string> args, string domains, string hostFile, string strategy)
        {
            args.Add("--filter-tcp=80");
            args.Add("--filter-l7=http");
            AddHosts(args, domains, hostFile);
            args.Add("--in-range=x");
            args.Add("--out-range=-d10");
            args.Add("--payload=http_req");
            // Split the request method at a fixed byte, not the TLS-only midsld
            // marker. fake-split deliberately uses only a real split for HTTP;
            // never send a fake TLS ClientHello into a plaintext HTTP flow.
            args.Add(strategy == "disorder" || strategy == "fake-disorder" ? "--lua-desync=multidisorder:pos=2" :
                "--lua-desync=multisplit:pos=2");
        }

        private static string DiscordDiscoveryFilter()
        {
            // Exact pinned official windivert_part.discord_media.txt predicate.
            // IPv4 only, no blanket STUN/media capture or process association.
            string filter = "outbound and ip and (udp.DstPort>=50000 and udp.DstPort<=50099 or udp.DstPort>=19294 and udp.DstPort<=19344) and udp.PayloadLength=74 and udp.Payload32[0]=0x00010046";
            for (int i = 2; i <= 17; i++) filter += " and udp.Payload32[" + i.ToString(CultureInfo.InvariantCulture) + "]=0";
            return filter;
        }

        private static string DiscordStunFilter()
        {
            // Pinned upstream windivert_part.stun.txt signature, narrowed to
            // the SAME voice port ranges as discovery. No blanket UDP capture,
            // media payload modification or WireGuard/QUIC profile. STUN is
            // not Discord-exclusive: another application using these ports can
            // match too, so this remains part of the opt-in voice feature.
            return "outbound and udp and (udp.DstPort>=50000 and udp.DstPort<=50099 or udp.DstPort>=19294 and udp.DstPort<=19344) and udp.PayloadLength>=20 and udp.Payload32[1]=0x2112A442 and udp.Payload[0]<0x40";
        }

        public static string ValidateHostListFile(string value)
        {
            if (String.IsNullOrEmpty(value)) return String.Empty;
            if (value.Length < 4 || value.Length > 240 || !Char.IsLetter(value[0]) || value[0] > 127 ||
                value[1] != ':' || value[2] != '\\')
                throw new ArgumentException("Hostlist must be a local absolute Windows file path.", "value");
            for (int i = 3; i < value.Length; i++)
            {
                char c = value[i];
                // This is a filename argument to CreateProcessW, NOT a shell
                // command. Legal Windows punctuation such as (), &, + and []
                // must work in installation and LocalAppData user paths.
                // Ownership/hash/reparse checks remain in OpenVerified.
                if (Char.IsControl(c) || "<>\"|?*:/".IndexOf(c) >= 0 ||
                    Char.GetUnicodeCategory(c) == UnicodeCategory.Format)
                    throw new ArgumentException("Путь списка содержит запрещённый или управляющий символ.", "value");
                if (Char.IsHighSurrogate(c))
                {
                    if (i + 1 >= value.Length || !Char.IsLowSurrogate(value[i + 1]))
                        throw new ArgumentException("Путь списка содержит некорректный Unicode.", "value");
                    if (CharUnicodeInfo.GetUnicodeCategory(value, i) == UnicodeCategory.Format)
                        throw new ArgumentException("Путь списка содержит запрещённый или управляющий символ.", "value");
                    i++;
                }
                else if (Char.IsLowSurrogate(c))
                    throw new ArgumentException("Путь списка содержит некорректный Unicode.", "value");
            }
            string[] parts = value.Substring(3).Split('\\');
            foreach (string part in parts)
            {
                if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".", StringComparison.Ordinal) ||
                    part.EndsWith(" ", StringComparison.Ordinal))
                    throw new ArgumentException("Hostlist path must be canonical.", "value");
                string stem = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
                if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" ||
                    stem == "CONIN$" || stem == "CONOUT$" ||
                    (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                        stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                        ((stem[3] >= '1' && stem[3] <= '9') || stem[3] == '\u00b9' || stem[3] == '\u00b2' || stem[3] == '\u00b3')))
                    throw new ArgumentException("Путь списка содержит зарезервированное имя устройства Windows.", "value");
            }
            if (!String.Equals(Path.GetFullPath(value), value, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Hostlist path must be canonical.", "value");
            return value;
        }

        // Hostlists match a domain and its subdomains, as defined by zapret2.
        // An IP address, URL, wildcard, list-control prefix (#/^), port or path
        // is not a domain. ECH / absent SNI and pure IP blocks are not solved.
        public static string[] ValidateDomains(string text)
        {
            if (text == null || text.Length > MaxDomainTextLength)
                throw new ArgumentException("Enter up to 32 domains, one per line (maximum 8192 characters).", "text");
            List<string> domains = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IdnMapping idn = new IdnMapping();
            idn.UseStd3AsciiRules = true;
            foreach (string rawLine in text.Split(new char[] { '\r', '\n' }, StringSplitOptions.None))
            {
                foreach (char c in rawLine)
                {
                    if (Char.IsControl(c) || Char.GetUnicodeCategory(c) == UnicodeCategory.Format)
                        throw new ArgumentException("Domain text contains a control character.", "text");
                }
                string domain = rawLine.Trim();
                if (domain.Length == 0) continue;
                if (domain.Length > 254) throw new ArgumentException("Domain is too long.", "text");
                foreach (char c in domain)
                {
                    if (c < 128 && !(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') &&
                        !(c >= '0' && c <= '9') && c != '-' && c != '.')
                        throw new ArgumentException("Use domain names only, without URLs, ports, wildcards or options.", "text");
                }
                try { domain = idn.GetAscii(domain).ToLowerInvariant(); }
                catch (ArgumentException) { throw new ArgumentException("Invalid international domain name.", "text"); }
                if (domain.EndsWith(".", StringComparison.Ordinal)) domain = domain.Substring(0, domain.Length - 1);
                if (domain.Length > 253) throw new ArgumentException("Domain is too long.", "text");
                string[] labels = domain.Split('.');
                if (labels.Length < 2) throw new ArgumentException("Use a full domain name, such as example.org.", "text");
                foreach (string label in labels)
                {
                    if (label.Length < 1 || label.Length > 63 || label[0] == '-' || label[label.Length - 1] == '-')
                        throw new ArgumentException("Invalid domain label.", "text");
                    foreach (char c in label)
                        if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '-')
                            throw new ArgumentException("Invalid domain character.", "text");
                }
                IPAddress ignored;
                if (IPAddress.TryParse(domain, out ignored))
                    throw new ArgumentException("Direct mode needs a domain name, not an IP address.", "text");
                if (seen.Add(domain)) domains.Add(domain);
                if (domains.Count > MaxDomainCount)
                    throw new ArgumentException("A maximum of 32 distinct domains is supported.", "text");
            }
            if (domains.Count == 0) throw new ArgumentException("Enter at least one domain name.", "text");
            return domains.ToArray();
        }

        private static IPAddress ValidateEndpoint(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length > 45)
                throw new ArgumentException("Enter a VPN server IP literal, without a port or hostname.", "value");
            foreach (char c in value)
                if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f') &&
                    !(c >= 'A' && c <= 'F') && c != ':' && c != '.')
                    throw new ArgumentException("VPN endpoint must be a plain IPv4 or IPv6 address.", "value");
            IPAddress address;
            if (!IPAddress.TryParse(value, out address))
                throw new ArgumentException("Invalid VPN server IP address.", "value");
            byte[] bytes = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                // Reject abbreviated, octal/hex and integer IPv4 accepted by
                // IPAddress.TryParse; canonical dotted decimal is unambiguous.
                if (!String.Equals(value, address.ToString(), StringComparison.Ordinal) ||
                    bytes[0] == 0 || bytes[0] == 127 || bytes[0] >= 224 ||
                    (bytes[0] == 169 && bytes[1] == 254))
                    throw new ArgumentException("Use an ordinary unicast VPN server address, not loopback/link-local/multicast.", "value");
            }
            else if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                bool zeroPrefix = true;
                for (int i = 0; i < 10; i++) if (bytes[i] != 0) zeroPrefix = false;
                bool mappedOrCompatible = zeroPrefix &&
                    ((bytes[10] == 255 && bytes[11] == 255) || (bytes[10] == 0 && bytes[11] == 0));
                if (mappedOrCompatible || IPAddress.IsLoopback(address) || address.IsIPv6Multicast || address.IsIPv6LinkLocal)
                    throw new ArgumentException("Use a unicast IPv6 server address, not mapped/loopback/link-local/multicast.", "value");
            }
            else throw new ArgumentException("Only IPv4 and IPv6 endpoints are supported.", "value");
            return address;
        }
    }
}
