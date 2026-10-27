using System;
using System.IO;
using System.Linq;
using System.Reflection;

internal static class PublicBuildTests
{
    static int checks;
    static void Check(bool value, string description)
    { if (!value) throw new Exception(description); checks++; }
    static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1) throw new ArgumentException("Expected build directory.");
            string root = Path.GetFullPath(args[0]);
            var main = Assembly.LoadFile(Path.Combine(root, "LocalLink.exe"));
            var embeddedLauncher = ReadResource(main, "LocalLink.TelegramLauncher");
            Check(embeddedLauncher.Length > 1024 && embeddedLauncher[0] == 'M' && embeddedLauncher[1] == 'Z', "Telegram launcher embedded in the application.");
            foreach (var assembly in new[] { main, Assembly.Load(embeddedLauncher) })
            {
                Check(assembly.GetName().Version == new Version(0, 8, 3, 0), "Unexpected executable version.");
                var names = assembly.GetTypes().Select(t => t.FullName).ToArray();
                foreach (string forbidden in new[] { "LicenseGate", "LicensePolicy", "OfflineLicense", "Enrollment", "AdminHost", "OfflineAdmin" })
                    Check(!names.Any(n => n.IndexOf(forbidden, StringComparison.OrdinalIgnoreCase) >= 0), "Obsolete access-control code remains: " + forbidden);
                Check(!assembly.GetManifestResourceNames().Any(n => n.IndexOf("LicensePolicy", StringComparison.OrdinalIgnoreCase) >= 0), "Activation policy embedded.");
            }
            Check(main.GetType("LocalLink.Engine", false) != null, "Engine missing.");
            Check(main.GetType("LocalLink.AppShell", false) != null, "Single-screen shell missing.");
            Check(main.GetType("LocalLink.DiagnosticHistory", false) != null, "Persistent diagnostic history missing.");
            Check(main.GetType("LocalLink.BugReport", false) != null, "Bug report exporter missing.");
            Check(main.GetType("LocalLink.ContactLink", false) != null, "Project contact link missing.");
            Check(main.GetType("LocalLink.DependencyInstaller", false) != null, "In-app component installer missing.");
            Check(main.GetType("LocalLink.SetupPrompt", false) != null, "Component confirmation dialog missing.");
            Check(main.GetType("LocalLink.BootstrapPayload", false) != null, "Embedded Telegram preparation missing.");
            Check(main.GetType("LocalLink.PageDeck", false) == null, "Obsolete page navigation remains.");
            foreach (string resource in new[] { "LocalLink.EngineManifest", "LocalLink.RuntimeManifest", "LocalLink.DependencyManifest", "LocalLink.TelegramScript", "LocalLink.TelegramConfig" })
                Check(ReadResource(main, resource).Length > 0, "Required bootstrap resource: " + resource);
            string dependencies = System.Text.Encoding.UTF8.GetString(ReadResource(main, "LocalLink.DependencyManifest"));
            Check(dependencies.Contains("/releases/download/v0.8.2/LocalLink-0.8.2-telegram-runtime.zip"), "Runtime download targets the pinned component release.");
            string runtime = System.Text.Encoding.UTF8.GetString(ReadResource(main, "LocalLink.RuntimeManifest"));
            Check(!runtime.Contains("__pycache__") && !runtime.Contains(".pyc\""), "Developer bytecode caches excluded from the runtime manifest.");
            CheckEmbeddedPreparation(main);
            Console.WriteLine("Public build: " + checks + " assertions passed."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
    static void CheckEmbeddedPreparation(Assembly assembly)
    {
        string fixture = Path.Combine(Path.GetTempPath(), "LocalLink-payload-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            string secret = Path.Combine(fixture, "proxy-secret.dpapi");
            File.WriteAllText(secret, "SYNTHETIC-STATE-NOT-A-SECRET");
            var ensure = assembly.GetType("LocalLink.BootstrapPayload", true).GetMethod("Ensure", BindingFlags.Public | BindingFlags.Static);
            Check(ensure != null, "Embedded Telegram preparation has a fixed local-folder API.");
            ensure.Invoke(null, new object[] { fixture });
            string[] files = { "LocalLink.Telegram.exe", "bridge.py", "LocalLink.Telegram.exe.config" };
            string[] resources = { "LocalLink.TelegramLauncher", "LocalLink.TelegramScript", "LocalLink.TelegramConfig" };
            for (int i = 0; i < files.Length; i++)
                Check(File.ReadAllBytes(Path.Combine(fixture, files[i])).SequenceEqual(ReadResource(assembly, resources[i])), "Prepared file matches embedded bytes: " + files[i]);
            var dates = files.Select(name => File.GetLastWriteTimeUtc(Path.Combine(fixture, name))).ToArray();
            ensure.Invoke(null, new object[] { fixture });
            for (int i = 0; i < files.Length; i++)
                Check(File.GetLastWriteTimeUtc(Path.Combine(fixture, files[i])) == dates[i], "Already prepared file is not rewritten: " + files[i]);
            Check(Directory.GetFiles(fixture, "*.previous-*").Length == 0, "Unchanged preparation creates no backups.");
            File.WriteAllText(Path.Combine(fixture, "bridge.py"), "SYNTHETIC-CORRUPTION");
            ensure.Invoke(null, new object[] { fixture });
            Check(File.ReadAllBytes(Path.Combine(fixture, "bridge.py")).SequenceEqual(ReadResource(assembly, "LocalLink.TelegramScript")), "Corrupt prepared script is repaired from embedded bytes.");
            Check(Directory.GetFiles(fixture, "bridge.py.previous-*").Length == 1, "Replaced fixture script is retained for rollback.");
            Check(File.ReadAllText(secret) == "SYNTHETIC-STATE-NOT-A-SECRET", "Existing Telegram state remains unchanged.");
            Check(Directory.GetDirectories(fixture).Length == 0, "Embedded preparation creates no runtime or unrelated directories.");
        }
        finally
        {
            // Only files in this newly created synthetic directory; no recursive delete.
            foreach (string file in Directory.GetFiles(fixture)) File.Delete(file);
            Directory.Delete(fixture, false);
        }
    }
    static byte[] ReadResource(Assembly assembly, string name)
    {
        using (var stream = assembly.GetManifestResourceStream(name))
        {
            Check(stream != null && stream.Length > 0 && stream.Length < 1048576, "Embedded resource is present and bounded: " + name);
            using (var memory = new MemoryStream()) { stream.CopyTo(memory); return memory.ToArray(); }
        }
    }
}
