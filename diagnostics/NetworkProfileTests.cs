using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace LocalLink
{
    internal static class NetworkProfileTests
    {
        static int checks;
        static readonly Encoding Utf8 = new UTF8Encoding(false);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        static extern bool CreateSymbolicLink(string link, string target, int flags);
        static int Main(string[] args)
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "network-profile-fixtures", Guid.NewGuid().ToString("N"));
            try
            {
                Guid a = new Guid("11111111-2222-3333-4444-555555555555"), b = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
                var first = NetworkSnapshot.FromNetworkIds(new[] { a });
                var second = NetworkSnapshot.FromNetworkIds(new[] { b });
                Check(first.Available && first.NetworkCount == 1 && first.Key.Length == 64, "one connected network yields bounded SHA256 identity");
                Check(first.Key != second.Key, "different Windows network profiles remain distinct on the same adapter");
                Check(first.Key == NetworkSnapshot.FromNetworkIds(new[] { a, Guid.Empty, a }).Key, "empty IDs and duplicates do not change identity");
                Check(NetworkSnapshot.FromNetworkIds(new[] { b, a }).Key == NetworkSnapshot.FromNetworkIds(new[] { a, b }).Key, "network enumeration order is irrelevant");
                Check(NetworkSnapshot.FromNetworkIds(new[] { a, b }).NetworkCount == 2, "multiple connected profiles are represented explicitly");
                Check(!NetworkSnapshot.FromNetworkIds(new Guid[0]).Available && !NetworkSnapshot.FromNetworkIds(new[] { Guid.Empty }).Available, "no profile is never bound to a global fallback identity");
                Check(!NetworkSnapshot.FromNetworkIds(Enumerable.Repeat(a, 65)).Available, "too many observations refuse a truncated identity");
                Check(!first.Key.Contains(a.ToString("N")) && !first.Key.Contains(a.ToString("D")), "network GUID is not retained as an exported identifier");
                Check(NetworkSnapshot.Unavailable().Key == "" && !NetworkSnapshot.Unavailable().Available, "unavailable snapshot contains no synthetic identity");
                Expect<ArgumentNullException>(() => NetworkSnapshot.FromNetworkIds(null), "null observations rejected");
                var connection = new NetworkConnectionObservation { NetworkId = a, Connected = true, Physical = true };
                Check(NetworkSnapshot.FromConnections(new[] { connection }).Key == first.Key, "physical connection observation identifies its Windows network");
                Check(NetworkSnapshot.FromConnections(new[] { connection, new NetworkConnectionObservation { NetworkId = b, Connected = true, Physical = false } }).Key == first.Key, "VPN or virtual adapter does not change physical-network identity");
                Check(NetworkSnapshot.FromConnections(new[] { connection, new NetworkConnectionObservation { NetworkId = b, Connected = false, Physical = true } }).Key == first.Key, "disconnected physical link does not alter current network");
                Check(!NetworkSnapshot.FromConnections(new[] { new NetworkConnectionObservation { NetworkId = b, Connected = true, Physical = false } }).Available, "virtual-only observation never invents a physical-network binding");
                Check(!NetworkSnapshot.FromConnections(Enumerable.Repeat(connection, 257)).Available, "connection observation count is bounded");
                Expect<ArgumentNullException>(() => NetworkSnapshot.FromConnections(null), "null connection collection rejected");
                string file = Path.Combine(root, "preferences", "networks.json");
                var store = new NetworkPresetStore(file);
                Check(store.Count == 0 && store.Find(first.Key) == "" && !Directory.Exists(root), "read-only lookup does not create user files");
                Check(!store.Forget(first.Key) && !Directory.Exists(root), "forgetting an unknown network does not create files");
                string preset = Guid.NewGuid().ToString("N"), other = Guid.NewGuid().ToString("N");
                store.Remember(first.Key, preset);
                Check(store.Count == 1 && store.Find(first.Key) == preset && store.Find(second.Key) == "", "explicit mapping is limited to its exact network");
                byte[] initial = File.ReadAllBytes(file);
                int fileCount = Directory.GetFiles(Path.GetDirectoryName(file)).Length;
                store.Remember(first.Key, preset);
                Check(Directory.GetFiles(Path.GetDirectoryName(file)).Length == fileCount && File.ReadAllBytes(file).SequenceEqual(initial), "unchanged mapping creates no backup churn");
                store.Remember(first.Key, other);
                string[] backups = Directory.GetFiles(Path.GetDirectoryName(file), "networks.json.backup-*");
                Check(backups.Length == 1 && File.ReadAllBytes(backups[0]).SequenceEqual(initial), "mapping replacement preserves exact preceding bytes");
                Check(new NetworkPresetStore(file).Find(first.Key) == other, "binding survives application restart");
                store.Remember(second.Key, preset);
                Check(store.Count == 2 && store.Find(first.Key) == other && store.Find(second.Key) == preset, "independent networks can select independent presets");
                string json = File.ReadAllText(file);
                foreach (string unwanted in new[] { a.ToString("D"), a.ToString("N"), "SSID", "MAC", "192.168.", "Hardware", "Adapter", "NetworkName" })
                    Check(!json.Contains(unwanted), "network binding contains no raw network or hardware metadata: " + unwanted);
                Check(store.Forget(first.Key) && store.Count == 1 && store.Find(first.Key) == "" && store.Find(second.Key) == preset, "explicit forget preserves other bindings");
                foreach (string invalid in new[] { "", "../escape", new string('A', 64), new string('g', 64), new string('a', 63), new string('a', 65) })
                {
                    Expect<ArgumentException>(() => store.Find(invalid), "invalid key rejected before reading");
                    Expect<ArgumentException>(() => store.Remember(invalid, preset), "invalid key rejected before writing");
                    Expect<ArgumentException>(() => store.Forget(invalid), "invalid key rejected before forgetting");
                }
                foreach (string invalid in new[] { "", "../escape", Guid.NewGuid().ToString("D"), other.ToUpperInvariant(), new string('g', 32) })
                    Expect<ArgumentException>(() => store.Remember(first.Key, invalid), "invalid or noncanonical preset ID rejected");
                foreach (string invalid in new[] { "relative.json", @"\\server\share\file.json", file + ":ads" })
                    Expect<ArgumentException>(() => new NetworkPresetStore(invalid), "nonlocal or alternate-stream path rejected");
                byte[] previous = File.ReadAllBytes(file);
                using (var guard = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Expect<IOException>(() => store.Remember(first.Key, preset), "locked primary cannot be replaced");
                Check(File.ReadAllBytes(file).SequenceEqual(previous), "failed atomic commit leaves previous bindings intact");
                string damaged = Path.Combine(root, "damaged.json");
                var broken = new NetworkPresetStore(damaged);
                foreach (string corrupt in new[] {
                    "{broken", "null", "[]", "{\"Version\":2,\"Bindings\":[]}", "{\"Version\":1,\"Bindings\":null}",
                    "{\"Version\":1,\"Bindings\":[],\"Extra\":1}", "{\"Version\":1,\"Bindings\":[{}]}",
                    "{\"Version\":1,\"Bindings\":[{\"NetworkKey\":\"../escape\",\"PresetId\":\"" + preset + "\"}]}" })
                {
                    File.WriteAllText(damaged, corrupt, Utf8);
                    ExpectAny(() => broken.Remember(first.Key, preset), "corrupt store never silently overwritten");
                    Check(File.ReadAllText(damaged) == corrupt, "corrupt primary preserved for recovery");
                }
                string entry = "{\"NetworkKey\":\"" + first.Key + "\",\"PresetId\":\"" + preset + "\"}";
                File.WriteAllText(damaged, "{\"Version\":1,\"Bindings\":[" + entry + "," + entry + "]}", Utf8);
                Expect<IOException>(() => broken.Find(first.Key), "duplicate network keys rejected");
                File.WriteAllBytes(damaged, new byte[] { 123, 255, 125 });
                Expect<DecoderFallbackException>(() => broken.Find(first.Key), "malformed UTF8 rejected");
                File.WriteAllBytes(damaged, new byte[NetworkPresetStore.MaximumBytes + 1]);
                Expect<IOException>(() => broken.Find(first.Key), "oversize file rejected before deserialize");
                string limitedPath = Path.Combine(root, "limited.json");
                var limited = new NetworkPresetStore(limitedPath);
                for (int i = 0; i < NetworkPresetStore.MaximumBindings; i++) limited.Remember(i.ToString("x64"), preset);
                Check(limited.Count == NetworkPresetStore.MaximumBindings, "all allowed network mappings roundtrip");
                previous = File.ReadAllBytes(limitedPath);
                Expect<IOException>(() => limited.Remember(new string('f', 64), preset), "binding cap enforced");
                Check(File.ReadAllBytes(limitedPath).SequenceEqual(previous), "binding cap preserves prior file");
                limited.Remember(0.ToString("x64"), other);
                Check(limited.Find(0.ToString("x64")) == other, "existing mapping remains editable at the cap");
                string linkedFolder = Path.Combine(root, "linked-folder");
                if (CreateSymbolicLink(linkedFolder, Path.GetDirectoryName(file), 3) || CreateSymbolicLink(linkedFolder, Path.GetDirectoryName(file), 1))
                    Expect<IOException>(() => new NetworkPresetStore(Path.Combine(linkedFolder, "networks.json")), "reparse ancestor rejected");
                else Console.WriteLine("SKIP: fixture folder symlink unavailable; no privileges changed.");
                string linkedFile = Path.Combine(root, "linked.json");
                if (CreateSymbolicLink(linkedFile, file, 2) || CreateSymbolicLink(linkedFile, file, 0))
                    Expect<IOException>(() => new NetworkPresetStore(linkedFile), "reparse primary rejected");
                else Console.WriteLine("SKIP: fixture file symlink unavailable; no privileges changed.");
                if (args.Length == 1 && args[0] == "--live-read-only")
                {
                    var observation = NetworkIdentityObserver.Capture();
                    Check(!observation.Available || (observation.Key.Length == 64 && observation.NetworkCount > 0), "read-only Windows NLM observation has valid bounded shape");
                    Console.WriteLine("Read-only NLM observation: available=" + observation.Available + ", count=" + observation.NetworkCount + "; identifiers not logged.");
                }
                Console.WriteLine("Network profiles: " + checks + " assertions passed. Isolated fixtures: " + root);
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); Console.Error.WriteLine("Fixtures retained: " + root); return 1; }
        }
        static void Check(bool value, string text) { checks++; if (!value) throw new Exception(text); }
        static void Expect<T>(Action action, string text) where T : Exception
        { checks++; try { action(); } catch (T) { return; } throw new Exception(text); }
        static void ExpectAny(Action action, string text)
        {
            checks++; try { action(); }
            catch (IOException) { return; } catch (ArgumentException) { return; } catch (InvalidOperationException) { return; }
            throw new Exception(text);
        }
    }
}
