using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LocalLink
{
    public sealed class AppUpdateOffer
    {
        public string Version { get; internal set; }
        public string ReleaseUrl { get; internal set; }
        public string PackageUrl { get; internal set; }
        public string Sha256 { get; internal set; }
        public long Bytes { get; internal set; }
        public bool PublisherSignatureVerified { get; internal set; }
        internal AppUpdateAsset Manifest, Signature;
        public override string ToString() { return Version; }
    }
    internal sealed class AppUpdateAsset
    {
        public string Name, Url, Sha256;
        public long Bytes;
    }
    public sealed class StagedAppUpdate
    {
        public string Version { get; internal set; }
        public string Directory { get; internal set; }
        internal string ArchiveSha256;
    }
    public sealed class AppUpdateProgress
    {
        public string Message { get; private set; }
        public int Percent { get; private set; }
        public AppUpdateProgress(string message, int percent) { Message = message; Percent = Math.Max(0, Math.Min(100, percent)); }
    }

    public sealed class AppUpdateClient
    {
        public const string RepositoryUrl = "https://github.com/DrKreck/LocalLink";
        public const string LatestReleaseApi = "https://api.github.com/repos/DrKreck/LocalLink/releases/latest";
        public const long MaximumPackageBytes = 64L * 1024 * 1024;
        const int MaximumReleaseBytes = 512 * 1024;
        readonly string root, publicKey;
        readonly Func<Uri, long, IProgress<AppUpdateProgress>, CancellationToken, Task<byte[]>> fetch;

        // Production updates require a manifest signed by this pinned publisher.
        // HTTPS and package SHA256 remain additional checks, not a signature fallback.
        // Keys are NEVER downloaded or taken from user settings.
        public const string TrustedManifestPublicKeyXml = "<RSAKeyValue><Modulus>wiU6/X8U1sW+C7ZK2mHTcTvQYWTpdsQp/IS0lDyHow5VK4TP4w2vk7djbtxzG9Rz4K4kbz7+C115VdZ9YiKf9yegiknT+ZDcrFI98gpa3ZAHXn65ltHrkm17svgnJncogq4DowHk6BihGz9QbvX4goRiqyx020XZq2MkvMmhIFdCqiIIporHyOn3+hDztLDn31CARDn1zo5N87alI8Nu7uE8OdU3FDC4kIVetc+rZc8Rwc3dx5SQywtSRY8HAa+Y4GH9jKKJR7mRdJxgpxgWsYr+QKqM2iM3BG1FyFdhJbCmTwa0RqpbKlsTZKvaFwtDcjUK89e2UnQG8/Q2+zvpGbHslTi70/1GLm50C5Mk0roXVWtO6Py6vN/R2iNrKNnlfBg4wTdhZE41c6CzyFS3oDlzuyiFiYszF2aT90yQyV1TstriOTsf97I0BzIYR4AeYr13fbNqc7iUEdtZ/UfLzjjA44QAfR/LTPEpz6QOQ59Q6sqsi0eVfi71/C1yQeNOxehelEiJ2kuykGG+ddANfGRXFroz/uLwhhJJqwecOKECNkBEp5YRPgP2osfhslaRp3WMtstLUpcH9qz8Z/E3Bao5XFfL8nH3XXloGAkm774ds4ynLQ07UoFpGYUvYrCqEauQ7VhdbkzUuSMtlBlNvGnHYMrvMPvYkrvaVGirZHU=</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";
        public static string DefaultRoot
        {
            get
            {
#if LOCALLINK_UPDATER_TESTS
                string fixture = Environment.GetEnvironmentVariable("LOCALLINK_UPDATE_FIXTURE_ROOT");
                if (!String.IsNullOrWhiteSpace(fixture)) return AppUpdateFiles.LocalDirectory(fixture);
#endif
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalLink", "updates");
            }
        }
        public AppUpdateClient() : this(DefaultRoot, TrustedManifestPublicKeyXml, FetchAsync) { }
        internal AppUpdateClient(string directory, string trustedPublicKey,
            Func<Uri, long, IProgress<AppUpdateProgress>, CancellationToken, Task<byte[]>> transport)
        {
            root = AppUpdateFiles.LocalDirectory(directory); publicKey = trustedPublicKey ?? ""; fetch = transport;
            if (fetch == null) throw new ArgumentNullException("transport");
            if (publicKey.Length > 16384) throw new ArgumentException("Недопустимый ключ обновлений.");
        }
        public async Task<AppUpdateOffer> CheckAsync(string currentVersion, CancellationToken token)
        {
            Version current = ParseVersion(currentVersion);
            byte[] bytes = await fetch(new Uri(LatestReleaseApi), MaximumReleaseBytes, null, token).ConfigureAwait(false);
            AppUpdateOffer offer = ParseRelease(bytes, current);
            if (offer == null) return null;
            if (publicKey.Length != 0)
            {
                if (offer.Manifest == null || offer.Signature == null) throw new IOException("В релизе нет подписанного манифеста обновления.");
                byte[] manifest = await FetchAssetAsync(offer.Manifest, 16384, token).ConfigureAwait(false);
                byte[] signature = await FetchAssetAsync(offer.Signature, 8192, token).ConfigureAwait(false);
                VerifySignedManifest(offer, manifest, signature, publicKey);
                offer.PublisherSignatureVerified = true;
            }
            return offer;
        }
        async Task<byte[]> FetchAssetAsync(AppUpdateAsset asset, long limit, CancellationToken token)
        {
            if (asset.Bytes < 1 || asset.Bytes > limit) throw new IOException("Недопустимый размер манифеста обновления.");
            byte[] bytes = await fetch(new Uri(asset.Url), asset.Bytes, null, token).ConfigureAwait(false);
            if (bytes.LongLength != asset.Bytes || AppUpdateFiles.Hash(bytes) != asset.Sha256) throw new IOException("Контрольная сумма манифеста не совпала.");
            return bytes;
        }
        public async Task<StagedAppUpdate> StageAsync(AppUpdateOffer offer, IProgress<AppUpdateProgress> progress, CancellationToken token)
        {
            ValidateOffer(offer); token.ThrowIfCancellationRequested();
            if (publicKey.Length != 0 && !offer.PublisherSignatureVerified) throw new IOException("Подпись обновления не проверена.");
            AppUpdateFiles.NoReparse(root); Directory.CreateDirectory(root); AppUpdateFiles.NoReparse(root);
            string directory = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            AppUpdateFiles.NoReparse(directory);
            if (progress != null) progress.Report(new AppUpdateProgress("Скачивание обновления", 0));
            byte[] bytes = await fetch(new Uri(offer.PackageUrl), offer.Bytes, progress, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (bytes.LongLength != offer.Bytes || AppUpdateFiles.Hash(bytes) != offer.Sha256) throw new IOException("Контрольная сумма обновления не совпала.");
            string archive = Path.Combine(directory, "package.zip"); AppUpdateFiles.WriteNew(archive, bytes);
            ExtractPackage(archive, Path.Combine(directory, "payload"), offer.Version, token);
            if (progress != null) progress.Report(new AppUpdateProgress("Обновление готово", 100));
            return new StagedAppUpdate { Version = offer.Version, Directory = directory, ArchiveSha256 = offer.Sha256 };
        }
        internal static AppUpdateOffer ParseRelease(byte[] bytes, Version current)
        {
            if (bytes == null || bytes.Length < 2 || bytes.Length > MaximumReleaseBytes) throw new IOException("Недопустимый ответ сервера обновлений.");
            var data = AppUpdateFiles.Object(bytes, MaximumReleaseBytes);
            if (AppUpdateFiles.Boolean(data, "draft") || AppUpdateFiles.Boolean(data, "prerelease")) return null;
            string tag = AppUpdateFiles.Text(data, "tag_name", 40);
            if (!Regex.IsMatch(tag, @"\Av(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4})\z")) throw new IOException("Недопустимый номер релиза.");
            Version version = ParseVersion(tag.Substring(1));
            if (version.CompareTo(current) <= 0) return null;
            string releaseUrl = AppUpdateFiles.Text(data, "html_url", 256);
            if (releaseUrl != RepositoryUrl + "/releases/tag/" + tag) throw new IOException("Адрес релиза не принадлежит LocalLink.");
            object assetsValue; var assets = data.TryGetValue("assets", out assetsValue) ? assetsValue as object[] : null;
            if (assets == null || assets.Length > 32) throw new IOException("Недопустимый список файлов релиза.");
            string packageName = "LocalLink-" + version.ToString(3) + "-windows-x64.zip";
            AppUpdateAsset package = null, manifest = null, signature = null;
            foreach (object item in assets)
            {
                var value = item as Dictionary<string, object>; if (value == null) throw new IOException("Недопустимое описание файла релиза.");
                string name = AppUpdateFiles.Text(value, "name", 160);
                if (name != packageName && name != "update-manifest.json" && name != "update-manifest.sig") continue;
                var asset = new AppUpdateAsset { Name = name, Url = AppUpdateFiles.Text(value, "browser_download_url", 512),
                    Sha256 = Digest(AppUpdateFiles.Text(value, "digest", 80)), Bytes = AppUpdateFiles.Number(value, "size", 1, MaximumPackageBytes) };
                if (asset.Url != RepositoryUrl + "/releases/download/" + tag + "/" + name || AppUpdateFiles.Text(value, "state", 20) != "uploaded")
                    throw new IOException("Недопустимый адрес файла обновления.");
                if (name == packageName) { if (package != null) throw new IOException("Повторяющийся архив обновления."); package = asset; }
                else if (name == "update-manifest.json") { if (manifest != null) throw new IOException("Повторяющийся манифест."); manifest = asset; }
                else { if (signature != null) throw new IOException("Повторяющаяся подпись."); signature = asset; }
            }
            if (package == null) throw new IOException("Windows-архив не найден в релизе.");
            return new AppUpdateOffer { Version = version.ToString(3), ReleaseUrl = releaseUrl, PackageUrl = package.Url, Sha256 = package.Sha256,
                Bytes = package.Bytes, Manifest = manifest, Signature = signature };
        }
        static string Digest(string value)
        {
            if (!Regex.IsMatch(value, @"\Asha256:[a-fA-F0-9]{64}\z")) throw new IOException("В релизе нет контрольной суммы SHA256.");
            return value.Substring(7).ToLowerInvariant();
        }
        internal static Version ParseVersion(string value)
        {
            Version version;
            if (value == null || !Regex.IsMatch(value, @"\A(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4})(?:\.0)?\z") || !Version.TryParse(value, out version))
                throw new IOException("Недопустимый номер версии приложения.");
            return new Version(version.Major, version.Minor, version.Build);
        }
        internal static void ValidateOffer(AppUpdateOffer offer)
        {
            if (offer == null) throw new ArgumentNullException("offer");
            string version = ParseVersion(offer.Version).ToString(3);
            if (offer.Version != version || offer.Bytes < 1 || offer.Bytes > MaximumPackageBytes || !AppUpdateFiles.IsHash(offer.Sha256) ||
                offer.PackageUrl != RepositoryUrl + "/releases/download/v" + version + "/LocalLink-" + version + "-windows-x64.zip" ||
                offer.ReleaseUrl != RepositoryUrl + "/releases/tag/v" + version) throw new IOException("Недопустимый план обновления.");
        }
        internal static void VerifySignedManifest(AppUpdateOffer offer, byte[] bytes, byte[] signatureText, string key)
        {
            if (bytes == null || bytes.Length < 1 || bytes.Length > 16384 || signatureText == null || signatureText.Length > 8192) throw new IOException("Недопустимый манифест.");
            byte[] signature;
            try { signature = Convert.FromBase64String(AppUpdateFiles.Utf8.GetString(signatureText).Trim()); }
            catch (FormatException) { throw new IOException("Недопустимая подпись обновления."); }
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false; rsa.FromXmlString(key);
                if (rsa.KeySize < 2048 || !rsa.VerifyData(bytes, CryptoConfig.MapNameToOID("SHA256"), signature)) throw new IOException("Подпись обновления не совпала.");
            }
            var value = AppUpdateFiles.Object(bytes, 16384);
            AppUpdateFiles.Keys(value, "Schema", "Version", "Package", "Sha256", "Bytes");
            if (AppUpdateFiles.Text(value, "Schema", 64) != "locallink-update-v1" || AppUpdateFiles.Text(value, "Version", 40) != offer.Version ||
                AppUpdateFiles.Text(value, "Package", 160) != "LocalLink-" + offer.Version + "-windows-x64.zip" ||
                AppUpdateFiles.Text(value, "Sha256", 64) != offer.Sha256 || AppUpdateFiles.Number(value, "Bytes", 1, MaximumPackageBytes) != offer.Bytes)
                throw new IOException("Подписанный манифест не соответствует архиву.");
        }
        internal static void ExtractPackage(string archive, string destination, string version, CancellationToken token)
        {
            AppUpdateFiles.NoReparse(archive); AppUpdateFiles.NoReparse(destination);
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Папка обновления уже существует.");
            using (var zip = ZipFile.OpenRead(archive))
            {
                if (zip.Entries.Count != AppUpdateFiles.Names.Length) throw new IOException("В архиве обновления должны быть только файлы приложения.");
                var found = new HashSet<string>(StringComparer.Ordinal); long total = 0;
                foreach (var entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    if (!AppUpdateFiles.Names.Contains(entry.FullName, StringComparer.Ordinal) || !found.Add(entry.FullName) ||
                        entry.Name != entry.FullName || entry.Length < 1 || entry.Length > MaximumPackageBytes ||
                        ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & 0x400) != 0)
                        throw new IOException("Небезопасная структура архива обновления.");
                    total = checked(total + entry.Length); if (total > 96L * 1024 * 1024) throw new IOException("Архив обновления слишком большой.");
                }
                Directory.CreateDirectory(destination); AppUpdateFiles.NoReparse(destination);
                foreach (var entry in zip.Entries)
                    using (var input = entry.Open()) using (var output = new FileStream(Path.Combine(destination, entry.FullName), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { AppUpdateFiles.CopyBounded(input, output, entry.Length, token); output.Flush(true); }
            }
            AppUpdateFiles.ValidateApplication(Path.Combine(destination, "LocalLink.exe"), version);
        }
        internal static bool AllowedUri(Uri uri, bool api)
        {
            if (uri == null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) return false;
            if (api) return uri.AbsoluteUri == LatestReleaseApi;
            if (uri.Host == "github.com") return uri.AbsolutePath.StartsWith("/DrKreck/LocalLink/releases/download/v", StringComparison.Ordinal);
            return uri.Host == "release-assets.githubusercontent.com" || uri.Host == "objects.githubusercontent.com";
        }
        static async Task<byte[]> FetchAsync(Uri start, long maximum, IProgress<AppUpdateProgress> progress, CancellationToken token)
        {
            bool api = start.AbsoluteUri == LatestReleaseApi;
            if (maximum < 1 || maximum > MaximumPackageBytes || !AllowedUri(start, api)) throw new IOException("Недопустимый источник обновления.");
            using (var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None })
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(3) })
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("LocalLink-Updater/1");
                client.DefaultRequestHeaders.Accept.ParseAdd(api ? "application/vnd.github+json" : "application/octet-stream");
                Uri current = start;
                for (int redirects = 0; redirects <= 4; redirects++)
                {
                    token.ThrowIfCancellationRequested();
                    if (!AllowedUri(current, api)) throw new IOException("Сервер обновления перенаправил на неизвестный адрес.");
                    using (var response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                    {
                        int status = (int)response.StatusCode;
                        if (status >= 300 && status <= 399)
                        {
                            Uri location = response.Headers.Location;
                            if (api || location == null || redirects == 4) throw new IOException("Недопустимое перенаправление обновления.");
                            current = location.IsAbsoluteUri ? location : new Uri(current, location); continue;
                        }
                        if (!response.IsSuccessStatusCode) throw new IOException("Сервер обновлений ответил HTTP " + status.ToString(CultureInfo.InvariantCulture) + ".");
                        if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value > maximum)
                            throw new IOException("Файл обновления превышает допустимый размер.");
                        using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false)) using (var output = new MemoryStream())
                        {
                            var buffer = new byte[32768]; long count = 0; int read;
                            while ((read = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) != 0)
                            {
                                count += read; if (count > maximum) throw new IOException("Файл обновления превышает допустимый размер.");
                                output.Write(buffer, 0, read);
                                if (progress != null) progress.Report(new AppUpdateProgress("Скачивание обновления", (int)Math.Min(95, count * 95 / maximum)));
                            }
                            return output.ToArray();
                        }
                    }
                }
            }
            throw new IOException("Слишком много перенаправлений обновления.");
        }
    }

    public sealed class AppUpdateRollback
    {
        public string Version { get; internal set; }
        public bool Interrupted { get; internal set; }
        internal string PlanPath, PlanHash;
    }
    internal sealed class AppUpdateFileState
    {
        public string Name { get; set; }
        public bool Exists { get; set; }
        public string Sha256 { get; set; }
        public long Bytes { get; set; }
    }
    internal sealed class AppUpdatePlan
    {
        public int Schema { get; set; }
        public string Directory { get; set; }
        public string Target { get; set; }
        public string Version { get; set; }
        public string PreviousVersion { get; set; }
        public string ParentSha256 { get; set; }
        public int ParentId { get; set; }
        public long ParentStartedUtcTicks { get; set; }
        public string HealthToken { get; set; }
        public bool Rollback { get; set; }
        public AppUpdateFileState[] Incoming { get; set; }
        public AppUpdateFileState[] Previous { get; set; }
    }
    public sealed class AppUpdateHandoff : IDisposable
    {
        readonly AppUpdatePlan plan;
        readonly string planHash, helperPath, helperHash;
        readonly object gate = new object();
        long helperStarted;
        bool confirmed, cancelled, disposed;
        public Process Helper { get; private set; }
        internal AppUpdateHandoff(AppUpdatePlan value, string hash, string executable)
        { plan = value; planHash = hash; helperPath = executable; helperHash = AppUpdateFiles.HashFile(executable); }
        internal void AcceptReady(int id, long started, int directId)
        {
            if (directId != 0 && id != directId) throw new IOException("Ответил другой модуль обновления.");
            var process = Process.GetProcessById(id);
            try
            {
                if (process.HasExited || DesktopProcess.QueryStartTimeUtcTicks(id) != started ||
                    !AppUpdateInstaller.SamePath(DesktopProcess.QueryImagePath(id), helperPath) || AppUpdateFiles.HashFile(helperPath) != helperHash)
                    throw new IOException("Модуль обновления изменился во время запуска.");
                Helper = process; helperStarted = started; process = null;
            }
            finally { if (process != null) process.Dispose(); }
        }
        public void ConfirmExit()
        {
            lock (gate)
            {
                if (cancelled || disposed) throw new InvalidOperationException("Передача обновления отменена.");
                if (confirmed) return;
                if (!OwnsLiveHelper()) throw new IOException("Модуль обновления завершился до передачи управления.");
                AppUpdateHandoffControl.Write(plan, planHash, "exit-confirmed"); confirmed = true;
            }
        }
        public void Cancel()
        {
            lock (gate)
            {
                if (cancelled) return;
                cancelled = true; confirmed = false;
                try { AppUpdateHandoffControl.Write(plan, planHash, "abort"); }
                finally
                {
                    // Never find or stop a process by name. The ready response
                    // was checked against this transaction's exact helper.
                    if (OwnsLiveHelper()) try { Helper.Kill(); } catch (InvalidOperationException) { }
                }
            }
        }
        bool OwnsLiveHelper()
        {
            if (Helper == null) return false;
            try
            {
                return !Helper.HasExited && DesktopProcess.QueryStartTimeUtcTicks(Helper.Id) == helperStarted &&
                    AppUpdateInstaller.SamePath(DesktopProcess.QueryImagePath(Helper.Id), helperPath) && AppUpdateFiles.HashFile(helperPath) == helperHash;
            }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return false; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                try { if (!confirmed) Cancel(); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (System.ComponentModel.Win32Exception) { }
                finally { disposed = true; if (Helper != null) Helper.Dispose(); }
            }
        }
    }
    internal static class AppUpdateHandoffControl
    {
        internal static void Write(AppUpdatePlan plan, string hash, string name)
        {
            string path = Path.Combine(plan.Directory, name);
            string text = hash + "\n" + plan.HealthToken;
            if (File.Exists(path)) { if (!IsSet(plan, hash, name)) throw new IOException("Недопустимый ответ обновления."); return; }
            string temporary = path + ".tmp";
            AppUpdateFiles.WriteNew(temporary, AppUpdateFiles.Utf8.GetBytes(text));
            AppUpdateFiles.NoReparse(path); File.Move(temporary, path);
        }
        internal static bool IsSet(AppUpdatePlan plan, string hash, string name)
        {
            string path = Path.Combine(plan.Directory, name);
            if (!File.Exists(path)) return false;
            if (AppUpdateFiles.Utf8.GetString(AppUpdateFiles.Read(path, 128)) != hash + "\n" + plan.HealthToken)
                throw new IOException("Ответ обновления относится к другому запуску.");
            return true;
        }
        internal static void ThrowIfAborted(AppUpdatePlan plan, string hash)
        { if (IsSet(plan, hash, "abort")) throw new OperationCanceledException("Обновление отменено до выхода приложения."); }
    }
    public static class AppUpdateLauncher
    {
        public static bool IsElevated
        { get { using (var identity = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); } }
        public static Process LaunchApply(StagedAppUpdate staged)
        {
            var handoff = LaunchApplyAsync(staged, CancellationToken.None).GetAwaiter().GetResult();
            handoff.ConfirmExit(); return handoff.Helper;
        }
        public static async Task<AppUpdateHandoff> LaunchApplyAsync(StagedAppUpdate staged, CancellationToken token)
        {
            string executable = Assembly.GetEntryAssembly().Location;
            AppUpdatePlan plan = await Task.Run(() => AppUpdateInstaller.Prepare(staged, executable, false, null), token).ConfigureAwait(false);
            return await LaunchAsync(plan, token).ConfigureAwait(false);
        }
        public static AppUpdateRollback FindRollback()
        { return AppUpdateInstaller.FindRollback(Assembly.GetEntryAssembly().Location, AppUpdateClient.DefaultRoot); }
        public static Process LaunchRollback(AppUpdateRollback rollback)
        {
            var handoff = LaunchRollbackAsync(rollback, CancellationToken.None).GetAwaiter().GetResult();
            handoff.ConfirmExit(); return handoff.Helper;
        }
        public static async Task<AppUpdateHandoff> LaunchRollbackAsync(AppUpdateRollback rollback, CancellationToken token)
        {
            if (rollback == null) throw new ArgumentNullException("rollback");
            string executable = Assembly.GetEntryAssembly().Location;
            AppUpdatePlan plan = await Task.Run(() =>
            {
                AppUpdatePlan original = AppUpdateInstaller.LoadPlan(rollback.PlanPath, rollback.PlanHash, AppUpdateClient.DefaultRoot);
                if (rollback.Version != original.PreviousVersion) throw new IOException("Версия отката изменилась.");
                var staged = new StagedAppUpdate { Version = original.PreviousVersion, Directory = original.Directory };
                return AppUpdateInstaller.Prepare(staged, executable, true, original, rollback.Interrupted);
            }, token).ConfigureAwait(false);
            return await LaunchAsync(plan, token).ConfigureAwait(false);
        }
        static async Task<AppUpdateHandoff> LaunchAsync(AppUpdatePlan plan, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string path = Path.Combine(plan.Directory, "plan.json");
            byte[] bytes = AppUpdateFiles.Json(plan); AppUpdateFiles.WriteNew(path, bytes);
            string hash = AppUpdateFiles.Hash(bytes);
            string helper = WriteResource(plan.Directory, "LocalLink.UpdateHelper", "LocalLink.UpdateHelper.exe");
            WriteResource(plan.Directory, "LocalLink.UpdateHelperConfig", "LocalLink.UpdateHelper.exe.config");
            var handoff = new AppUpdateHandoff(plan, hash, helper);
            Process direct = null;
            // Keep the verified helper bytes read-only throughout its ready
            // handshake, not just until a shell has accepted the launch request.
            using (var locked = new FileStream(helper, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    var clock = Stopwatch.StartNew();
                    bool broker = IsElevated;
#if LOCALLINK_UPDATER_TESTS
                    broker = broker || Environment.GetEnvironmentVariable("LOCALLINK_UPDATE_TEST_DESKTOP") == "1";
#endif
                    if (broker)
                    {
                        Task launch = LaunchOnDesktopAsync(helper, path, hash);
                        if (await Task.WhenAny(launch, Task.Delay(10000, token)).ConfigureAwait(false) != launch)
                        {
                            ObserveFailure(launch);
                            token.ThrowIfCancellationRequested(); throw new IOException("Рабочий стол не ответил. Текущая версия продолжает работать.");
                        }
                        await launch.ConfigureAwait(false);
                    }
                    else
                    {
                        direct = Process.Start(new ProcessStartInfo { FileName = helper, Arguments = "--apply " + Quote(path) + " " + hash,
                            WorkingDirectory = plan.Directory, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                        if (direct == null) throw new IOException("Не удалось запустить обновление.");
                    }
                    string ready = Path.Combine(plan.Directory, "helper-ready.json");
                    while (clock.ElapsedMilliseconds < 10000)
                    {
                        token.ThrowIfCancellationRequested();
                        if (File.Exists(ready))
                        {
                            var value = AppUpdateFiles.Object(AppUpdateFiles.Read(ready, 1024), 1024);
                            AppUpdateFiles.Keys(value, "Schema", "PlanSha256", "HealthToken", "ProcessId", "StartedUtcTicks");
                            if (AppUpdateFiles.Number(value, "Schema", 1, 1) != 1 || AppUpdateFiles.Text(value, "PlanSha256", 64) != hash ||
                                AppUpdateFiles.Text(value, "HealthToken", 32) != plan.HealthToken) throw new IOException("Ответ модуля обновления не совпал с планом.");
                            handoff.AcceptReady((int)AppUpdateFiles.Number(value, "ProcessId", 1, Int32.MaxValue),
                                AppUpdateFiles.Number(value, "StartedUtcTicks", 1, DateTime.UtcNow.AddSeconds(5).Ticks), direct == null ? 0 : direct.Id);
                            token.ThrowIfCancellationRequested(); return handoff;
                        }
                        if (direct != null && direct.HasExited) throw new IOException("Модуль обновления не прошёл подготовку. Текущая версия продолжает работать.");
                        await Task.Delay(100, token).ConfigureAwait(false);
                    }
                    throw new IOException("Модуль обновления не ответил. Текущая версия продолжает работать.");
                }
                catch { handoff.Dispose(); throw; }
                finally { if (direct != null) direct.Dispose(); }
            }
        }
        static Task LaunchOnDesktopAsync(string helper, string path, string hash)
        {
            var completion = new TaskCompletionSource<bool>();
            var thread = new Thread(() =>
            {
                try { DesktopProcess.LaunchVerifiedHelper(helper, path, hash); completion.SetResult(true); }
                catch (Exception error) { completion.SetException(error); }
            });
            thread.IsBackground = true; thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
        }
        static void ObserveFailure(Task task)
        { task.ContinueWith(completed => GC.KeepAlive(completed.Exception), TaskContinuationOptions.OnlyOnFaulted); }
        static string WriteResource(string directory, string resource, string filename)
        {
            byte[] bytes;
            using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            {
                if (input == null) throw new IOException("В сборке отсутствует модуль обновления.");
                using (var output = new MemoryStream()) { AppUpdateFiles.CopyBounded(input, output, input.Length, CancellationToken.None); bytes = output.ToArray(); }
            }
            string path = Path.Combine(directory, filename); AppUpdateFiles.WriteNew(path, bytes);
            if (AppUpdateFiles.HashFile(path) != AppUpdateFiles.Hash(bytes)) throw new IOException("Модуль обновления повреждён.");
            return path;
        }
        internal static string Quote(string value)
        { if (value == null || value.IndexOf('"') >= 0 || value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) throw new IOException("Недопустимый аргумент обновления."); return "\"" + value + "\""; }
    }
    public static class AppUpdateHealth
    {
        public static bool IsHealthLaunch(string[] args)
        { return args != null && args.Length == 3 && args[0] == "--update-health"; }
        internal static bool SupportsStartupGate(string version)
        { return AppUpdateClient.ParseVersion(version) >= new Version(0, 9, 0); }
        public static AppUpdateStartupGate CreateStartupGate(string[] args)
        {
            if (!IsHealthLaunch(args)) return null;
            AppUpdatePlan plan = AppUpdateInstaller.LoadPlan(args[1], args[2], AppUpdateClient.DefaultRoot);
            string executable = Assembly.GetEntryAssembly().Location;
            if (!AppUpdateInstaller.SamePath(executable, Path.Combine(plan.Target, "LocalLink.exe")) ||
                AppUpdateFiles.HashFile(executable) != plan.Incoming.Single(value => value.Name == "LocalLink.exe").Sha256)
                throw new IOException("Запущено другое приложение обновления.");
            return new AppUpdateStartupGate(plan, args[1], args[2], AppUpdateClient.DefaultRoot);
        }
        public static bool Acknowledge(string[] args)
        {
            var gate = CreateStartupGate(args);
            return gate != null && gate.Acknowledge();
        }
    }
    // A health-launched GUI may draw and acknowledge readiness immediately,
    // but must not auto-connect, elevate or exit during the helper's stability
    // window. Only release of this transaction's lock permits normal startup.
    public sealed class AppUpdateStartupGate
    {
        readonly string planPath, planHash, root, directory, target, healthToken, executableHash;
        internal AppUpdateStartupGate(AppUpdatePlan plan)
            : this(plan, PlanPath(plan), AppUpdateFiles.HashFile(PlanPath(plan)), Path.GetDirectoryName(plan.Directory)) { }
        internal AppUpdateStartupGate(AppUpdatePlan plan, string path, string hash, string baseRoot)
        {
            AppUpdateInstaller.ValidatePlan(plan, baseRoot);
            planPath = Path.GetFullPath(path); planHash = hash; root = AppUpdateFiles.LocalDirectory(baseRoot);
            var bound = AppUpdateInstaller.LoadPlan(planPath, planHash, root);
            directory = bound.Directory; target = bound.Target; healthToken = bound.HealthToken;
            executableHash = bound.Incoming.Single(value => value.Name == "LocalLink.exe").Sha256;
        }
        static string PlanPath(AppUpdatePlan plan)
        { if (plan == null) throw new ArgumentNullException("plan"); return Path.Combine(plan.Directory, "plan.json"); }
        internal bool Acknowledge()
        {
            ValidateBoundPlan();
            string marker = Path.Combine(directory, "healthy");
            if (File.Exists(marker)) return AppUpdateFiles.Utf8.GetString(AppUpdateFiles.Read(marker, 128)) == healthToken;
            AppUpdateFiles.WriteNew(marker, AppUpdateFiles.Utf8.GetBytes(healthToken)); return true;
        }
        void ValidateBoundPlan()
        {
            var plan = AppUpdateInstaller.LoadPlan(planPath, planHash, root);
            if (!AppUpdateInstaller.SamePath(plan.Directory, directory) || !AppUpdateInstaller.SamePath(plan.Target, target) ||
                plan.HealthToken != healthToken || plan.Incoming.Single(value => value.Name == "LocalLink.exe").Sha256 != executableHash ||
                AppUpdateFiles.HashFile(Path.Combine(target, "LocalLink.exe")) != executableHash)
                throw new IOException("Приложение или план обновления изменились.");
        }
        public bool CanResume()
        {
            try
            {
                ValidateBoundPlan();
                string locked = Path.Combine(directory, "helper.lock"), healthy = Path.Combine(directory, "healthy"), installed = Path.Combine(directory, "status.installed");
                AppUpdateFiles.NoReparse(locked); AppUpdateFiles.NoReparse(healthy); AppUpdateFiles.NoReparse(installed);
                if (!File.Exists(locked) || !File.Exists(healthy) || !File.Exists(installed)) return false;
                // FileMode.Open never creates a missing lock. The helper must
                // have finished the exact transaction, not merely disappeared.
                using (var released = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    if (AppUpdateFiles.Utf8.GetString(AppUpdateFiles.Read(healthy, 128)) != healthToken) return false;
                    AppUpdateFiles.Read(installed, 128);
                    ValidateBoundPlan();
                }
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (System.Security.SecurityException) { return false; }
            catch (CryptographicException) { return false; }
        }
    }
    internal static class AppUpdateInstaller
    {
        const int MaximumPlanBytes = 32 * 1024;
        internal static AppUpdatePlan Prepare(StagedAppUpdate staged, string executable, bool rollback, AppUpdatePlan original, bool interrupted = false)
        {
            if (staged == null) throw new ArgumentNullException("staged");
            string target = TargetDirectory(executable);
            string directory = AppUpdateFiles.LocalDirectory(staged.Directory);
            AppUpdateFiles.NoReparse(directory);
            if (!directory.StartsWith(AppUpdateClient.DefaultRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("Недопустимая папка подготовленного обновления.");
            if (rollback)
            {
                if (original == null || !SamePath(original.Target, target)) throw new IOException("Откат относится к другой копии LocalLink.");
                if (interrupted)
                {
                    if (!File.Exists(Path.Combine(original.Directory, "status.prepared")) || File.Exists(Path.Combine(original.Directory, "status.committed")) ||
                        File.Exists(Path.Combine(original.Directory, "status.rolled-back")) || Busy(original)) throw new IOException("Прерванное обновление больше не доступно для восстановления.");
                }
                else VerifyFiles(original.Target, original.Incoming);
                VerifyFiles(Path.Combine(original.Directory, "backup"), original.Previous);
                string next = Path.Combine(AppUpdateClient.DefaultRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(next);
                CopyStates(Path.Combine(original.Directory, "backup"), Path.Combine(next, "payload"), original.Previous); directory = next;
            }
            else
            {
                if (!AppUpdateFiles.IsHash(staged.ArchiveSha256) || AppUpdateFiles.HashFile(Path.Combine(directory, "package.zip")) != staged.ArchiveSha256)
                    throw new IOException("Архив обновления изменился после проверки.");
                // Re-extract from the verified archive to defeat stale/tampered payloads.
                string checkedPayload = Path.Combine(directory, "validated");
                AppUpdateClient.ExtractPackage(Path.Combine(directory, "package.zip"), checkedPayload, staged.Version, CancellationToken.None);
                foreach (string name in AppUpdateFiles.Names)
                    if (AppUpdateFiles.HashFile(Path.Combine(checkedPayload, name)) != AppUpdateFiles.HashFile(Path.Combine(directory, "payload", name))) throw new IOException("Файлы обновления изменились после проверки.");
            }
            string oldVersion = AssemblyName.GetAssemblyName(executable).Version.ToString(3);
            var incoming = Snapshot(Path.Combine(directory, "payload"));
            if (!incoming.Single(value => value.Name == "LocalLink.exe").Exists) throw new IOException("В обновлении отсутствует LocalLink.exe.");
            AppUpdateFiles.ValidateApplication(Path.Combine(directory, "payload", "LocalLink.exe"), staged.Version);
            var previous = Snapshot(target); CopyStates(target, Path.Combine(directory, "backup"), previous);
            var process = Process.GetCurrentProcess();
            var plan = new AppUpdatePlan { Schema = 1, Directory = directory, Target = target, Version = staged.Version, PreviousVersion = oldVersion,
                ParentId = process.Id, ParentStartedUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                ParentSha256 = AppUpdateFiles.HashFile(executable), HealthToken = Guid.NewGuid().ToString("N"), Rollback = rollback,
                Incoming = incoming, Previous = previous };
            ValidatePlan(plan, AppUpdateClient.DefaultRoot); return plan;
        }
        internal static AppUpdateRollback FindRollback(string executable, string root)
        {
            var interrupted = FindPointer(executable, root, "pending-update.json", true);
            return interrupted ?? FindPointer(executable, root, "pending-update.json", false) ?? FindPointer(executable, root, "last-update.json", false);
        }
        static AppUpdateRollback FindPointer(string executable, string root, string pointerName, bool interrupted)
        {
            try
            {
                string pointer = Path.Combine(root, pointerName);
                if (!File.Exists(pointer)) return null;
                var value = AppUpdateFiles.Object(AppUpdateFiles.Read(pointer, 4096), 4096); AppUpdateFiles.Keys(value, "Plan", "Sha256");
                string path = AppUpdateFiles.Text(value, "Plan", 1024), hash = AppUpdateFiles.Text(value, "Sha256", 64);
                AppUpdatePlan plan = LoadPlan(path, hash, root);
                if (!SamePath(TargetDirectory(executable), plan.Target) || File.Exists(Path.Combine(plan.Directory, "status.rolled-back")) || Busy(plan)) return null;
                if (interrupted)
                {
                    if (!File.Exists(Path.Combine(plan.Directory, "status.prepared")) || File.Exists(Path.Combine(plan.Directory, "status.committed"))) return null;
                }
                else if (!File.Exists(Path.Combine(plan.Directory, "status.committed"))) return null;
                if (!interrupted) VerifyFiles(plan.Target, plan.Incoming);
                VerifyFiles(Path.Combine(plan.Directory, "backup"), plan.Previous);
                return new AppUpdateRollback { Version = plan.PreviousVersion, Interrupted = interrupted, PlanPath = path, PlanHash = hash };
            }
            catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; } catch (InvalidOperationException) { return null; }
        }
        internal static bool Busy(AppUpdatePlan plan)
        {
            string path = Path.Combine(plan.Directory, "helper.lock");
            AppUpdateFiles.NoReparse(path);
            if (!File.Exists(path)) return false;
            try { using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } return false; }
            catch (IOException) { return true; }
        }
        internal static AppUpdatePlan LoadPlan(string path, string expectedHash, string root)
        {
            if (!AppUpdateFiles.IsHash(expectedHash) || Path.GetFileName(path) != "plan.json") throw new IOException("Недопустимый план обновления.");
            byte[] bytes = AppUpdateFiles.Read(path, MaximumPlanBytes);
            if (AppUpdateFiles.Hash(bytes) != expectedHash) throw new IOException("План обновления изменился.");
            AppUpdatePlan plan;
            try { plan = new JavaScriptSerializer { MaxJsonLength = MaximumPlanBytes, RecursionLimit = 12 }.Deserialize<AppUpdatePlan>(AppUpdateFiles.Utf8.GetString(bytes)); }
            catch (ArgumentException) { throw new IOException("Недопустимый план обновления."); }
            catch (InvalidOperationException) { throw new IOException("Недопустимый план обновления."); }
            ValidatePlan(plan, root);
            if (!SamePath(Path.Combine(plan.Directory, "plan.json"), path)) throw new IOException("План находится в другой папке.");
            return plan;
        }
        internal static void ValidatePlan(AppUpdatePlan plan, string root)
        {
            if (plan == null || plan.Schema != 1 || plan.ParentId < 1 || plan.ParentStartedUtcTicks < new DateTime(2020, 1, 1).Ticks ||
                plan.ParentStartedUtcTicks > DateTime.UtcNow.AddMinutes(5).Ticks || !AppUpdateFiles.IsHash(plan.ParentSha256) ||
                plan.HealthToken == null || !Regex.IsMatch(plan.HealthToken, @"\A[a-f0-9]{32}\z")) throw new IOException("Недопустимый план обновления.");
            string directory = AppUpdateFiles.LocalDirectory(plan.Directory), target = AppUpdateFiles.LocalDirectory(plan.Target), baseRoot = AppUpdateFiles.LocalDirectory(root);
            Guid id;
            if (!SamePath(Path.GetDirectoryName(directory), baseRoot) || !Guid.TryParseExact(Path.GetFileName(directory), "N", out id) ||
                SamePath(directory, target) || target.StartsWith(baseRoot + "\\", StringComparison.OrdinalIgnoreCase) ||
                directory.StartsWith(target + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("Недопустимая папка назначения обновления.");
            AppUpdateClient.ParseVersion(plan.Version); AppUpdateClient.ParseVersion(plan.PreviousVersion);
            ValidateStates(plan.Incoming); ValidateStates(plan.Previous);
            if (!plan.Incoming.Single(value => value.Name == "LocalLink.exe").Exists || !plan.Previous.Single(value => value.Name == "LocalLink.exe").Exists)
                throw new IOException("Нет приложения для обновления или отката.");
            if (plan.Previous.Single(value => value.Name == "LocalLink.exe").Sha256 != plan.ParentSha256) throw new IOException("Исходное приложение не совпадает с планом.");
        }
        internal static string TargetDirectory(string executable)
        {
            if (String.IsNullOrWhiteSpace(executable) || Path.GetFileName(executable) != "LocalLink.exe") throw new IOException("Обновление поддерживает только LocalLink.exe.");
            string directory = AppUpdateFiles.LocalDirectory(Path.GetDirectoryName(executable)); AppUpdateFiles.NoReparse(executable);
            if (!File.Exists(executable)) throw new IOException("Установленная копия LocalLink не найдена."); return directory;
        }
        internal static bool SamePath(string left, string right)
        { return String.Equals(Path.GetFullPath(left).TrimEnd('\\', '/'), Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
        internal static AppUpdateFileState[] Snapshot(string directory)
        {
            AppUpdateFiles.NoReparse(directory);
            return AppUpdateFiles.Names.Select(name =>
            {
                string path = Path.Combine(directory, name); AppUpdateFiles.NoReparse(path);
                if (Directory.Exists(path)) throw new IOException("Вместо файла приложения находится папка.");
                bool exists = File.Exists(path); long bytes = exists ? new FileInfo(path).Length : 0;
                if (exists && (bytes < 1 || bytes > AppUpdateClient.MaximumPackageBytes)) throw new IOException("Недопустимый размер файла приложения.");
                return new AppUpdateFileState { Name = name, Exists = exists, Bytes = bytes, Sha256 = exists ? AppUpdateFiles.HashFile(path) : "" };
            }).ToArray();
        }
        static void ValidateStates(AppUpdateFileState[] states)
        {
            if (states == null || states.Length != AppUpdateFiles.Names.Length) throw new IOException("Недопустимый список файлов обновления.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in states)
                if (value == null || !AppUpdateFiles.Names.Contains(value.Name, StringComparer.Ordinal) || !names.Add(value.Name) ||
                    (value.Exists ? value.Bytes < 1 || value.Bytes > AppUpdateClient.MaximumPackageBytes || !AppUpdateFiles.IsHash(value.Sha256) : value.Bytes != 0 || value.Sha256 != ""))
                    throw new IOException("Недопустимый файл обновления.");
        }
        internal static void VerifyFiles(string directory, AppUpdateFileState[] states)
        {
            ValidateStates(states); AppUpdateFiles.NoReparse(directory);
            foreach (var value in states)
            {
                string path = Path.Combine(directory, value.Name); AppUpdateFiles.NoReparse(path);
                if (Directory.Exists(path) || File.Exists(path) != value.Exists || (value.Exists && (new FileInfo(path).Length != value.Bytes || AppUpdateFiles.HashFile(path) != value.Sha256)))
                    throw new IOException("Файлы приложения изменились после подготовки обновления.");
            }
        }
        static void CopyStates(string source, string destination, AppUpdateFileState[] states)
        {
            VerifyFiles(source, states); AppUpdateFiles.NoReparse(destination);
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Резервная папка уже существует.");
            Directory.CreateDirectory(destination);
            foreach (var value in states.Where(value => value.Exists))
            {
                string to = Path.Combine(destination, value.Name); AppUpdateFiles.NoReparse(to);
                using (var input = new FileStream(Path.Combine(source, value.Name), FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { AppUpdateFiles.CopyBounded(input, output, value.Bytes, CancellationToken.None); output.Flush(true); }
            }
            VerifyFiles(destination, states);
        }
        internal static void ApplyFiles(AppUpdatePlan plan, Action<int> afterFile)
        {
            VerifyFiles(plan.Target, plan.Previous); VerifyFiles(Path.Combine(plan.Directory, "payload"), plan.Incoming); VerifyFiles(Path.Combine(plan.Directory, "backup"), plan.Previous);
            try
            {
                int count = 0;
                foreach (var value in plan.Incoming) { ReplaceOne(plan.Target, Path.Combine(plan.Directory, "payload"), value); if (afterFile != null) afterFile(++count); }
                VerifyFiles(plan.Target, plan.Incoming); Mark(plan, "installed");
            }
            catch { RestoreFiles(plan); throw; }
        }
        internal static void RestoreFiles(AppUpdatePlan plan)
        {
            VerifyFiles(Path.Combine(plan.Directory, "backup"), plan.Previous);
            foreach (var value in plan.Previous) ReplaceOne(plan.Target, Path.Combine(plan.Directory, "backup"), value);
            VerifyFiles(plan.Target, plan.Previous); Mark(plan, "rolled-back");
        }
        static void ReplaceOne(string target, string source, AppUpdateFileState value)
        {
            string path = Path.Combine(target, value.Name); AppUpdateFiles.NoReparse(target); AppUpdateFiles.NoReparse(path);
            if (Directory.Exists(path)) throw new IOException("Вместо файла приложения находится папка.");
            if (!value.Exists) { if (File.Exists(path)) File.Delete(path); return; }
            string temp = Path.Combine(target, ".locallink-update-" + Guid.NewGuid().ToString("N")); AppUpdateFiles.NoReparse(temp);
            using (var input = new FileStream(Path.Combine(source, value.Name), FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { AppUpdateFiles.CopyBounded(input, output, value.Bytes, CancellationToken.None); output.Flush(true); }
            if (AppUpdateFiles.HashFile(temp) != value.Sha256) throw new IOException("Файл обновления изменился при установке.");
            AppUpdateFiles.NoReparse(path);
            ReplaceWithRetry(path, temp, value, delegate
            {
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }, Thread.Sleep);
        }
        internal static void ReplaceWithRetry(string path, string temporary, AppUpdateFileState replacement, Action replace, Action<int> delay)
        {
            if (replacement == null || !replacement.Exists || !AppUpdateFiles.IsHash(replacement.Sha256) || replacement.Bytes < 0 || replace == null || delay == null)
                throw new ArgumentException("Недопустимый файл замены.");
            AppUpdateFileState original = ReadRetryState(path);
            int[] pauses = { 25, 50, 100, 200, 400, 400, 400 }; int retry = 0;
            var clock = Stopwatch.StartNew();
            while (true)
            {
                // 1175 preserves both names; 1176/1177 can move/delete names and
                // must not be retried. No ACL/merge-error bypass flags are used.
                // https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-replacefilew
                RequireRetryState(path, original); RequireRetryState(temporary, replacement);
                try { replace(); return; }
                catch (IOException error)
                {
                    uint hresult = unchecked((uint)error.HResult);
                    int code = (int)(hresult & 0xffff);
                    if ((hresult & 0xffff0000U) != 0x80070000U || (code != 32 && code != 33 && code != 1175) ||
                        retry >= pauses.Length || clock.ElapsedMilliseconds + pauses[retry] > 2000) throw;
                    delay(pauses[retry++]);
                }
            }
        }
        static AppUpdateFileState ReadRetryState(string path)
        {
            AppUpdateFiles.NoReparse(path);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { return new AppUpdateFileState { Exists = false }; }
            catch (DirectoryNotFoundException) { return new AppUpdateFileState { Exists = false }; }
            if ((attributes & FileAttributes.Directory) != 0) throw new IOException("Вместо файла приложения находится папка.");
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var hash = SHA256.Create())
            {
                long length = input.Length;
                string checksum = BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
                if (input.Length != length || input.Position != length) throw new IOException("Файл приложения изменился при проверке.");
                return new AppUpdateFileState { Exists = true, Bytes = length, Sha256 = checksum };
            }
        }
        static void RequireRetryState(string path, AppUpdateFileState expected)
        {
            AppUpdateFileState actual = ReadRetryState(path);
            if (actual.Exists != expected.Exists || actual.Exists && (actual.Bytes != expected.Bytes || actual.Sha256 != expected.Sha256))
                throw new IOException("Файл обновления изменился между попытками замены.");
        }
        internal static void Mark(AppUpdatePlan plan, string status)
        {
            if (status != "prepared" && status != "installed" && status != "committed" && status != "rolled-back" && status != "failed") throw new IOException("Недопустимое состояние обновления.");
            string path = Path.Combine(plan.Directory, "status." + status);
            if (!File.Exists(path)) AppUpdateFiles.WriteNew(path, AppUpdateFiles.Utf8.GetBytes(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)));
        }
        internal static void SavePointer(AppUpdatePlan plan, string hash)
        { WritePointer(plan, hash, "last-update.json"); }
        internal static void CommitHealthy(AppUpdatePlan plan,string hash)
        {
            // Keep the existing pending pointer as a fallback if metadata can't
            // be committed (e.g. disk full / locked last-update.json). A healthy
            // running application must not be rolled back for this reason.
            try{Mark(plan,"committed");}catch(IOException){}catch(UnauthorizedAccessException){}
            try{SavePointer(plan,hash);}catch(IOException){}catch(UnauthorizedAccessException){}
        }
        internal static void SavePending(AppUpdatePlan plan, string hash)
        { Mark(plan, "prepared"); WritePointer(plan, hash, "pending-update.json"); }
        static void WritePointer(AppUpdatePlan plan, string hash, string filename)
        {
            string root = Path.GetDirectoryName(plan.Directory), target = Path.Combine(root, filename), stage = Path.Combine(root, "pointer-" + Guid.NewGuid().ToString("N") + ".json");
            AppUpdateFiles.WriteNew(stage, AppUpdateFiles.Json(new { Plan = Path.Combine(plan.Directory, "plan.json"), Sha256 = hash }));
            AppUpdateFiles.NoReparse(target);
            if (File.Exists(target)) File.Replace(stage, target, null); else File.Move(stage, target);
        }
    }

    internal static class AppUpdateFiles
    {
        internal static readonly string[] Names = { "LocalLink.exe", "LocalLink.exe.config", "LICENSE", "THIRD-PARTY.md" };
        internal static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        internal static bool IsHash(string text) { return text != null && Regex.IsMatch(text, @"\A[a-f0-9]{64}\z"); }
        internal static string Hash(byte[] bytes) { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        internal static string HashFile(string path)
        { NoReparse(path); using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "").ToLowerInvariant(); }
        internal static string LocalDirectory(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || !Regex.IsMatch(value, @"\A[A-Za-z]:[\\/]") || value.StartsWith(@"\\", StringComparison.Ordinal) || value.IndexOf(':', 2) >= 0)
                throw new IOException("Нужна локальная папка обновления.");
            string result = Path.GetFullPath(value).TrimEnd('\\', '/');
            if (result.Length < 4 || result == Path.GetPathRoot(result).TrimEnd('\\', '/')) throw new IOException("Нужна отдельная папка обновления.");
            NoReparse(result); return result;
        }
        internal static void NoReparse(string path)
        {
            for (string current = Path.GetFullPath(path); !String.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                FileAttributes value;
                try { value = File.GetAttributes(current); } catch (FileNotFoundException) { continue; } catch (DirectoryNotFoundException) { continue; }
                if ((value & FileAttributes.ReparsePoint) != 0) throw new IOException("Папка обновления содержит перенаправление.");
            }
        }
        internal static void WriteNew(string path, byte[] bytes)
        { NoReparse(path); using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); } }
        internal static byte[] Read(string path, int maximum)
        {
            NoReparse(path); using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length < 1 || input.Length > maximum) throw new IOException("Недопустимый размер файла обновления.");
                using (var output = new MemoryStream()) { CopyBounded(input, output, input.Length, CancellationToken.None); return output.ToArray(); }
            }
        }
        internal static void CopyBounded(Stream input, Stream output, long expected, CancellationToken token)
        {
            var buffer = new byte[32768]; int read; long count = 0;
            while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
            { token.ThrowIfCancellationRequested(); count += read; if (count > expected) throw new IOException("Размер файла обновления изменился."); output.Write(buffer, 0, read); }
            if (count != expected) throw new IOException("Файл обновления скачан не полностью.");
        }
        internal static void ValidateApplication(string path, string version)
        {
            NoReparse(path); AssemblyName assembly;
            try { assembly = AssemblyName.GetAssemblyName(path); } catch (BadImageFormatException) { throw new IOException("Архив не содержит приложение LocalLink."); }
            var actual = new Version(assembly.Version.Major, assembly.Version.Minor, assembly.Version.Build);
            if (actual != AppUpdateClient.ParseVersion(version)) throw new IOException("Версия приложения не соответствует релизу.");
            var metadata = FileVersionInfo.GetVersionInfo(path);
            if (metadata.ProductName != "LocalLink") throw new IOException("Архив не содержит приложение LocalLink.");
        }
        internal static byte[] Json(object value) { return Utf8.GetBytes(new JavaScriptSerializer { MaxJsonLength = 256 * 1024, RecursionLimit = 20 }.Serialize(value)); }
        internal static Dictionary<string, object> Object(byte[] bytes, int maximum)
        {
            try
            {
                string json = Utf8.GetString(bytes);
                var result = new JavaScriptSerializer { MaxJsonLength = maximum, RecursionLimit = 20 }.DeserializeObject(json) as Dictionary<string, object>;
                if (result == null) throw new IOException("Недопустимый документ обновления."); return result;
            }
            catch (ArgumentException) { throw new IOException("Недопустимый документ обновления."); }
            catch (InvalidOperationException) { throw new IOException("Недопустимый документ обновления."); }
        }
        internal static void Keys(Dictionary<string, object> value, params string[] names)
        { if (value.Count != names.Length || names.Any(name => !value.ContainsKey(name))) throw new IOException("Недопустимые поля документа обновления."); }
        internal static string Text(Dictionary<string, object> value, string key, int maximum)
        { object raw; string text = value.TryGetValue(key, out raw) ? raw as string : null; if (text == null || text.Length < 1 || text.Length > maximum || text.Any(Char.IsControl)) throw new IOException("Недопустимое поле обновления."); return text; }
        internal static bool Boolean(Dictionary<string, object> value, string key)
        { object raw; if (!value.TryGetValue(key, out raw) || !(raw is bool)) throw new IOException("Недопустимое поле обновления."); return (bool)raw; }
        internal static long Number(Dictionary<string, object> value, string key, long minimum, long maximum)
        { object raw; if (!value.TryGetValue(key, out raw) || !(raw is int || raw is long)) throw new IOException("Недопустимое число обновления."); long number = Convert.ToInt64(raw, CultureInfo.InvariantCulture); if (number < minimum || number > maximum) throw new IOException("Недопустимое число обновления."); return number; }
    }
}
