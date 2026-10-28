using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace LocalLink
{
    // Argument generation and component preflight only. Never starts an engine,
    // driver, network request or changes the user's domain snapshots.
    internal static class DomainExcludeTests
    {
        static int checks;
        static int Main()
        {
            try
            {
                const string include = @"C:\LocalLink\lists\sites-include.lst";
                const string exclude = @"C:\Users\Игрок (1)\LocalLink\lists\sites-exclude.lst";
                foreach (string strategy in Profiles.TlsStrategies())
                    foreach (string discord in new[] { "", "disorder", "fake-split" })
                        foreach (bool voice in new[] { false, true })
                            foreach (bool quic in new[] { false, true })
                                foreach (bool native in new[] { false, true })
                                    foreach (bool external in new[] { false, true })
                                    {
                                        var spec = new ProfileSpec { Domains = "example.org", Strategy = strategy,
                                            DiscordStrategy = discord, DiscordVoice = voice, Quic = quic,
                                            TelegramNative = native, HostListFile = external ? include : "" };
                                        var baseline = Profiles.Build(spec);
                                        Check(!baseline.Any(x => x.StartsWith("--hostlist-exclude=", StringComparison.Ordinal)), "no opt-in exclusion flags");
                                        spec.HostListExcludeFile = exclude;
                                        var actual = Profiles.Build(spec);
                                        Check(actual.Where(x => !x.StartsWith("--hostlist-exclude=", StringComparison.Ordinal)).SequenceEqual(baseline),
                                            "exclusions never change existing filters, includes or desync methods");
                                        int applicable = 0;
                                        foreach (var profile in Split(actual))
                                        {
                                            bool hosts = profile.Any(x => x.StartsWith("--hostlist-domains=", StringComparison.Ordinal) ||
                                                x.StartsWith("--hostlist=", StringComparison.Ordinal));
                                            int exclusions = profile.Count(x => x == "--hostlist-exclude=" + exclude);
                                            Check(exclusions == (hosts ? 1 : 0), "one exclusion per hostname profile; none for raw UDP/Telegram");
                                            if (hosts) applicable++;
                                        }
                                        Check(applicable == 2 + (discord.Length > 0 ? 2 : 0) + (voice ? 5 : 0) + (quic ? 1 : 0),
                                            "all inherited/separate Discord, voice TLS, HTTP and QUIC profiles covered");
                                    }
                foreach (string unsafePath in new[] { "relative.lst", @"\\server\share\sites.lst", @"C:\LocalLink\..\sites.lst",
                    @"C:\LocalLink\sites.lst:stream", "C:\\LocalLink\\sites\".lst", "C:\\LocalLink\\sites\n.lst" })
                    Expect<ArgumentException>(() => Profiles.Build(new ProfileSpec { Domains = "example.org", HostListExcludeFile = unsafePath }), "unsafe exclusion path rejected");
                foreach (HelperMode mode in new[] { HelperMode.VpnTls, HelperMode.VpnWireGuard })
                    Expect<ArgumentException>(() => Profiles.Build(new ProfileSpec { Mode = mode, Endpoint = "203.0.113.10",
                        Strategy = mode == HelperMode.VpnTls ? "split" : "wireguard", HostListExcludeFile = exclude }), "VPN modes reject hostname exclusion scope");

                CheckPreflight();
                Console.WriteLine("Domain exclusions: " + checks + " offline assertions passed; no network, filter, driver or settings changes.");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        static IEnumerable<List<string>> Split(IEnumerable<string> args)
        {
            var values = new List<string>();
            foreach (string arg in args)
            {
                if (arg == "--new") { yield return values; values = new List<string>(); }
                else values.Add(arg);
            }
            yield return values;
        }

        static void CheckPreflight()
        {
            string root = AppDomain.CurrentDomain.BaseDirectory;
            string folder = Path.Combine(root, "lists");
            var snapshot = new JavaScriptSerializer().Deserialize<ListSnapshot>(File.ReadAllText(Path.Combine(folder, "current.json"), Encoding.UTF8));
            string path = Path.Combine(folder, snapshot.FileName);
            string component = Path.Combine(root, "engine", "winws2.exe");
            using (var engine = new Engine(root))
            {
                var spec = new ProfileSpec { Domains = "example.org", HostListExcludeFile = path };
                var preparing = engine.PrepareStartAsync(spec, CancellationToken.None);
                spec.HostListExcludeFile = "changed-after-capture";
                using (var prepared = preparing.GetAwaiter().GetResult())
                {
                    Check(!engine.IsRunning, "exclusion preflight does not start engine");
                    Check(!CanExclusivelyRead(path), "exclusion file read guard retained");
                    Check(prepared.CopyArguments().Contains("--hostlist-exclude=" + path), "immutable snapshot captures exclusion before worker starts");
                    Check(!prepared.CopyArguments().Any(x => x.Contains("changed-after-capture")), "caller mutation cannot change prepared arguments");
                }
                Check(CanExclusivelyRead(path), "disposing preflight releases exclusion guard");
                using (var source = new CancellationTokenSource())
                using (var prepared = engine.PrepareStartAsync(new ProfileSpec { Domains = "example.org", HostListExcludeFile = path }, source.Token).GetAwaiter().GetResult())
                {
                    source.Cancel();
                    Expect<OperationCanceledException>(() => engine.StartPrepared(prepared), "cancelled commit rejects before launch");
                }
                Check(CanExclusivelyRead(path) && CanExclusivelyRead(component), "cancelled commit releases both exclusion and component guards");
                Expect<IOException>(() => engine.PrepareStartAsync(new ProfileSpec { Domains = "example.org",
                    HostListExcludeFile = Path.Combine(folder, "sites-no-passport.lst") }, CancellationToken.None).GetAwaiter().GetResult(),
                    "unverified exclusion snapshot rejected before launch");
                Check(CanExclusivelyRead(component), "failed exclusion verification releases component guards");
            }
        }

        static bool CanExclusivelyRead(string path)
        {
            try { using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) return true; }
            catch (IOException) { return false; }
        }
        static void Expect<T>(Action action, string label) where T : Exception
        { bool rejected = false; try { action(); } catch (T) { rejected = true; } Check(rejected, label); }
        static void Check(bool condition, string label)
        { if (!condition) throw new InvalidOperationException(label); checks++; }
    }
}
