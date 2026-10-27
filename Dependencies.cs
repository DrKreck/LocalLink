using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LocalLink
{
    public sealed class DependencyStatus
    {
        public bool EngineReady { get; internal set; }
        public bool TelegramReady { get; internal set; }
        public bool Ready { get; internal set; }
        public string[] MissingComponents { get; internal set; }
    }

    public sealed class DependencyProgress
    {
        public string Message { get; private set; }
        public int Percent { get; private set; }
        public DependencyProgress(string message, int percent)
        { Message = message; Percent = Math.Max(0, Math.Min(100, percent)); }
    }

    public sealed class DependencyInstaller
    {
        internal sealed class LockedFile
        {
            internal string Path, Sha256, Url;
            internal long Size;
        }
        internal sealed class InstallPlan
        {
            internal LockedFile[] EngineFiles, RuntimeFiles;
            internal string RuntimeUrl, RuntimeSha256;
            internal long RuntimeSize;
        }
        private sealed class EngineLock { public EngineEntry[] files { get; set; } }
        private sealed class EngineEntry { public string path { get; set; } public string sha256 { get; set; } public string url { get; set; } public long size { get; set; } }
        private sealed class RuntimeLock { public RuntimeEntry[] Files { get; set; } }
        private sealed class RuntimeEntry { public string Path { get; set; } public string SHA256 { get; set; } public long Bytes { get; set; } }
        private sealed class DownloadLock { public int schemaVersion { get; set; } public ArchiveEntry telegramArchive { get; set; } }
        private sealed class ArchiveEntry { public string url { get; set; } public string sha256 { get; set; } public long size { get; set; } }

        private readonly string root;
        private readonly InstallPlan plan;
        private readonly Func<Uri, string, long, IProgress<DependencyProgress>, CancellationToken, Task> download;
        private sealed class ScaledProgress : IProgress<DependencyProgress>
        {
            private readonly IProgress<DependencyProgress> target;
            private readonly string message;
            private readonly int start, end;
            internal ScaledProgress(IProgress<DependencyProgress> target, string message, int start, int end)
            { this.target = target; this.message = message; this.start = start; this.end = end; }
            public void Report(DependencyProgress value)
            { DependencyInstaller.Report(target, message, start + (end - start) * value.Percent / 100); }
        }
        public string ComponentsRoot { get { return root; } }
        public static string DefaultComponentsRoot
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalLink", "components", "0.8.2"); }
        }
        public DependencyInstaller(string componentsRoot) : this(componentsRoot, LoadPlan(), DownloadAsync) { }
        internal DependencyInstaller(string componentsRoot, InstallPlan installPlan,
            Func<Uri, string, long, IProgress<DependencyProgress>, CancellationToken, Task> downloader)
        {
            if (String.IsNullOrWhiteSpace(componentsRoot)) throw new ArgumentException("Не задана папка компонентов.");
            root = Path.GetFullPath(componentsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (Path.GetPathRoot(root).TrimEnd('\\', '/') == root.TrimEnd('\\', '/')) throw new ArgumentException("Нужна отдельная папка компонентов.");
            plan = installPlan; download = downloader;
            ValidatePlan(plan);
            if (download == null) throw new ArgumentNullException("downloader");
        }

        public DependencyStatus Inspect(bool includeTelegram)
        { return Inspect(includeTelegram, CancellationToken.None); }
        public DependencyStatus Inspect(bool includeTelegram, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); CheckNoReparse(root);
            bool engine = VerifyFolder(Path.Combine(root, "engine"), plan.EngineFiles, token);
            bool telegram = includeTelegram && VerifyFolder(Path.Combine(root, "telegram", "runtime"), plan.RuntimeFiles, token);
            var missing = new List<string>();
            if (!engine) missing.Add("Сетевой модуль");
            if (includeTelegram && !telegram) missing.Add("Модуль Telegram");
            return new DependencyStatus { EngineReady = engine, TelegramReady = telegram,
                Ready = engine && (!includeTelegram || telegram), MissingComponents = missing.ToArray() };
        }

        public Task InstallAsync(bool includeTelegram, IProgress<DependencyProgress> progress, CancellationToken token)
        {
            return Task.Run(async delegate { await InstallCore(includeTelegram, progress, token).ConfigureAwait(false); }, token);
        }

        private async Task InstallCore(bool includeTelegram, IProgress<DependencyProgress> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            CheckNoReparse(root); Directory.CreateDirectory(root); CheckNoReparse(root);
            string lockPath = SafePath(root, ".install.lock");
            FileStream installationLock;
            try { installationLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) { throw new IOException("Подготовка компонентов уже выполняется.", ex); }
            using (installationLock)
            {
                string stage = Path.Combine(root, ".install-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stage); CheckNoReparse(stage);
                try
                {
                    string engine = Path.Combine(root, "engine");
                    if (!VerifyFolder(engine, plan.EngineFiles, token))
                    {
                        string stagedEngine = Path.Combine(stage, "engine");
                        Directory.CreateDirectory(stagedEngine);
                        for (int i = 0; i < plan.EngineFiles.Length; i++)
                        {
                            token.ThrowIfCancellationRequested();
                            LockedFile entry = plan.EngineFiles[i];
                            Report(progress, "Скачивание сетевого модуля…", i * 45 / plan.EngineFiles.Length);
                            string target = SafePath(stagedEngine, entry.Path);
                            Directory.CreateDirectory(Path.GetDirectoryName(target)); CheckNoReparse(target);
                            string previous = SafePath(engine, entry.Path);
                            if (VerifyFile(previous, entry, token)) File.Copy(previous, target, false);
                            else await download(new Uri(entry.Url), target, entry.Size,
                                new ScaledProgress(progress, "Скачивание сетевого модуля…", i * 45 / plan.EngineFiles.Length, (i + 1) * 45 / plan.EngineFiles.Length), token).ConfigureAwait(false);
                            if (!VerifyFile(target, entry, token)) throw new IOException("Не удалось проверить сетевой модуль. Повторите установку.");
                        }
                        token.ThrowIfCancellationRequested();
                        if (!VerifyFolder(stagedEngine, plan.EngineFiles, token)) throw new IOException("Сетевой модуль загружен не полностью.");
                        CommitFolder(stagedEngine, engine, token);
                    }
                    Report(progress, "Сетевой модуль готов", includeTelegram ? 45 : 100);
                    string runtime = Path.Combine(root, "telegram", "runtime");
                    if (includeTelegram && !VerifyFolder(runtime, plan.RuntimeFiles, token))
                    {
                        token.ThrowIfCancellationRequested();
                        string archive = SafePath(stage, "telegram-runtime.zip");
                        Report(progress, "Скачивание модуля Telegram…", 50);
                        await download(new Uri(plan.RuntimeUrl), archive, plan.RuntimeSize,
                            new ScaledProgress(progress, "Скачивание модуля Telegram…", 50, 78), token).ConfigureAwait(false);
                        if (!VerifyFile(archive, new LockedFile { Size = plan.RuntimeSize, Sha256 = plan.RuntimeSha256 }, token))
                            throw new IOException("Не удалось проверить модуль Telegram. Повторите установку.");
                        string stagedRuntime = Path.Combine(stage, "runtime");
                        Directory.CreateDirectory(stagedRuntime);
                        Report(progress, "Подготовка Telegram…", 80);
                        ExtractRuntime(archive, stagedRuntime, plan.RuntimeFiles, token);
                        if (!VerifyFolder(stagedRuntime, plan.RuntimeFiles, token)) throw new IOException("Модуль Telegram загружен не полностью.");
                        Directory.CreateDirectory(Path.GetDirectoryName(runtime)); CheckNoReparse(runtime);
                        CommitFolder(stagedRuntime, runtime, token);
                    }
                    token.ThrowIfCancellationRequested();
                    if (!Inspect(includeTelegram, token).Ready) throw new IOException("Компоненты ещё не готовы. Повторите установку.");
                    Report(progress, "Готово", 100);
                }
                finally { CleanStage(stage); }
            }
        }

        private static void CommitFolder(string stage, string destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); CheckNoReparse(stage); CheckNoReparse(destination);
            if (File.Exists(destination)) throw new IOException("Папка компонентов занята другим файлом.");
            string previous = destination + ".previous-" + Guid.NewGuid().ToString("N");
            bool moved = false;
            if (Directory.Exists(destination)) { Directory.Move(destination, previous); moved = true; }
            try { Directory.Move(stage, destination); }
            catch
            {
                if (moved && !Directory.Exists(destination)) Directory.Move(previous, destination);
                throw;
            }
            // A replaced incomplete installation stays beside the new one for rollback.
        }

        internal static void ExtractRuntime(string archive, string destination, LockedFile[] files, CancellationToken token)
        {
            CheckNoReparse(destination);
            var expected = new Dictionary<string, LockedFile>(StringComparer.OrdinalIgnoreCase);
            foreach (LockedFile file in files) expected.Add(file.Path, file);
            var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var input = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var zip = new ZipArchive(input, ZipArchiveMode.Read, false))
            {
                if (zip.Entries.Count > files.Length + 1024) throw new IOException("В архиве слишком много файлов.");
                long total = 0;
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    string name = entry.FullName;
                    bool directory = name.EndsWith("/", StringComparison.Ordinal);
                    string relative = directory ? name.TrimEnd('/') : name;
                    ValidateRelative(relative);
                    int mode = (entry.ExternalAttributes >> 16) & 0xF000;
                    if (mode == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Ссылки в архиве недопустимы.");
                    string target = SafePath(destination, relative);
                    CheckNoReparse(target);
                    if (directory) continue;
                    if (String.Equals(relative, "runtime-manifest.json", StringComparison.Ordinal)) continue;
                    LockedFile locked;
                    if (!expected.TryGetValue(relative, out locked) || !extracted.Add(relative)) throw new IOException("Неожиданный файл в архиве компонентов.");
                    if (entry.Length != locked.Size) throw new IOException("Размер файла в архиве не совпал.");
                    total = checked(total + entry.Length);
                    if (total > 268435456L) throw new IOException("Архив компонентов слишком большой.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)); CheckNoReparse(target);
                    using (var source = entry.Open())
                    using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        CopyBounded(source, output, locked.Size, token);
                    if (!VerifyFile(target, locked, token)) throw new IOException("Файл в архиве не прошёл проверку.");
                }
            }
            if (extracted.Count != expected.Count) throw new IOException("В архиве не хватает компонентов.");
        }

        private static async Task DownloadAsync(Uri source, string destination, long size,
            IProgress<DependencyProgress> progress, CancellationToken token)
        {
            ValidateDownloadUri(source);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            using (var client = new HttpClient(handler))
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(3));
                client.Timeout = TimeSpan.FromMinutes(3);
                Uri current = source;
                for (int redirects = 0; redirects <= 5; redirects++)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    using (var request = new HttpRequestMessage(HttpMethod.Get, current))
                    {
                        request.Headers.UserAgent.ParseAdd("LocalLink/0.8.2");
                        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                        {
                            int code = (int)response.StatusCode;
                            if (code == 301 || code == 302 || code == 303 || code == 307 || code == 308)
                            {
                                if (redirects == 5 || response.Headers.Location == null) throw new IOException("Не удалось скачать компоненты.");
                                current = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(current, response.Headers.Location);
                                ValidateDownloadUri(current);
                                if (source.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) && !current.Host.Equals(source.Host, StringComparison.OrdinalIgnoreCase))
                                    throw new IOException("Адрес загрузки компонентов изменился.");
                                continue;
                            }
                            response.EnsureSuccessStatusCode();
                            if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value != size)
                                throw new IOException("Размер загрузки компонентов не совпал.");
                            try
                            {
                                using (timeout.Token.Register(delegate { response.Dispose(); }))
                                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                                {
                                    byte[] buffer = new byte[65536]; long total = 0;
                                    for (;;)
                                    {
                                        int count = await input.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false);
                                        if (count == 0) break;
                                        total += count;
                                        if (total > size) throw new IOException("Размер загрузки компонентов превышен.");
                                        await output.WriteAsync(buffer, 0, count, timeout.Token).ConfigureAwait(false);
                                        Report(progress, "Скачивание…", (int)(total * 100 / size));
                                    }
                                    if (total != size) throw new IOException("Загрузка компонентов прервалась. Повторите установку.");
                                }
                            }
                            catch { timeout.Token.ThrowIfCancellationRequested(); throw; }
                            return;
                        }
                    }
                }
                throw new IOException("Не удалось скачать компоненты.");
            }
        }

        private static void ValidateDownloadUri(Uri source)
        {
            if (source == null || !source.IsAbsoluteUri || source.Scheme != "https" || !String.IsNullOrEmpty(source.UserInfo) || source.Port != 443)
                throw new IOException("Недопустимый адрес компонентов.");
            string host = source.DnsSafeHost.ToLowerInvariant();
            if (host != "raw.githubusercontent.com" && host != "github.com" && host != "release-assets.githubusercontent.com" && host != "objects.githubusercontent.com")
                throw new IOException("Недопустимый сервер компонентов.");
        }

        private static bool VerifyFolder(string folder, LockedFile[] files, CancellationToken token)
        {
            CheckNoReparse(folder);
            if (!Directory.Exists(folder)) return false;
            foreach (LockedFile file in files)
            {
                token.ThrowIfCancellationRequested();
                if (!VerifyFile(SafePath(folder, file.Path), file, token)) return false;
            }
            return true;
        }
        private static bool VerifyFile(string path, LockedFile entry, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); CheckNoReparse(path);
            if (!File.Exists(path)) return false;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length != entry.Size) return false;
                using (var hash = SHA256.Create())
                {
                    byte[] buffer = new byte[65536]; int count;
                    while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                    { token.ThrowIfCancellationRequested(); hash.TransformBlock(buffer, 0, count, buffer, 0); }
                    hash.TransformFinalBlock(new byte[0], 0, 0);
                    return String.Equals(BitConverter.ToString(hash.Hash).Replace("-", ""), entry.Sha256, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        private static void CopyBounded(Stream input, Stream output, long size, CancellationToken token)
        {
            byte[] buffer = new byte[65536]; long total = 0; int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested(); total += count;
                if (total > size) throw new IOException("Размер файла превышен.");
                output.Write(buffer, 0, count);
            }
            if (total != size) throw new IOException("Неполный файл компонентов.");
        }
        private static string SafePath(string folder, string relative)
        {
            ValidateRelative(relative);
            string prefix = Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string result = Path.GetFullPath(Path.Combine(prefix, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("Недопустимый путь компонентов.");
            return result;
        }
        private static void ValidateRelative(string path)
        {
            if (String.IsNullOrEmpty(path) || path.Length > 220 || path.IndexOf('\\') >= 0 || path.StartsWith("/", StringComparison.Ordinal))
                throw new IOException("Недопустимый путь компонентов.");
            foreach (char value in path)
                if (!(value >= 'a' && value <= 'z') && !(value >= 'A' && value <= 'Z') && !(value >= '0' && value <= '9') && value != '_' && value != '-' && value != '.' && value != '/')
                    throw new IOException("Недопустимый путь компонентов.");
            foreach (string part in path.Split('/'))
            {
                if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".", StringComparison.Ordinal)) throw new IOException("Недопустимый путь компонентов.");
                string device = part.Split('.')[0].ToUpperInvariant();
                if (device == "CON" || device == "PRN" || device == "AUX" || device == "NUL" ||
                    (device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) && device[3] >= '1' && device[3] <= '9'))
                    throw new IOException("Недопустимое имя файла компонентов.");
            }
        }
        internal static void CheckNoReparse(string path)
        {
            string current = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(current))
            {
                if (File.Exists(current) || Directory.Exists(current))
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Папка компонентов не должна быть ссылкой.");
                string parent = Path.GetDirectoryName(current);
                if (String.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
                current = parent;
            }
        }
        private static void CleanStage(string stage)
        {
            try
            {
                CheckNoReparse(stage);
                if (!Directory.Exists(stage)) return;
                DeleteOwnedTree(stage);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        private static void DeleteOwnedTree(string folder)
        {
            CheckNoReparse(folder);
            foreach (string item in Directory.GetFileSystemEntries(folder))
            {
                CheckNoReparse(item);
                if (Directory.Exists(item)) DeleteOwnedTree(item);
                else File.Delete(item);
            }
            Directory.Delete(folder, false);
        }
        private static void Report(IProgress<DependencyProgress> progress, string message, int percent)
        { if (progress != null) progress.Report(new DependencyProgress(message, percent)); }

        private static T Resource<T>(string name)
        {
            using (var stream = typeof(DependencyInstaller).Assembly.GetManifestResourceStream(name))
            {
                if (stream == null) throw new IOException("Неполная сборка LocalLink. Скачайте приложение заново.");
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    return new JavaScriptSerializer { MaxJsonLength = 1048576 }.Deserialize<T>(reader.ReadToEnd());
            }
        }
        internal static InstallPlan LoadPlan()
        {
            EngineLock engine = Resource<EngineLock>("LocalLink.EngineManifest");
            RuntimeLock runtime = Resource<RuntimeLock>("LocalLink.RuntimeManifest");
            DownloadLock dependencies = Resource<DownloadLock>("LocalLink.DependencyManifest");
            if (engine == null || runtime == null || dependencies == null || dependencies.schemaVersion != 1 || engine.files == null || runtime.Files == null || dependencies.telegramArchive == null)
                throw new IOException("Неполный список компонентов LocalLink.");
            var engineFiles = new List<LockedFile>();
            foreach (EngineEntry entry in engine.files)
            {
                const string prefix = "vendor/engine/";
                if (entry.path == null || !entry.path.StartsWith(prefix, StringComparison.Ordinal)) throw new IOException("Неверный список сетевого модуля.");
                engineFiles.Add(new LockedFile { Path = entry.path.Substring(prefix.Length), Sha256 = entry.sha256, Url = entry.url, Size = entry.size });
            }
            var runtimeFiles = new List<LockedFile>();
            foreach (RuntimeEntry entry in runtime.Files) runtimeFiles.Add(new LockedFile { Path = entry.Path, Sha256 = entry.SHA256, Size = entry.Bytes });
            return new InstallPlan { EngineFiles = engineFiles.ToArray(), RuntimeFiles = runtimeFiles.ToArray(), RuntimeUrl = dependencies.telegramArchive.url,
                RuntimeSha256 = dependencies.telegramArchive.sha256, RuntimeSize = dependencies.telegramArchive.size };
        }
        private static void ValidatePlan(InstallPlan value)
        {
            if (value == null || value.EngineFiles == null || value.RuntimeFiles == null || value.EngineFiles.Length == 0 || value.RuntimeFiles.Length == 0 ||
                value.EngineFiles.Length > 100 || value.RuntimeFiles.Length > 2000 || value.RuntimeSize <= 0 || value.RuntimeSize > 67108864 || !IsHash(value.RuntimeSha256))
                throw new IOException("Неверный список компонентов.");
            ValidateDownloadUri(new Uri(value.RuntimeUrl));
            foreach (LockedFile[] group in new[] { value.EngineFiles, value.RuntimeFiles })
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (LockedFile file in group)
                {
                    if (file == null) throw new IOException("Неверный список компонентов.");
                    ValidateRelative(file.Path);
                    if (!seen.Add(file.Path) || !IsHash(file.Sha256) || file.Size < 0 || file.Size > 67108864) throw new IOException("Неверный список компонентов.");
                    if (!String.IsNullOrEmpty(file.Url)) ValidateDownloadUri(new Uri(file.Url));
                }
            }
        }
        private static bool IsHash(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f') && !(c >= 'A' && c <= 'F')) return false;
            return true;
        }
    }
}
