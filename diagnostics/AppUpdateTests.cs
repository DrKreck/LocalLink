using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LocalLink
{
    internal static class AppUpdateTests
    {
        static int checks;
        static string root, version;
        static int Main()
        {
            root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app-update-fixtures", Guid.NewGuid().ToString("N"));
            version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            try
            {
                Directory.CreateDirectory(root);
                ReleaseValidation(); SignatureValidation(); ArchiveValidation(); TransportAndStaging(); TransactionAndRollback(); ReplacementRetryValidation(); HealthyMetadataFailure(); PlanValidation();
                Console.WriteLine("PASS App updates: " + checks + " checks"); return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        static void ReleaseValidation()
        {
            var current = new Version(0, 0, 1);
            var json = Release(version, 55, new string('a', 64));
            AppUpdateOffer offer = AppUpdateClient.ParseRelease(json, current);
            Check(offer != null && offer.Version == version && offer.Bytes == 55 && offer.Sha256 == new string('a', 64), "stable release offers exact Windows asset");
            Check(!offer.PublisherSignatureVerified, "GitHub digest is not mislabeled as publisher signature");
            Check(AppUpdateClient.ParseRelease(json, new Version(999, 0, 0)) == null, "older releases never offered");
            Check(AppUpdateClient.ParseRelease(json, AppUpdateClient.ParseVersion(version)) == null, "current release never reinstalled");
            var value = AppUpdateFiles.Object(json, 100000); value["draft"] = true;
            Check(AppUpdateClient.ParseRelease(AppUpdateFiles.Json(value), current) == null, "draft never offered");
            value["draft"] = false; value["prerelease"] = true;
            Check(AppUpdateClient.ParseRelease(AppUpdateFiles.Json(value), current) == null, "prerelease never offered");
            foreach (string tag in new[] { "v1.2.3-beta", "v01.2.3", "1.2.3", "v1.2.3/evil", "v1.2.3.4", "v655360.0.0" })
            { value = AppUpdateFiles.Object(json, 100000); value["tag_name"] = tag; Expect<IOException>(() => AppUpdateClient.ParseRelease(AppUpdateFiles.Json(value), current), "unsafe tag rejected"); }
            foreach (string name in new[] { "http://github.com/DrKreck/LocalLink/releases/tag/v" + version, "https://github.com/other/LocalLink/releases/tag/v" + version })
            { value = AppUpdateFiles.Object(json, 100000); value["html_url"] = name; Expect<IOException>(() => AppUpdateClient.ParseRelease(AppUpdateFiles.Json(value), current), "foreign release URL rejected"); }
            foreach (string digest in new[] { "", "md5:" + new string('a', 32), "sha256:" + new string('g', 64), "sha256:" + new string('a', 63) })
            {
                value = AppUpdateFiles.Object(json, 100000); var asset = (Dictionary<string, object>)((object[])value["assets"])[0]; asset["digest"] = digest;
                Expect<IOException>(() => AppUpdateClient.ParseRelease(AppUpdateFiles.Json(value), current), "missing/invalid trusted digest rejected");
            }
            value = AppUpdateFiles.Object(json, 100000); value["assets"] = new object[0];
            Expect<IOException>(() => AppUpdateClient.ParseRelease(AppUpdateFiles.Json(value), current), "missing app archive rejected");
            value = AppUpdateFiles.Object(json, 100000); var first = ((object[])value["assets"])[0]; value["assets"] = new[] { first, first };
            Expect<IOException>(() => AppUpdateClient.ParseRelease(AppUpdateFiles.Json(value), current), "duplicate package rejected");
            value = AppUpdateFiles.Object(json, 100000); var original = (Dictionary<string, object>)((object[])value["assets"])[0]; original["browser_download_url"] = "https://evil.test/file.zip";
            Expect<IOException>(() => AppUpdateClient.ParseRelease(AppUpdateFiles.Json(value), current), "foreign asset source rejected");
            foreach (string input in new[] { "", "1.0", "1.0.0.1", "-1.0.0", "01.0.0", "1.0.0\n", "v1.0.0" })
                Expect<IOException>(() => AppUpdateClient.ParseVersion(input), "noncanonical app version rejected");
            foreach (string uri in new[] { "http://github.com/DrKreck/LocalLink/releases/download/v1.0.0/a", "https://evil.test/a", "https://github.com.evil.test/a", "https://github.com:8443/DrKreck/LocalLink/releases/download/v1.0.0/a", "https://user:password@github.com/DrKreck/LocalLink/releases/download/v1.0.0/a", "https://raw.githubusercontent.com/a", "https://127.0.0.1/a" })
                Check(!AppUpdateClient.AllowedUri(new Uri(uri), false), "unsafe asset redirect rejected");
            Check(AppUpdateClient.AllowedUri(new Uri(offer.PackageUrl), false), "repository initial download URL allowed");
            Check(AppUpdateClient.AllowedUri(new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/x"), false), "official release CDN allowed");
            Check(AppUpdateClient.AllowedUri(new Uri(AppUpdateClient.LatestReleaseApi), true), "exact latest API allowed");
            Check(!AppUpdateClient.AllowedUri(new Uri("https://api.github.com/repos/other/LocalLink/releases/latest"), true), "API cannot query other repo");
        }
        static void SignatureValidation()
        {
            var offer = AppUpdateClient.ParseRelease(Release(version, 55, new string('a', 64)), new Version(0, 0, 1));
            byte[] manifest = AppUpdateFiles.Json(new { Schema = "locallink-update-v1", Version = version, Package = "LocalLink-" + version + "-windows-x64.zip", Sha256 = offer.Sha256, Bytes = offer.Bytes });
            using (var rsa = new RSACryptoServiceProvider(2048))
            {
                rsa.PersistKeyInCsp = false; string publicKey = rsa.ToXmlString(false);
                byte[] signature = AppUpdateFiles.Utf8.GetBytes(Convert.ToBase64String(rsa.SignData(manifest, CryptoConfig.MapNameToOID("SHA256"))));
                AppUpdateClient.VerifySignedManifest(offer, manifest, signature, publicKey); Check(true, "optional pinned RSA signature accepted");
                byte[] changed = (byte[])manifest.Clone(); changed[changed.Length - 2] ^= 1;
                Expect<IOException>(() => AppUpdateClient.VerifySignedManifest(offer, changed, signature, publicKey), "signed manifest tampering rejected");
                var mismatch = AppUpdateFiles.Json(new { Schema = "locallink-update-v1", Version = version, Package = "LocalLink-" + version + "-windows-x64.zip", Sha256 = new string('b', 64), Bytes = offer.Bytes });
                var mismatchSignature = AppUpdateFiles.Utf8.GetBytes(Convert.ToBase64String(rsa.SignData(mismatch, CryptoConfig.MapNameToOID("SHA256"))));
                Expect<IOException>(() => AppUpdateClient.VerifySignedManifest(offer, mismatch, mismatchSignature, publicKey), "even signed mismatched package rejected");
                using (var other = new RSACryptoServiceProvider(2048))
                { other.PersistKeyInCsp = false; Expect<IOException>(() => AppUpdateClient.VerifySignedManifest(offer, manifest, signature, other.ToXmlString(false)), "other signing key rejected"); }
                Expect<IOException>(() => AppUpdateClient.VerifySignedManifest(offer, manifest, AppUpdateFiles.Utf8.GetBytes("not-base64"), publicKey), "invalid signature format rejected");
                var client = new AppUpdateClient(Path.Combine(root, "signature-client"), publicKey, (uri, max, progress, token) => Task.FromResult(Release(version, 55, new string('a', 64))));
                Expect<IOException>(() => client.CheckAsync("0.0.1", CancellationToken.None).GetAwaiter().GetResult(), "configured key cannot silently downgrade to digest-only");
            }
        }
        static void ArchiveValidation()
        {
            string good = Zip("good", PackageFiles());
            string output = Path.Combine(root, "extracted");
            AppUpdateClient.ExtractPackage(good, output, version, CancellationToken.None);
            Check(Directory.GetFiles(output).Length == 4, "exact4 packaged app files extracted");
            Check(File.ReadAllBytes(Path.Combine(output, "LocalLink.exe")).SequenceEqual(File.ReadAllBytes(Assembly.GetExecutingAssembly().Location)), "application bytes preserved");
            Expect<IOException>(() => AppUpdateClient.ExtractPackage(good, output, version, CancellationToken.None), "existing payload never overwritten");
            foreach (string name in new[] { "../evil.exe", "/evil.exe", "folder/LocalLink.exe", "LocalLink.exe:evil", "locallink.exe", "LocalLink.exe\\bad" })
            {
                var files = PackageFiles(); files.Remove("LICENSE"); files[name] = new byte[] { 1 };
                string bad = Zip(Guid.NewGuid().ToString("N"), files);
                Expect<IOException>(() => AppUpdateClient.ExtractPackage(bad, Path.Combine(root, Guid.NewGuid().ToString("N")), version, CancellationToken.None), "unsafe archive entry rejected");
            }
            var extra = PackageFiles(); extra["credentials.txt"] = new byte[] { 1 };
            Expect<IOException>(() => AppUpdateClient.ExtractPackage(Zip("extra", extra), Path.Combine(root, "extra-out"), version, CancellationToken.None), "extra files rejected");
            var missing = PackageFiles(); missing.Remove("THIRD-PARTY.md");
            Expect<IOException>(() => AppUpdateClient.ExtractPackage(Zip("missing", missing), Path.Combine(root, "missing-out"), version, CancellationToken.None), "missing license rejected");
            var empty = PackageFiles(); empty["LICENSE"] = new byte[0];
            Expect<IOException>(() => AppUpdateClient.ExtractPackage(Zip("empty", empty), Path.Combine(root, "empty-out"), version, CancellationToken.None), "empty entry rejected");
            var invalidApp = PackageFiles(); invalidApp["LocalLink.exe"] = Encoding.UTF8.GetBytes("not an executable");
            Expect<IOException>(() => AppUpdateClient.ExtractPackage(Zip("not-app", invalidApp), Path.Combine(root, "not-app-out"), version, CancellationToken.None), "non-PE app rejected");
            Expect<IOException>(() => AppUpdateClient.ExtractPackage(good, Path.Combine(root, "wrong-version"), "123.0.0", CancellationToken.None), "assembly version must match release");
            string symlink = Zip("symlink", PackageFiles());
            using (var zip = ZipFile.Open(symlink, ZipArchiveMode.Update)) zip.GetEntry("LICENSE").ExternalAttributes = unchecked((int)(0xA000 << 16));
            Expect<IOException>(() => AppUpdateClient.ExtractPackage(symlink, Path.Combine(root, "symlink-out"), version, CancellationToken.None), "ZIP symbolic links rejected");
            string duplicate = Zip("duplicate", PackageFiles());
            using (var zip = ZipFile.Open(duplicate, ZipArchiveMode.Update)) { zip.GetEntry("LICENSE").Delete(); using (var stream = zip.CreateEntry("LocalLink.exe").Open()) stream.WriteByte(1); }
            Expect<IOException>(() => AppUpdateClient.ExtractPackage(duplicate, Path.Combine(root, "duplicate-out"), version, CancellationToken.None), "ZIP duplicate names rejected");
            var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Expect<OperationCanceledException>(() => AppUpdateClient.ExtractPackage(good, Path.Combine(root, "cancelled-out"), version, cancelled.Token), "cancelled extraction rejected");
        }
        static void TransportAndStaging()
        {
            byte[] zip = File.ReadAllBytes(Zip("transport", PackageFiles())); int calls = 0;
            var client = new AppUpdateClient(Path.Combine(root, "staging"), "", (uri, max, progress, token) =>
            { calls++; return Task.FromResult(uri.AbsoluteUri == AppUpdateClient.LatestReleaseApi ? Release(version, zip.Length, AppUpdateFiles.Hash(zip)) : zip); });
            var offer = client.CheckAsync("0.0.1", CancellationToken.None).GetAwaiter().GetResult();
            var staged = client.StageAsync(offer, null, CancellationToken.None).GetAwaiter().GetResult();
            Check(calls == 2 && staged.Version == version && Directory.GetFiles(Path.Combine(staged.Directory, "payload")).Length == 4, "offline injected transport check+stage verified");
            Check(File.Exists(Path.Combine(staged.Directory, "package.zip")), "verified archive retained for apply-time revalidation");
            var corrupt = new AppUpdateClient(Path.Combine(root, "corrupt-staging"), "", (uri, max, progress, token) => Task.FromResult(new byte[] { 1, 2, 3 }));
            Expect<IOException>(() => corrupt.StageAsync(offer, null, CancellationToken.None).GetAwaiter().GetResult(), "download checksum mismatch stops staging");
            var offline = new AppUpdateClient(Path.Combine(root, "offline-staging"), "", (uri, max, progress, token) => { throw new IOException("offline fixture"); });
            Expect<IOException>(() => offline.CheckAsync("0.0.1", CancellationToken.None).GetAwaiter().GetResult(), "offline failure explicit");
            Check(!Directory.Exists(Path.Combine(root, "offline-staging")), "offline check never mutates installed/staged files");
            var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Expect<OperationCanceledException>(() => client.StageAsync(offer, null, cancelled.Token).GetAwaiter().GetResult(), "cancel before staging creates nothing");
            foreach (string path in new[] { "relative", @"C:", @"C:", @"\\host\share\x", root + ":stream", @"\rooted-only", "C:relative" })
                Expect<IOException>(() => new AppUpdateClient(path, "", (uri, max, p, token) => Task.FromResult(zip)), "nonlocal/ambiguous updater root rejected");
        }
        static void TransactionAndRollback()
        {
            string fixture = Path.Combine(root, "transactions"); Directory.CreateDirectory(fixture);
            AppUpdatePlan plan = FixturePlan(fixture, "success");
            File.WriteAllText(Path.Combine(plan.Target, "user-settings.json"), "preserve me");
            Directory.CreateDirectory(Path.Combine(plan.Target, "my-docs")); File.WriteAllText(Path.Combine(plan.Target, "my-docs", "keep.txt"), "keep");
            AppUpdateInstaller.ApplyFiles(plan, null);
            AppUpdateInstaller.VerifyFiles(plan.Target, plan.Incoming); Check(true, "all4 app files transaction applied");
            Check(File.ReadAllText(Path.Combine(plan.Target, "user-settings.json")) == "preserve me" && File.ReadAllText(Path.Combine(plan.Target, "my-docs", "keep.txt")) == "keep", "unrelated files/settings untouched");
            Check(File.Exists(Path.Combine(plan.Directory, "status.installed")), "installed receipt persisted");
            AppUpdateInstaller.RestoreFiles(plan); AppUpdateInstaller.VerifyFiles(plan.Target, plan.Previous);
            Check(File.Exists(Path.Combine(plan.Directory, "status.rolled-back")), "manual rollback restores exact4 previous files");
            foreach (int failureAt in new[] { 1, 2, 3, 4 })
            {
                var interrupted = FixturePlan(fixture, "failure-" + failureAt);
                Expect<IOException>(() => AppUpdateInstaller.ApplyFiles(interrupted, index => { if (index == failureAt) throw new IOException("injected interrupted update"); }), "injected per-file failure surfaced");
                AppUpdateInstaller.VerifyFiles(interrupted.Target, interrupted.Previous); Check(true, "partial installation rolled back without losing original");
            }
            var changed = FixturePlan(fixture, "changed-current"); File.WriteAllText(Path.Combine(changed.Target, "LICENSE"), "changed externally");
            Expect<IOException>(() => AppUpdateInstaller.ApplyFiles(changed, null), "externally modified original rejects before mutation");
            Check(File.ReadAllText(Path.Combine(changed.Target, "LICENSE")) == "changed externally", "unapproved original edit not overwritten");
            var tampered = FixturePlan(fixture, "changed-payload"); File.WriteAllText(Path.Combine(tampered.Directory, "payload", "LICENSE"), "tampered");
            Expect<IOException>(() => AppUpdateInstaller.ApplyFiles(tampered, null), "tampered payload rejected at installation");
            AppUpdateInstaller.VerifyFiles(tampered.Target, tampered.Previous); Check(true, "tampered stage leaves original intact");
            var absent = FixturePlan(fixture, "original-absent");
            File.Delete(Path.Combine(absent.Target, "THIRD-PARTY.md")); File.Delete(Path.Combine(absent.Directory, "backup", "THIRD-PARTY.md"));
            absent.Previous = AppUpdateInstaller.Snapshot(absent.Target);
            AppUpdateInstaller.ApplyFiles(absent, null); AppUpdateInstaller.RestoreFiles(absent);
            Check(!File.Exists(Path.Combine(absent.Target, "THIRD-PARTY.md")), "rollback restores originally absent approved file state");
        }
        static void ReplacementRetryValidation()
        {
            string folder = Path.Combine(root, "retry"); Directory.CreateDirectory(folder);
            foreach (int code in new[] { 32, 33, 1175 })
            {
                string target, temporary; AppUpdateFileState incoming = RetryFixture(folder, out target, out temporary);
                int calls = 0; var delays = new List<int>();
                AppUpdateInstaller.ReplaceWithRetry(target, temporary, incoming, delegate { if (++calls < 3) throw new RetryIOException(code); }, delays.Add);
                Check(calls == 3 && delays.SequenceEqual(new[] { 25, 50 }), "temporary Windows file lock retries without real sleeps: " + code);
                Check(File.ReadAllText(target) == "original" && File.ReadAllText(temporary) == "incoming", "retry seam itself never changes unrelated file content");
            }
            {
                string target, temporary; AppUpdateFileState incoming = RetryFixture(folder, out target, out temporary);
                int calls = 0; var delays = new List<int>();
                Expect<RetryIOException>(() => AppUpdateInstaller.ReplaceWithRetry(target, temporary, incoming, delegate { calls++; throw new RetryIOException(1175); }, delays.Add), "persistent replacement lock is surfaced");
                Check(calls == 8 && delays.SequenceEqual(new[] { 25, 50, 100, 200, 400, 400, 400 }) && delays.Sum() < 2000, "retry attempts and total backoff are bounded below two seconds");
            }
            {
                string target, temporary; AppUpdateFileState incoming = RetryFixture(folder, out target, out temporary);
                int calls = 0, sleeps = 0;
                using (var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
                    AppUpdateInstaller.ReplaceWithRetry(target, temporary, incoming,
                        delegate { calls++; File.Replace(temporary, target, null); }, delegate { sleeps++; locked.Dispose(); });
                Check(calls == 2 && sleeps == 1, "real Windows delete-sharing lock retries once after the isolated fixture handle closes");
                Check(File.ReadAllText(target) == "incoming" && !File.Exists(temporary), "real replacement installs exact checked bytes after transient lock");
            }
            foreach (int code in new[] { 5, 39, 112, 1176, 1177, 123, 145 })
            {
                string target, temporary; AppUpdateFileState incoming = RetryFixture(folder, out target, out temporary);
                int calls = 0, sleeps = 0;
                Expect<RetryIOException>(() => AppUpdateInstaller.ReplaceWithRetry(target, temporary, incoming, delegate { calls++; throw new RetryIOException(code); }, value => sleeps++), "nonretryable Windows error surfaced: " + code);
                Check(calls == 1 && sleeps == 0, "ACL/disk-full/partially moved names never retried: " + code);
            }
            {
                string target, temporary; AppUpdateFileState incoming = RetryFixture(folder, out target, out temporary);
                int calls = 0, sleeps = 0;
                Expect<UnauthorizedAccessException>(() => AppUpdateInstaller.ReplaceWithRetry(target, temporary, incoming, delegate { calls++; throw new UnauthorizedAccessException("fixture"); }, value => sleeps++), "access denied is not caught as a temporary file lock");
                Check(calls == 1 && sleeps == 0, "no retry or permissions change for denied access");
                Expect<IOException>(() => AppUpdateInstaller.ReplaceWithRetry(target, temporary, incoming, delegate { throw new IOException("generic fixture"); }, value => { throw new Exception("must not retry generic IOException"); }), "generic IOException not retried");
            }
            foreach (string mutation in new[] { "target-content", "target-size", "target-missing", "target-directory", "temporary-content", "temporary-size", "temporary-missing", "temporary-directory", "absent-target-appeared" })
            {
                string target, temporary; AppUpdateFileState incoming = RetryFixture(folder, out target, out temporary);
                if (mutation == "absent-target-appeared") File.Delete(target);
                int calls = 0, sleeps = 0;
                Expect<IOException>(() => AppUpdateInstaller.ReplaceWithRetry(target, temporary, incoming,
                    delegate { calls++; throw new RetryIOException(32); }, value =>
                    {
                        sleeps++;
                        string file = mutation.StartsWith("temporary", StringComparison.Ordinal) ? temporary : target;
                        if (mutation.EndsWith("content", StringComparison.Ordinal)) File.WriteAllText(file, "tampered"); // same byte length as the original fixture
                        else if (mutation.EndsWith("size", StringComparison.Ordinal)) File.WriteAllText(file, "externally modified size");
                        else if (mutation.EndsWith("missing", StringComparison.Ordinal)) File.Delete(file);
                        else if (mutation.EndsWith("directory", StringComparison.Ordinal)) { File.Delete(file); Directory.CreateDirectory(file); }
                        else File.WriteAllText(file, "appeared externally");
                    }), "changed replacement/target aborts rather than overwriting: " + mutation);
                Check(calls == 1 && sleeps == 1, "file state is revalidated before the next attempt: " + mutation);
            }
            {
                string target, temporary; AppUpdateFileState incoming = RetryFixture(folder, out target, out temporary);
                File.WriteAllText(temporary, "tampered"); int calls = 0;
                Expect<IOException>(() => AppUpdateInstaller.ReplaceWithRetry(target, temporary, incoming, delegate { calls++; }, delegate { }), "initial replacement must already match signed state");
                Check(calls == 0 && File.ReadAllText(target) == "original", "invalid initial replacement never reaches rename");
            }
        }
        static AppUpdateFileState RetryFixture(string folder, out string target, out string temporary)
        {
            string fixture = Path.Combine(folder, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
            target = Path.Combine(fixture, "target"); temporary = Path.Combine(fixture, "temporary");
            File.WriteAllText(target, "original"); File.WriteAllText(temporary, "incoming");
            return new AppUpdateFileState { Exists = true, Bytes = new FileInfo(temporary).Length, Sha256 = AppUpdateFiles.HashFile(temporary) };
        }
        sealed class RetryIOException : IOException
        { internal RetryIOException(int code) : base("synthetic Windows error " + code) { HResult = unchecked((int)(0x80070000U | (uint)code)); } }
        static void HealthyMetadataFailure()
        {
            string fixture=Path.Combine(root,"metadata-failure");Directory.CreateDirectory(fixture);
            var plan=FixturePlan(fixture,"metadata");var bytes=AppUpdateFiles.Json(plan);string hash=AppUpdateFiles.Hash(bytes);
            AppUpdateFiles.WriteNew(Path.Combine(plan.Directory,"plan.json"),bytes);
            AppUpdateInstaller.SavePending(plan,hash);AppUpdateInstaller.ApplyFiles(plan,null);
            using(var locked=new FileStream(Path.Combine(fixture,"last-update.json"),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None))
            {
                AppUpdateInstaller.CommitHealthy(plan,hash);
                AppUpdateInstaller.VerifyFiles(plan.Target,plan.Incoming);
                Check(!File.Exists(Path.Combine(plan.Directory,"status.rolled-back")),"metadata failure cannot roll back a healthy live install");
                Check(File.Exists(Path.Combine(plan.Directory,"status.committed")),"healthy commit recorded despite locked last-update pointer");
                var fallback=AppUpdateInstaller.FindRollback(Path.Combine(plan.Target,"LocalLink.exe"),fixture);
                Check(fallback!=null&&!fallback.Interrupted&&fallback.Version==plan.PreviousVersion,"committed pending journal supplies rollback despite locked pointer");
            }
        }
        static void PlanValidation()
        {
            string fixture = Path.Combine(root, "plan-root"); Directory.CreateDirectory(fixture);
            AppUpdatePlan plan = FixturePlan(fixture, "plan");
            string path = Path.Combine(plan.Directory, "plan.json"); byte[] bytes = AppUpdateFiles.Json(plan); AppUpdateFiles.WriteNew(path, bytes);
            string hash = AppUpdateFiles.Hash(bytes);
            Check(AppUpdateInstaller.LoadPlan(path, hash, fixture).Version == version, "hash-bound local plan loads");
            Expect<IOException>(() => AppUpdateInstaller.LoadPlan(path, new string('0', 64), fixture), "plan hash tampering rejected");
            Expect<IOException>(() => AppUpdateInstaller.LoadPlan(path, hash, Path.Combine(root, "another-root")), "plan cannot escape staging root");
            string old = plan.ParentSha256; plan.ParentSha256 = new string('b', 64);
            Expect<IOException>(() => AppUpdateInstaller.ValidatePlan(plan, fixture), "parent binary bound to original snapshot"); plan.ParentSha256 = old;
            var oldNames = plan.Incoming; plan.Incoming = plan.Incoming.Concat(new[] { plan.Incoming[0] }).ToArray();
            Expect<IOException>(() => AppUpdateInstaller.ValidatePlan(plan, fixture), "plan cannot add unapproved file"); plan.Incoming = oldNames;
            string name = plan.Incoming[0].Name; plan.Incoming[0].Name = "../escape";
            Expect<IOException>(() => AppUpdateInstaller.ValidatePlan(plan, fixture), "plan file traversal rejected"); plan.Incoming[0].Name = name;
            AppUpdateInstaller.ApplyFiles(plan, null); AppUpdateInstaller.Mark(plan, "committed"); AppUpdateInstaller.SavePointer(plan, hash);
            var rollback = AppUpdateInstaller.FindRollback(Path.Combine(plan.Target, "LocalLink.exe"), fixture);
            Check(rollback != null && rollback.Version == plan.PreviousVersion, "rollback offered only for matching installed transaction");
            string other = Path.Combine(root, "unrelated-app"); Directory.CreateDirectory(other); File.WriteAllBytes(Path.Combine(other, "LocalLink.exe"), new byte[] { 1 });
            Check(AppUpdateInstaller.FindRollback(Path.Combine(other, "LocalLink.exe"), fixture) == null, "other LocalLink copy cannot consume rollback");
            AppUpdateInstaller.RestoreFiles(plan);
            Check(AppUpdateInstaller.FindRollback(Path.Combine(plan.Target, "LocalLink.exe"), fixture) == null, "completed rollback not offered as pending");
            var pending = FixturePlan(fixture, "pending"); string pendingPath = Path.Combine(pending.Directory, "plan.json");
            byte[] pendingBytes = AppUpdateFiles.Json(pending); AppUpdateFiles.WriteNew(pendingPath, pendingBytes);
            AppUpdateInstaller.SavePending(pending, AppUpdateFiles.Hash(pendingBytes));
            var recovery = AppUpdateInstaller.FindRollback(Path.Combine(pending.Target, "LocalLink.exe"), fixture);
            Check(recovery != null && recovery.Interrupted, "uncommitted prepared transaction offers recovery after interruption");
            using (var locked = new FileStream(Path.Combine(pending.Directory, "helper.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                Check(AppUpdateInstaller.FindRollback(Path.Combine(pending.Target, "LocalLink.exe"), fixture) == null, "active helper transaction never offered for concurrent recovery");
            Check(!AppUpdateHealth.IsHealthLaunch(new string[0]) && !AppUpdateHealth.IsHealthLaunch(new[] { "--update-health" }) && AppUpdateHealth.IsHealthLaunch(new[] { "--update-health", "x", "y" }), "health protocol recognized only exactargument shape");
            foreach (string arg in new[] { "a\"b", "a\nb", "a\rb", "a\0b" }) Expect<IOException>(() => AppUpdateLauncher.Quote(arg), "shell/argument injection rejected");
        }
        static AppUpdatePlan FixturePlan(string fixture, string label)
        {
            string directory = Path.Combine(fixture, Guid.NewGuid().ToString("N")), target = Path.Combine(root, "target-" + label);
            Directory.CreateDirectory(directory); Directory.CreateDirectory(target);
            foreach (string name in AppUpdateFiles.Names) File.WriteAllBytes(Path.Combine(target, name), Encoding.UTF8.GetBytes("old " + label + " " + name));
            string payload = Path.Combine(directory, "payload"), backup = Path.Combine(directory, "backup"); Directory.CreateDirectory(payload); Directory.CreateDirectory(backup);
            foreach (string name in AppUpdateFiles.Names) { File.Copy(Path.Combine(target, name), Path.Combine(backup, name)); File.WriteAllBytes(Path.Combine(payload, name), Encoding.UTF8.GetBytes("new " + label + " " + name)); }
            var previous = AppUpdateInstaller.Snapshot(target); var process = Process.GetCurrentProcess();
            return new AppUpdatePlan { Schema = 1, Directory = directory, Target = target, Version = version, PreviousVersion = "0.0.1",
                ParentId = process.Id, ParentStartedUtcTicks = process.StartTime.ToUniversalTime().Ticks, ParentSha256 = previous.Single(value => value.Name == "LocalLink.exe").Sha256,
                HealthToken = Guid.NewGuid().ToString("N"), Incoming = AppUpdateInstaller.Snapshot(payload), Previous = previous };
        }
        static Dictionary<string, byte[]> PackageFiles()
        { return new Dictionary<string, byte[]> { { "LocalLink.exe", File.ReadAllBytes(Assembly.GetExecutingAssembly().Location) }, { "LocalLink.exe.config", Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><configuration/>") }, { "LICENSE", Encoding.UTF8.GetBytes("fixture license") }, { "THIRD-PARTY.md", Encoding.UTF8.GetBytes("fixture attribution") } }; }
        static string Zip(string name, Dictionary<string, byte[]> files)
        {
            string path = Path.Combine(root, name + ".zip");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
                foreach (var item in files) using (var stream = archive.CreateEntry(item.Key).Open()) stream.Write(item.Value, 0, item.Value.Length);
            return path;
        }
        static byte[] Release(string releaseVersion, long size, string hash)
        {
            string tag = "v" + releaseVersion, name = "LocalLink-" + releaseVersion + "-windows-x64.zip";
            return AppUpdateFiles.Json(new { draft = false, prerelease = false, tag_name = tag,
                html_url = AppUpdateClient.RepositoryUrl + "/releases/tag/" + tag,
                assets = new[] { new { name = name, size = size, digest = "sha256:" + hash, state = "uploaded", browser_download_url = AppUpdateClient.RepositoryUrl + "/releases/download/" + tag + "/" + name } } });
        }
        static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception("FAIL: " + message); }
        static void Expect<T>(Action action, string message) where T : Exception
        { bool passed = false; try { action(); } catch (T) { passed = true; } Check(passed, message); }
    }
}
