using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LocalLink
{
    internal static class DomainSelectionTests
    {
        static int assertions;
        static void Check(bool value, string text) { assertions++; if (!value) throw new Exception(text); }
        static void Expect<T>(Action action, string text) where T : Exception
        { assertions++; try { action(); } catch (T) { return; } throw new Exception(text); }
        static int Main(string[] args)
        {
            try
            {
                if (args.Length == 1 && args[0] == "--isolated") return Run();
                string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "offline-fixtures", "domains-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                var setup = new AppDomainSetup { ApplicationBase = folder, ConfigurationFile = Assembly.GetExecutingAssembly().Location + ".config" };
                var isolated = AppDomain.CreateDomain("domain-selection", null, setup);
                try { return isolated.ExecuteAssembly(Assembly.GetExecutingAssembly().Location, new[] { "--isolated" }); }
                finally { AppDomain.Unload(isolated); }
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        static int Run()
        {
            var settings = new AppSettings { UsePublicLists = true, CustomDomains = "mine.org\nsub.mine.org" };
            Check(DomainSelection.Exclusions(settings).Length == 0, "existing defaults preserve coverage");
            Check(DomainSelection.Categories().Any(x => x.Id == "public") && DomainSelection.Categories().Any(x => x.Id == "custom"), "explicit honest categories");
            settings.Services.Remove("discord");
            string[] excluded = DomainSelection.Exclusions(settings);
            Check(excluded.Contains("discord.com") && excluded.Contains("discord-attachments-uploads-prd.storage.googleapis.com"), "disabled category excludes every service root despite public list");
            Check(DomainSelection.IsExcluded("cdn.discord.com", excluded), "suffix exclusions include descendants");
            Check(!DomainSelection.IsExcluded("notdiscord.com", excluded) && !DomainSelection.IsExcluded("discord.com.evil.org", excluded), "suffix boundaries reject lookalikes");
            settings.Services.Add("discord");
            DomainSelection.SetEnabled(settings, "MINE.ORG.", false);
            Check(settings.DisabledDomains.SequenceEqual(new[] { "mine.org" }), "user rule normalized");
            Check(DomainSelection.IsExcluded("a.mine.org", DomainSelection.Exclusions(settings)), "user parent exclusion inherited");
            Expect<InvalidOperationException>(() => DomainSelection.SetEnabled(settings, "a.mine.org", true), "enabling child cannot silently undo broader exclusion");
            DomainSelection.SetEnabled(settings, "mine.org", true);
            Check(settings.DisabledDomains.Count == 0, "explicit rule restored");
            settings.UseCustomDomains = false;
            Check(DomainSelection.Exclusions(settings).SequenceEqual(new[] { "mine.org" }), "disabled custom category compacted");
            settings.UseCustomDomains = true;

            string lists = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lists");
            var snapshot = PublicLists.SaveSnapshot(lists, new[] { "alpha.org", "bravo.org", "cdn.discord.com", "child.mine.org", "discord.com", "mine.org", "youtube.com" }, new string[0], 0, CancellationToken.None);
            string path = Path.Combine(lists, snapshot.FileName);
            byte[] pointer = File.ReadAllBytes(Path.Combine(lists, "current.json"));
            var page = DomainSelection.Search(settings, path, "", "", 0, 2, CancellationToken.None);
            Check(page.Rows.Count == 2 && page.HasMore, "bounded page with next-page state");
            var next = DomainSelection.Search(settings, path, "", "", 2, 2, CancellationToken.None);
            Check(!page.Rows.Any(p => next.Rows.Any(n => p.Domain == n.Domain)), "stable nonoverlapping paging");
            var matching = DomainSelection.Search(settings, path, "discord", "CDN.", 0, 100, CancellationToken.None);
            Check(matching.Rows.Count == 1 && matching.Rows[0].Domain == "cdn.discord.com", "public subdomains classified by actual known root");
            var publicRows = DomainSelection.Search(settings, path, "public", "", 0, 200, CancellationToken.None);
            Check(publicRows.Rows.Count == 2 && publicRows.Rows.All(x => x.Domain == "alpha.org" || x.Domain == "bravo.org"), "unclassified public entries not invented topical categories");
            var customRows = DomainSelection.Search(settings, path, "custom", "", 0, 200, CancellationToken.None);
            Check(customRows.Rows.Count == 3 && customRows.Rows.Any(x => x.Domain == "child.mine.org"), "custom descendants categorized and deduplicated");
            DomainSelection.SetEnabled(settings, "mine.org", false);
            var child = DomainSelection.Search(settings, path, "", "child.mine.org", 0, 200, CancellationToken.None).Rows.Single();
            Check(!child.Enabled && child.InheritedExclusion, "UI exposes inherited disabled status");
            settings.Services.Remove("discord");
            matching = DomainSelection.Search(settings, path, "discord", "cdn.", 0, 200, CancellationToken.None);
            Check(!matching.Rows[0].Enabled && !matching.Rows[0].CategoryEnabled, "off category cannot appear enabled from public fallback");
            Expect<ArgumentOutOfRangeException>(() => DomainSelection.Search(settings, path, "", "", 0, 201, CancellationToken.None), "UI cannot request million-row page");
            Expect<ArgumentException>(() => DomainSelection.Search(settings, path, "fake", "", 0, 20, CancellationToken.None), "unknown category rejected");
            Expect<OperationCanceledException>(() => DomainSelection.Search(settings, path, "", "", 0, 20, new CancellationToken(true)), "pre-cancelled search stops before file read");

            string rulePath = DomainSelection.WriteExclusions(settings, lists, CancellationToken.None);
            Check(File.ReadAllBytes(Path.Combine(lists, "current.json")).SequenceEqual(pointer), "exclusion snapshot never replaces public pointer");
            Check(new JavaScriptSerializer().Deserialize<ListSnapshot>(File.ReadAllText(Path.Combine(lists, "current.json"))).FileName == snapshot.FileName,
                "isolated public active pointer unchanged without reading user's active snapshot");
            using (var verified = PublicLists.OpenVerified(rulePath))
            {
                Check(verified.Length > 0, "exclusions pass engine ownership/passport verification");
                Check(DomainSelection.WriteExclusions(settings, lists, CancellationToken.None) == rulePath, "unchanged rules reuse immutable snapshot while held open");
            }
            settings.DisabledDomains.Add("other.org");
            string changed = DomainSelection.WriteExclusions(settings, lists, CancellationToken.None);
            Check(changed != rulePath && File.Exists(rulePath), "new selection preserves old engine snapshot");
            var tamperSettings = new AppSettings { DisabledDomains = new List<string> { "test-one.org" } };
            string tampered = DomainSelection.WriteExclusions(tamperSettings, lists, CancellationToken.None);
            File.WriteAllText(tampered, "test-two.org\n", new UTF8Encoding(false));
            var forged = new JavaScriptSerializer().Deserialize<ListSnapshot>(File.ReadAllText(tampered + ".json"));
            using (var sha = SHA256.Create())
                forged.Sha256 = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(tampered))).Replace("-", "").ToLowerInvariant();
            File.WriteAllText(tampered + ".json", new JavaScriptSerializer().Serialize(forged), new UTF8Encoding(false));
            Expect<IOException>(() => DomainSelection.WriteExclusions(tamperSettings, lists, CancellationToken.None), "existing exclusion snapshot must match requested rules, not merely its own passport");
            Expect<IOException>(() => DomainSelection.WriteExclusions(settings, Path.Combine(lists, "foreign"), CancellationToken.None), "foreign folder rejected");
            Expect<OperationCanceledException>(() => DomainSelection.WriteExclusions(settings, lists, new CancellationToken(true)), "cancelled exclusions stop before writes");
            string preferences = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings-fixture.json");
            settings.SaveToPath(preferences, null);
            var restored = AppSettings.LoadFromPath(preferences, null);
            Check(restored.DisabledDomains.SequenceEqual(settings.DisabledDomains) && restored.UseCustomDomains, "rules roundtrip in native settings serializer");
            File.WriteAllText(preferences, "{\"Services\":[\"discord\"],\"CustomDomains\":\"old.org\"}", new UTF8Encoding(false));
            restored = AppSettings.LoadFromPath(preferences, null);
            Check(restored.UseCustomDomains && restored.DisabledDomains.Count == 0 && restored.CustomDomains == "old.org", "old preferences migrate without losing custom category");
            var many = new List<string>(); for (int i = 0; i < DomainSelection.MaximumRules + 1; i++) many.Add("d" + i + ".org");
            Expect<ArgumentException>(() => DomainSelection.NormalizeDisabled(many), "rule cap enforced");
            var fullRules = new AppSettings { DisabledDomains = many.Take(DomainSelection.MaximumRules).ToList() };
            fullRules.SaveToPath(preferences, null);
            Check(AppSettings.LoadFromPath(preferences, null).DisabledDomains.Count == DomainSelection.MaximumRules, "maximum supported exclusion choices roundtrip without loss");
            LargeSearch(lists);
            Console.WriteLine("Domain selection: " + assertions + " assertions passed.");
            return 0;
        }
        static void LargeSearch(string lists)
        {
            const int total = 1700000;
            string path = Path.Combine(lists, "sites-large-fixture.lst");
            var watch = Stopwatch.StartNew();
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(false), 65536))
                for (int i = 0; i < total; i++) writer.WriteLine("site" + i.ToString("D7") + ".example.org");
            string digest;
            using (var sha = SHA256.Create())
            using (var input = File.OpenRead(path)) digest = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
            File.WriteAllText(path + ".json", new JavaScriptSerializer().Serialize(new ListSnapshot { FileName = Path.GetFileName(path), Sha256 = digest,
                DomainCount = total, SourceCount = 0, Sources = new string[0], UpdatedUtc = DateTime.UtcNow.ToString("o") }), new UTF8Encoding(false));
            Console.WriteLine("Synthetic fixture: " + total + " domains, " + new FileInfo(path).Length + " bytes, generated in " + watch.ElapsedMilliseconds + " ms.");
            watch.Restart();
            var result = DomainSelection.Search(new AppSettings { UsePublicLists = true }, path, "public", "site1699999", 0, 200, CancellationToken.None);
            Check(result.Rows.Count == 1 && result.Rows[0].Domain == "site1699999.example.org" && !result.HasMore, "search finds final domain in 1.7m source without building million UI rows");
            Console.WriteLine("Full 1.7m verification + end-of-file search: " + watch.ElapsedMilliseconds + " ms.");
            watch.Restart();
            result = DomainSelection.Search(new AppSettings { UsePublicLists = true }, path, "public", "site000", 0, 200, CancellationToken.None);
            Check(result.Rows.Count == 200 && result.HasMore, "large-source search strictly caps returned rows");
            Console.WriteLine("Verified 1.7m first page: " + watch.ElapsedMilliseconds + " ms.");
            using (var cancel = new CancellationTokenSource())
            {
                var search = Task.Run(() => DomainSelection.Search(new AppSettings { UsePublicLists = true }, path, "public", "not-present-anywhere", 0, 200, cancel.Token));
                cancel.CancelAfter(30);
                Expect<OperationCanceledException>(() => search.GetAwaiter().GetResult(), "large in-progress search can be cancelled");
            }
        }
    }
}
