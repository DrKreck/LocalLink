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
            foreach (string executable in new[] { "LocalLink.exe", "telegram/LocalLink.Telegram.exe" })
            {
                var assembly = Assembly.LoadFile(Path.Combine(root, executable));
                Check(assembly.GetName().Version == new Version(0, 8, 1, 0), "Unexpected executable version.");
                var names = assembly.GetTypes().Select(t => t.FullName).ToArray();
                foreach (string forbidden in new[] { "LicenseGate", "LicensePolicy", "OfflineLicense", "Enrollment", "AdminHost", "OfflineAdmin" })
                    Check(!names.Any(n => n.IndexOf(forbidden, StringComparison.OrdinalIgnoreCase) >= 0), "Obsolete access-control code remains: " + forbidden);
                Check(!assembly.GetManifestResourceNames().Any(n => n.IndexOf("LicensePolicy", StringComparison.OrdinalIgnoreCase) >= 0), "Activation policy embedded.");
            }
            var main = Assembly.LoadFile(Path.Combine(root, "LocalLink.exe"));
            Check(main.GetType("LocalLink.Engine", false) != null, "Engine missing.");
            Check(main.GetType("LocalLink.AppShell", false) != null, "Single-screen shell missing.");
            Check(main.GetType("LocalLink.DiagnosticHistory", false) != null, "Persistent diagnostic history missing.");
            Check(main.GetType("LocalLink.BugReport", false) != null, "Bug report exporter missing.");
            Check(main.GetType("LocalLink.ContactLink", false) != null, "Project contact link missing.");
            Check(main.GetType("LocalLink.PageDeck", false) == null, "Obsolete page navigation remains.");
            Check(main.GetManifestResourceNames().Contains("LocalLink.EngineManifest"), "Integrity manifest missing.");
            Console.WriteLine("Public build: " + checks + " assertions passed."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
}
