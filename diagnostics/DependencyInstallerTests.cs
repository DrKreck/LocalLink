using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LocalLink
{
    internal static class DependencyInstallerTests
    {
        private static int checks;
        private static string fixture;
        private static readonly byte[] EngineOne = Encoding.UTF8.GetBytes("fixture engine one"), EngineTwo = Encoding.UTF8.GetBytes("fixture library two");
        private static readonly byte[] RuntimeOne = Encoding.UTF8.GetBytes("fixture Python executable"), RuntimeTwo = Encoding.UTF8.GetBytes("fixture crypto library");
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool CreateSymbolicLink(string link, string target, int flags);
        private sealed class Capture : IProgress<DependencyProgress>
        {
            internal readonly List<DependencyProgress> Values = new List<DependencyProgress>();
            public void Report(DependencyProgress value) { Values.Add(value); }
        }
        private static string Hash(byte[] bytes)
        { using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        private static void Check(bool condition, string message)
        { checks++; if (!condition) throw new Exception(message); }
        private static void Throws<T>(Action action, string message) where T : Exception
        { bool caught = false; try { action(); } catch (T) { caught = true; } Check(caught, message); }
        private static string Folder(string name)
        { string path = Path.Combine(fixture, name); Directory.CreateDirectory(path); return path; }
        private static DependencyInstaller.LockedFile Locked(string name, byte[] bytes)
        { return new DependencyInstaller.LockedFile { Path = name, Sha256 = Hash(bytes), Size = bytes.Length, Url = "https://raw.githubusercontent.com/DrKreck/LocalLink/fixture/" + name }; }
        private static byte[] Zip(params Tuple<string, byte[]>[] entries)
        {
            using (var memory = new MemoryStream())
            {
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
                    foreach (var item in entries)
                    {
                        var entry = zip.CreateEntry(item.Item1);
                        using (var output = entry.Open()) output.Write(item.Item2, 0, item.Item2.Length);
                    }
                return memory.ToArray();
            }
        }
        private static byte[] RuntimeZip()
        { return Zip(Tuple.Create("python.exe", RuntimeOne), Tuple.Create("Lib/site-packages/crypto.pyd", RuntimeTwo)); }
        private static DependencyInstaller.InstallPlan Plan(byte[] archive)
        {
            return new DependencyInstaller.InstallPlan {
                EngineFiles = new[] { Locked("winws2.exe", EngineOne), Locked("WinDivert.dll", EngineTwo) },
                RuntimeFiles = new[] { Locked("python.exe", RuntimeOne), Locked("Lib/site-packages/crypto.pyd", RuntimeTwo) },
                RuntimeUrl = "https://github.com/DrKreck/LocalLink/releases/download/v0.8.2/LocalLink-0.8.2-telegram-runtime.zip",
                RuntimeSha256 = Hash(archive), RuntimeSize = archive.Length };
        }
        private static DependencyInstaller Installer(string root, byte[] archive, Action<Uri> beforeDownload, Action<string> onWrite)
        {
            return new DependencyInstaller(root, Plan(archive), delegate(Uri uri, string destination, long size, IProgress<DependencyProgress> progress, CancellationToken token) {
                token.ThrowIfCancellationRequested(); if (beforeDownload != null) beforeDownload(uri); token.ThrowIfCancellationRequested();
                byte[] bytes = uri.Host == "github.com" ? archive : (uri.AbsolutePath.EndsWith("winws2.exe", StringComparison.Ordinal) ? EngineOne : EngineTwo);
                File.WriteAllBytes(destination, bytes);
                if (onWrite != null) onWrite(destination);
                return Task.FromResult(0);
            });
        }
        private static void Install(DependencyInstaller value, bool telegram, IProgress<DependencyProgress> progress, CancellationToken token)
        { value.InstallAsync(telegram, progress, token).GetAwaiter().GetResult(); }
        public static int Main(string[] args)
        {
            fixture = Path.Combine(Path.GetTempPath(), "LocalLink-dependency-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixture);
            try
            {
                MissingAndSuccess(); ReuseAndRepair(); CacheIdentity(); CrossVersionReuse(); ReuseSafety(); CancellationAndRetry(); CorruptAndFailedDownloads(); ArchiveSafety(); ManifestSafety(); ConcurrentAndReparse();
                var embedded = new DependencyInstaller(Folder("embedded"));
                Check(!embedded.Inspect(true).Ready, "Embedded manifests load without missing resource and identify empty fixture");
                if (args.Length == 3 && args[0] == "--verify-local") VerifyRealArchive(args[1], args[2]);
                else if (args.Length == 1 && args[0] == "--verify-engine-download")
                {
                    var live = new DependencyInstaller(Folder("live-engine"));
                    Install(live, false, null, CancellationToken.None);
                    Check(live.Inspect(false).Ready, "Native HTTPS engine downloader verified every pinned file without execution");
                    Console.WriteLine("Live HTTPS engine download and verification passed; no filter or driver was launched.");
                }
                else if (args.Length != 0) throw new ArgumentException("Expected --verify-local <runtime ZIP> <engine folder> or --verify-engine-download.");
                Check(new DependencyProgress("x", -5).Percent == 0 && new DependencyProgress("x", 110).Percent == 100, "Progress bounded");
                Console.WriteLine("Dependency installer: " + checks + " checks passed; no downloaded executable or filter was launched.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
            finally
            {
                string resolved = Path.GetFullPath(fixture);
                string prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\', '/') + Path.DirectorySeparatorChar + "LocalLink-dependency-tests-";
                if (resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    try { Directory.Delete(resolved, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        private static void VerifyRealArchive(string runtimeZip, string engineFolder)
        {
            var plan = DependencyInstaller.LoadPlan();
            string root = Folder("real-asset"); int downloads = 0;
            var value = new DependencyInstaller(root, plan, delegate(Uri uri, string destination, long size, IProgress<DependencyProgress> progress, CancellationToken token) {
                token.ThrowIfCancellationRequested(); downloads++;
                if (uri.AbsoluteUri == plan.RuntimeUrl) File.Copy(runtimeZip, destination, false);
                else
                {
                    var entry = plan.EngineFiles.Single(item => item.Url == uri.AbsoluteUri);
                    File.Copy(Path.Combine(engineFolder, entry.Path.Replace('/', Path.DirectorySeparatorChar)), destination, false);
                }
                return Task.FromResult(0);
            });
            Install(value, true, null, CancellationToken.None);
            Check(value.Inspect(true).Ready, "Real pinned asset and every engine/runtime file installed and rehashed");
            Check(downloads == plan.EngineFiles.Length + 1, "Real asset installation downloaded exactly expected files");
            Check(Directory.GetFiles(Path.Combine(root, "telegram", "runtime"), "*.pyc", SearchOption.AllDirectories).Length == 0, "Real runtime contains no bytecode caches");
            Console.WriteLine("Verified local real asset: " + plan.EngineFiles.Length + " engine and " + plan.RuntimeFiles.Length + " runtime files; no binary execution.");
        }
        private static void MissingAndSuccess()
        {
            string root = Folder("success"); byte[] zip = RuntimeZip(); int downloads = 0;
            var value = Installer(root, zip, delegate { downloads++; }, null);
            var status = value.Inspect(true);
            Check(!status.Ready && !status.EngineReady && !status.TelegramReady && status.MissingComponents.Length == 2, "Empty folder identifies both components");
            var progress = new Capture(); Install(value, true, progress, CancellationToken.None);
            status = value.Inspect(true);
            Check(status.Ready && status.EngineReady && status.TelegramReady && status.MissingComponents.Length == 0, "Fresh installation is complete");
            Check(downloads == 3, "Exactly two engine files and one runtime archive fetched");
            Check(progress.Values.Count >= 5 && progress.Values.Last().Percent == 100, "Progress completes");
            Check(Directory.GetDirectories(root, ".install-*").Length == 0, "Successful staging cleaned");
            Check(File.ReadAllBytes(Path.Combine(root, "engine", "winws2.exe")).SequenceEqual(EngineOne), "Engine bytes preserved");
            Check(File.ReadAllBytes(Path.Combine(root, "telegram", "runtime", "Lib", "site-packages", "crypto.pyd")).SequenceEqual(RuntimeTwo), "Nested runtime installed");
            Check(!File.Exists(Path.Combine(root, "telegram", "runtime", "runtime-manifest.json")), "Unneeded archive metadata is not installed");
            string engineOnly = Folder("engine-only"); var only = Installer(engineOnly, zip, null, null); Install(only, false, null, CancellationToken.None);
            Check(only.Inspect(false).Ready && !only.Inspect(true).Ready, "Engine-only preparation does not require runtime");
            Check(!Directory.Exists(Path.Combine(engineOnly, "telegram")), "Engine-only installation does not create Telegram folder");
        }
        private static void ReuseAndRepair()
        {
            string root = Folder("repair"); byte[] zip = RuntimeZip(); int downloads = 0;
            var value = Installer(root, zip, delegate { downloads++; }, null); Install(value, true, null, CancellationToken.None);
            downloads = 0; Install(value, true, null, CancellationToken.None);
            Check(downloads == 0, "Valid installed components reused without network");
            string corrupt = Path.Combine(root, "engine", "winws2.exe"); File.WriteAllText(corrupt, "broken");
            Check(!value.Inspect(true).EngineReady && value.Inspect(true).TelegramReady, "Engine corruption identified separately");
            Install(value, true, null, CancellationToken.None);
            Check(downloads == 1 && value.Inspect(true).Ready, "Only corrupt engine file redownloaded; valid runtime retained");
            Check(Directory.GetDirectories(root, "engine.previous-*").Length == 1, "Previous incomplete engine retained for rollback");
            File.Delete(Path.Combine(root, "telegram", "runtime", "python.exe")); downloads = 0;
            Install(value, true, null, CancellationToken.None);
            Check(downloads == 1 && value.Inspect(true).Ready, "Missing runtime repaired from one archive");
        }
        private static void CacheIdentity()
        {
            byte[] zip = RuntimeZip(); var original = Plan(zip); var changed = Plan(zip);
            string identity = DependencyInstaller.ContentFingerprint(original);
            Check(identity.Length == 24 && identity.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f'), "Cache identity is a bounded hexadecimal content key");
            changed.EngineFiles[0].Url = "https://raw.githubusercontent.com/DrKreck/LocalLink/new-release/winws2.exe";
            changed.RuntimeUrl = "https://github.com/DrKreck/LocalLink/releases/download/v99.0.0/new-runtime.zip";
            changed.RuntimeSha256 = Hash(Encoding.UTF8.GetBytes("different archive packaging")); changed.RuntimeSize++;
            Array.Reverse(changed.EngineFiles); Array.Reverse(changed.RuntimeFiles);
            Check(DependencyInstaller.ContentFingerprint(changed) == identity, "GUI release URLs, archive packaging and manifest ordering do not invalidate identical component bytes");
            changed.EngineFiles[0].Sha256 = Hash(Encoding.UTF8.GetBytes("changed engine bytes"));
            Check(DependencyInstaller.ContentFingerprint(changed) != identity, "Changed locked component content gets a separate cache identity");
            changed = Plan(zip); changed.RuntimeFiles[0].Size++;
            Check(DependencyInstaller.ContentFingerprint(changed) != identity, "Changed runtime component size changes identity");
            changed = Plan(zip); changed.RuntimeFiles[0].Path = "other-python.exe";
            Check(DependencyInstaller.ContentFingerprint(changed) != identity, "Changed component relative path changes identity");
        }
        private static void Reuse(DependencyInstaller value, bool telegram, IEnumerable<string> roots, CancellationToken token)
        { value.ReuseInstalledAsync(telegram, roots, token).GetAwaiter().GetResult(); }
        private static void CrossVersionReuse()
        {
            byte[] zip = RuntimeZip(); string legacy = Folder("legacy-version");
            var installed = Installer(legacy, zip, null, null); Install(installed, true, null, CancellationToken.None);
            var original = Directory.GetFiles(legacy, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => Hash(File.ReadAllBytes(p)));
            int downloads = 0; string fresh = Folder("content-cache");
            var value = Installer(fresh, zip, delegate { downloads++; }, null);
            Reuse(value, true, new[] { fresh, legacy, legacy }, CancellationToken.None);
            Check(value.Inspect(true).Ready && downloads == 0, "New application cache imports verified older components without downloading");
            Reuse(value, true, new[] { legacy }, CancellationToken.None); Install(value, true, null, CancellationToken.None);
            Check(downloads == 0, "Repeated startup and install checks reuse ready cache with zero downloads");
            Check(Directory.GetDirectories(fresh, ".install-*").Length == 0 && Directory.GetDirectories(fresh, "*.previous-*").Length == 0, "Ready cache checks make no staging or rollback revisions");
            Check(original.All(p => File.Exists(p.Key) && Hash(File.ReadAllBytes(p.Key)) == p.Value), "Reuse leaves every legacy file byte-identical and does not remove it");

            string partial = Folder("partial-old"); Directory.CreateDirectory(Path.Combine(partial, "engine"));
            File.WriteAllBytes(Path.Combine(partial, "engine", "WinDivert.dll"), EngineTwo);
            File.WriteAllText(Path.Combine(partial, "engine", "winws2.exe"), "corrupt old engine");
            string repaired = Folder("partial-new"); downloads = 0; value = Installer(repaired, zip, delegate { downloads++; }, null);
            Reuse(value, false, new[] { partial }, CancellationToken.None);
            Check(!value.Inspect(false).Ready && File.ReadAllBytes(Path.Combine(repaired, "engine", "WinDivert.dll")).SequenceEqual(EngineTwo), "Valid individual legacy engine files are reused even when the older group is incomplete");
            Check(!File.Exists(Path.Combine(repaired, "engine", "winws2.exe")), "Hash-mismatched legacy component is not copied");
            Install(value, false, null, CancellationToken.None);
            Check(downloads == 1 && value.Inspect(false).Ready, "Only the missing engine file is downloaded after partial reuse");
            Check(File.ReadAllText(Path.Combine(partial, "engine", "winws2.exe")) == "corrupt old engine", "Legacy repair does not edit the older installation");

            string complementary = Folder("complementary-new");
            Directory.CreateDirectory(Path.Combine(complementary, "engine"));
            File.WriteAllBytes(Path.Combine(complementary, "engine", "winws2.exe"), EngineOne);
            downloads = 0; value = Installer(complementary, zip, delegate { downloads++; }, null);
            Reuse(value, false, new[] { partial }, CancellationToken.None);
            Check(value.Inspect(false).Ready && downloads == 0, "Migration preserves valid current files while importing complementary old files");
            Check(!Directory.Exists(Path.Combine(complementary, "telegram")), "Engine-only reuse does not create a Telegram runtime");

            string changedRoot = Folder("changed-plan-cache"); var changedPlan = Plan(zip);
            byte[] changedEngine = Encoding.UTF8.GetBytes("changed expected engine bytes");
            changedPlan.EngineFiles[0] = Locked("winws2.exe", changedEngine); downloads = 0;
            value = new DependencyInstaller(changedRoot, changedPlan, delegate(Uri uri, string destination, long size, IProgress<DependencyProgress> progress, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); downloads++;
                if (!uri.AbsolutePath.EndsWith("winws2.exe", StringComparison.Ordinal)) throw new Exception("Unchanged components must not be downloaded");
                File.WriteAllBytes(destination, changedEngine); return Task.FromResult(0);
            });
            Reuse(value, true, new[] { legacy }, CancellationToken.None);
            Check(!value.Inspect(true).EngineReady && value.Inspect(true).TelegramReady && downloads == 0, "Changed plan does not reuse mismatched engine but retains unchanged verified runtime without network");
            Check(!File.Exists(Path.Combine(changedRoot, "engine", "winws2.exe")), "Changed expected hash cannot be bypassed by a familiar filename");
            Install(value, true, null, CancellationToken.None);
            Check(value.Inspect(true).Ready && downloads == 1, "Component update fetches only changed engine bytes, not unchanged engine files or runtime archive");
        }
        private static void ReuseSafety()
        {
            byte[] zip = RuntimeZip(); string legacy = Folder("safe-legacy");
            Install(Installer(legacy, zip, null, null), true, null, CancellationToken.None);
            string fresh = Folder("safe-fresh"); var value = Installer(fresh, zip, null, null);
            using (var stop = new CancellationTokenSource())
            {
                stop.Cancel();
                Throws<OperationCanceledException>(delegate { Reuse(value, true, new[] { legacy }, stop.Token); }, "Pre-cancelled reuse is rejected");
            }
            Check(Directory.GetFileSystemEntries(fresh).Length == 0, "Pre-cancelled reuse creates no installation files");
            using (var occupied = new FileStream(Path.Combine(fresh, ".install.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                Throws<IOException>(delegate { Reuse(value, true, new[] { legacy }, CancellationToken.None); }, "Reuse respects the installation lock");
            Check(!Directory.Exists(Path.Combine(fresh, "engine")), "Locked reuse does not commit a component group");
            string link = Path.Combine(fixture, "reuse-source-link");
            if (CreateSymbolicLink(link, legacy, 3))
            {
                try
                {
                    Reuse(value, true, new[] { link }, CancellationToken.None);
                    Check(!value.Inspect(true).Ready && !Directory.Exists(Path.Combine(fresh, "engine")), "Reparse-point candidate is ignored without copying from its target");
                    var linked = Installer(link, zip, null, null);
                    Throws<IOException>(delegate { Reuse(linked, true, new[] { legacy }, CancellationToken.None); }, "Reparse-point destination is rejected");
                }
                finally { Directory.Delete(link, false); }
            }
            else Console.WriteLine("SKIP optional reuse symbolic-link fixture: Windows did not grant link creation (" + Marshal.GetLastWin32Error() + ").");
        }
        private static void CancellationAndRetry()
        {
            byte[] zip = RuntimeZip(); string root = Folder("cancel"); var stop = new CancellationTokenSource(); stop.Cancel(); int calls = 0;
            var value = Installer(root, zip, delegate { calls++; }, null);
            Throws<OperationCanceledException>(delegate { Install(value, true, null, stop.Token); }, "Already cancelled install rejected");
            Check(calls == 0 && Directory.GetFileSystemEntries(root).Length == 0, "Pre-cancel creates nothing");
            stop.Dispose(); stop = new CancellationTokenSource();
            var during = Installer(root, zip, delegate { stop.Cancel(); }, null);
            Throws<OperationCanceledException>(delegate { Install(during, true, null, stop.Token); }, "Download cancellation propagated");
            Check(!Directory.Exists(Path.Combine(root, "engine")) && Directory.GetDirectories(root, ".install-*").Length == 0, "Cancelled engine stage never installed");
            Install(value, true, null, CancellationToken.None); Check(value.Inspect(true).Ready, "Retry after cancellation succeeds");
            string installed = Path.Combine(root, "telegram", "runtime", "python.exe"); File.WriteAllText(installed, "broken-runtime");
            stop.Dispose(); stop = new CancellationTokenSource();
            var runtimeCancel = Installer(root, zip, delegate(Uri uri) { if (uri.Host == "github.com") stop.Cancel(); }, null);
            Throws<OperationCanceledException>(delegate { Install(runtimeCancel, true, null, stop.Token); }, "Runtime download cancellation propagated");
            Check(File.ReadAllText(installed) == "broken-runtime" && runtimeCancel.Inspect(false).Ready, "Runtime cancellation preserves current files and good engine");
            stop.Dispose();
        }
        private static void CorruptAndFailedDownloads()
        {
            string root = Folder("corrupt"); byte[] zip = RuntimeZip();
            var value = Installer(root, zip, null, delegate(string path) { File.WriteAllText(path, "wrong bytes"); });
            Throws<IOException>(delegate { Install(value, true, null, CancellationToken.None); }, "Checksum mismatch fails closed");
            Check(!Directory.Exists(Path.Combine(root, "engine")), "Mismatched engine never installed");
            var failed = Installer(root, zip, delegate { throw new IOException("fixture network failure"); }, null);
            Throws<IOException>(delegate { Install(failed, true, null, CancellationToken.None); }, "Network failure propagated");
            Check(Directory.GetDirectories(root, ".install-*").Length == 0, "Failed staging cleaned");
            var retry = Installer(root, zip, null, null); Install(retry, false, null, CancellationToken.None);
            var badRuntime = Installer(root, zip, null, delegate(string path) { if (path.EndsWith(".zip", StringComparison.Ordinal)) File.WriteAllText(path, "bad zip"); });
            Throws<IOException>(delegate { Install(badRuntime, true, null, CancellationToken.None); }, "Runtime archive checksum mismatch fails");
            Check(badRuntime.Inspect(false).Ready && !Directory.Exists(Path.Combine(root, "telegram")), "Bad runtime leaves good engine untouched");
        }
        private static void ArchiveSafety()
        {
            var cases = new[] {
                Zip(Tuple.Create("../escape.exe", RuntimeOne)),
                Zip(Tuple.Create("/escape.exe", RuntimeOne)),
                Zip(Tuple.Create("C:/escape.exe", RuntimeOne)),
                Zip(Tuple.Create("python.exe", RuntimeOne), Tuple.Create("python.exe", RuntimeOne)),
                Zip(Tuple.Create("python.exe", RuntimeOne), Tuple.Create("unexpected.exe", RuntimeTwo)),
                Zip(Tuple.Create("python.exe", RuntimeOne)),
                Zip(Tuple.Create("python.exe", RuntimeTwo), Tuple.Create("Lib/site-packages/crypto.pyd", RuntimeTwo)),
                Zip(Tuple.Create("CON", RuntimeOne)),
                Zip(Tuple.Create("dir\\escape.exe", RuntimeOne))
            };
            for (int i = 0; i < cases.Length; i++)
            {
                string root = Folder("archive-" + i); var value = Installer(root, cases[i], null, null);
                Throws<IOException>(delegate { Install(value, true, null, CancellationToken.None); }, "Unsafe/incomplete runtime archive rejected " + i);
                Check(!Directory.Exists(Path.Combine(root, "telegram", "runtime")), "Unsafe runtime archive not committed " + i);
                Check(!File.Exists(Path.Combine(root, "escape.exe")) && !File.Exists(Path.Combine(fixture, "escape.exe")), "Traversal wrote no escaped files " + i);
            }
            byte[] metadata = Zip(Tuple.Create("python.exe", RuntimeOne), Tuple.Create("Lib/site-packages/crypto.pyd", RuntimeTwo), Tuple.Create("runtime-manifest.json", Encoding.UTF8.GetBytes("{}")));
            var accepted = Installer(Folder("metadata"), metadata, null, null); Install(accepted, true, null, CancellationToken.None);
            Check(accepted.Inspect(true).Ready, "Known archive metadata safely ignored");
        }
        private static void ManifestSafety()
        {
            byte[] zip = RuntimeZip(); var value = Plan(zip); value.EngineFiles[0].Path = "../bad";
            Throws<IOException>(delegate { new DependencyInstaller(Folder("bad-plan"), value, null); }, "Unsafe manifest path rejected");
            value = Plan(zip); value.EngineFiles[0].Url = "http://raw.githubusercontent.com/unsafe";
            Throws<IOException>(delegate { new DependencyInstaller(Folder("http-plan"), value, null); }, "Plain HTTP manifest rejected");
            value = Plan(zip); value.RuntimeUrl = "https://example.org/runtime.zip";
            Throws<IOException>(delegate { new DependencyInstaller(Folder("host-plan"), value, null); }, "Unapproved download host rejected");
            value = Plan(zip); value.RuntimeSize = 100000000;
            Throws<IOException>(delegate { new DependencyInstaller(Folder("size-plan"), value, null); }, "Oversize archive manifest rejected");
            value = Plan(zip); value.EngineFiles[1].Path = "WINWS2.EXE";
            Throws<IOException>(delegate { new DependencyInstaller(Folder("duplicate-plan"), value, null); }, "Case-insensitive duplicate manifest rejected");
        }
        private static void ConcurrentAndReparse()
        {
            string root = Folder("locked"); var value = Installer(root, RuntimeZip(), null, null);
            using (var occupied = new FileStream(Path.Combine(root, ".install.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                Throws<IOException>(delegate { Install(value, true, null, CancellationToken.None); }, "Concurrent preparation is rejected");
            string target = Folder("link-target"), link = Path.Combine(fixture, "link");
            if (CreateSymbolicLink(link, target, 3))
            {
                try
                {
                    var linked = Installer(link, RuntimeZip(), null, null);
                    Throws<IOException>(delegate { linked.Inspect(true); }, "Reparse install root rejected");
                    Throws<IOException>(delegate { Install(linked, true, null, CancellationToken.None); }, "Reparse installation rejected before writes");
                    Check(Directory.GetFileSystemEntries(target).Length == 0, "Link target untouched");
                }
                finally { Directory.Delete(link, false); }
            }
            else Console.WriteLine("SKIP optional symbolic-link fixture: Windows did not grant unprivileged link creation (" + Marshal.GetLastWin32Error() + ").");
        }
    }
}
