using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace LocalLink
{
    internal static class PresetTests
    {
        static int checks;
        static readonly Encoding Utf8 = new UTF8Encoding(false);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        static extern bool CreateSymbolicLink(string link, string target, int flags);
        static int Main()
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preset-fixtures", Guid.NewGuid().ToString("N"));
            try
            {
                string folder = Path.Combine(root, "presets"); var store = new PresetStore(folder);
                Check(store.List().Count == 0 && !Directory.Exists(folder), "reading missing preset list never creates folders");
                var selected = new AppSettings { Services = new List<string> { "youtube", "discord" }, Mode = 1,
                    Strategy = "fake-disorder", DiscordStrategy = "sni-split", Quic = true, DiscordVoice = false,
                    TelegramNative = true, UsePublicLists = true, UseCustomDomains = false,
                    CustomDomains = "my-custom.example.org\nsecond.example.org", Endpoint = "203.0.113.20", Port = 8443,
                    Appearance = "dark", SelectedPresetId = "" };
                for (int i = 0; i < DomainSelection.MaximumRules; i++) selected.DisabledDomains.Add("disabled" + i.ToString("D4") + ".example.org");
                string id = store.Save("  Дом / основной  ", selected);
                Guid parsed;
                Check(Guid.TryParseExact(id, "N", out parsed) && parsed.ToString("N") == id, "native GUID ID not derived from display name");
                Check(selected.SelectedPresetId == "", "save does not change caller's active preset");
                Check(store.List().Count == 1 && store.List()[0].Id == id && store.List()[0].Name == "Дом / основной", "trimmed named preset lists without using name as path");
                AppSettings loaded = store.Load(id);
                Check(loaded.SelectedPresetId == id && loaded.Services.SequenceEqual(selected.Services), "preset ID and service categories restored");
                Check(loaded.Mode == selected.Mode && loaded.Strategy == selected.Strategy && loaded.DiscordStrategy == selected.DiscordStrategy, "connection and both methods restored");
                Check(loaded.Quic == selected.Quic && loaded.DiscordVoice == selected.DiscordVoice && loaded.TelegramNative == selected.TelegramNative, "all protocol switches restored");
                Check(loaded.UsePublicLists && !loaded.UseCustomDomains && loaded.CustomDomains == "my-custom.example.org" + Environment.NewLine + "second.example.org", "public/custom category switches and domain text restored");
                Check(loaded.DisabledDomains.SequenceEqual(selected.DisabledDomains) && loaded.DisabledDomains.Count == DomainSelection.MaximumRules, "2048 explicit domain rules roundtrip intact");
                Check(loaded.Endpoint == "203.0.113.20" && loaded.Port == 8443 && loaded.Appearance == "dark", "endpoint, port and theme restored");
                loaded.Services.Clear(); loaded.DisabledDomains.Clear();
                Check(store.Load(id).Services.Count == 2 && store.Load(id).DisabledDomains.Count == DomainSelection.MaximumRules, "loaded data is detached from stored snapshot");
                string path = Path.Combine(folder, id + ".json"); byte[] previous = File.ReadAllBytes(path);
                selected.Strategy = "split";
                Check(store.Save("Новый вариант", selected, id) == id && store.Load(id).Strategy == "split", "explicit overwrite retains selected ID");
                string[] backups = Directory.GetFiles(folder, id + ".json.backup-*");
                Check(backups.Length == 1 && File.ReadAllBytes(backups[0]).SequenceEqual(previous), "overwrite atomically preserves preceding exact bytes");
                Check(store.List().Count == 1 && store.List()[0].Name == "Новый вариант", "backups never appear as duplicate presets");
                string second = store.Save("Новый вариант", selected);
                Check(second != id && store.List().Count == 2, "same display name does not overwrite another preset accidentally");
                int stages = Directory.GetFiles(folder, id + ".json.staged-*").Length;
                previous = File.ReadAllBytes(path);
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Expect<IOException>(() => store.Save("Не должен замениться", new AppSettings(), id), "locked primary rejects atomic replace");
                Check(File.ReadAllBytes(path).SequenceEqual(previous) && Directory.GetFiles(folder, id + ".json.staged-*").Length == stages + 1,
                    "failed replace retains primary and uncommitted stage");
                foreach (string invalid in new[] { "", "../escape", "..\\escape", "a/b", id + ".json", new string('g', 32), Guid.NewGuid().ToString("D") })
                {
                    Expect<ArgumentException>(() => store.Load(invalid), "noncanonical/traversal preset ID rejected on load");
                    if (invalid.Length > 0) Expect<ArgumentException>(() => store.Save("X", new AppSettings(), invalid), "noncanonical/traversal ID rejected on save");
                }
                int files = Directory.GetFiles(folder).Length;
                foreach (string invalid in new[] { "", " ", "bad\nname", "bad\0name", new string('x', PresetStore.MaximumNameLength + 1) })
                    Expect<ArgumentException>(() => store.Save(invalid, new AppSettings()), "empty, control or oversized name rejected before writes");
                Check(Directory.GetFiles(folder).Length == files, "invalid names leave all saved files untouched");
                Expect<ArgumentNullException>(() => store.Save("X", null), "null settings rejected");
                foreach (string invalid in new[] { "relative", @"\\server\share\presets", root + ":ads" })
                    Expect<ArgumentException>(() => new PresetStore(invalid), "relative/remote/alternate-stream root rejected");
                string broken = Guid.NewGuid().ToString("N");
                string brokenPath = Path.Combine(folder, broken + ".json");
                File.WriteAllText(brokenPath, "{broken", Utf8);
                Expect<ArgumentException>(() => store.Load(broken), "malformed JSON never becomes a preset");
                Check(store.List().Count == 2 && File.ReadAllText(brokenPath) == "{broken", "list skips corrupt files without removing or rewriting them");
                File.WriteAllText(brokenPath, "{\"Version\":99,\"Name\":\"X\",\"Settings\":{}}", Utf8);
                Expect<IOException>(() => store.Load(broken), "unsupported preset schema rejected");
                File.WriteAllText(brokenPath, "{\"Version\":1,\"Name\":\"X\",\"Settings\":null}", Utf8);
                Expect<IOException>(() => store.Load(broken), "missing settings cannot normalize into fictitious defaults");
                File.WriteAllBytes(brokenPath, new byte[] { 0x7B, 0xFF, 0x7D });
                Expect<DecoderFallbackException>(() => store.Load(broken), "invalid UTF8 rejected");
                Check(store.List().Count == 2, "invalid UTF8 entry skipped in browser");
                File.WriteAllBytes(brokenPath, new byte[AppSettings.MaximumSettingsBytes + 4097]);
                Expect<IOException>(() => store.Load(broken), "oversized file rejected before allocation/deserialization");
                string preferences = Path.Combine(root, "settings-v3.json");
                new AppSettings { SelectedPresetId = id.ToUpperInvariant() }.SaveToPath(preferences, null);
                Check(AppSettings.LoadFromPath(preferences, null).SelectedPresetId == id, "last selected preset persists in regular startup preferences");
                File.WriteAllText(preferences, "{\"SelectedPresetId\":\"../../other\",\"Strategy\":\"disorder\"}", Utf8);
                Check(AppSettings.LoadFromPath(preferences, null).SelectedPresetId == "" && AppSettings.LoadFromPath(preferences, null).Strategy == "disorder", "invalid remembered preset ID is cleared without losing other preferences");
                File.WriteAllText(preferences, "{\"Appearance\":\"light\"}", Utf8);
                Check(AppSettings.LoadFromPath(preferences, null).SelectedPresetId == "", "legacy preferences default to no preset");
                string linked = Path.Combine(root, "linked");
                if (CreateSymbolicLink(linked, folder, 3) || CreateSymbolicLink(linked, folder, 1))
                    Expect<IOException>(() => new PresetStore(linked), "reparse root rejected");
                else Console.WriteLine("SKIP: fixture symlink unavailable; no privileges changed.");
                Console.WriteLine("Preset store: " + checks + " assertions passed. Isolated fixtures: " + root);
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); Console.Error.WriteLine("Fixtures retained: " + root); return 1; }
        }
        static void Check(bool value, string text) { checks++; if (!value) throw new Exception(text); }
        static void Expect<T>(Action action, string text) where T : Exception
        { checks++; try { action(); } catch (T) { return; } throw new Exception(text); }
    }
}
