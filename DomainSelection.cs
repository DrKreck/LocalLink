using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace LocalLink
{
    public sealed class DomainCategory
    {
        public string Id, Name;
    }

    public sealed class DomainSearchRow
    {
        public string Domain, CategoryId, CategoryName;
        public bool Enabled, CategoryEnabled, InheritedExclusion;
    }

    public sealed class DomainSearchResult
    {
        public readonly List<DomainSearchRow> Rows = new List<DomainSearchRow>();
        public bool HasMore;
    }

    public static class DomainSelection
    {
        public const int MaximumRules = 2048;
        public const int MaximumPageSize = 200;
        private static readonly Dictionary<string, string> KnownRoots = BuildRoots();

        public static List<DomainCategory> Categories()
        {
            var result = new List<DomainCategory>();
            foreach (var service in ServiceCatalog.All())
                result.Add(new DomainCategory { Id = service.Id, Name = service.Name + (service.Id=="steam"||service.Id=="arma"||service.Id=="tarkov"||service.Id=="dcs"?" · веб":"") });
            result.Add(new DomainCategory { Id = "custom", Name = "Свои домены" });
            result.Add(new DomainCategory { Id = "public", Name = "Остальной публичный список" });
            return result;
        }

        public static List<string> NormalizeDisabled(IEnumerable<string> values)
        {
            var unique = new SortedSet<string>(StringComparer.Ordinal);
            int visited = 0;
            if (values != null)
                foreach (string value in values)
                {
                    if (++visited > MaximumRules)
                        throw new ArgumentException("Слишком много исключений доменов. Максимум: " + MaximumRules + ".");
                    string domain = PublicLists.NormalizeDomain(value);
                    if (domain != null) unique.Add(domain);
                }
            return new List<string>(unique);
        }

        // Exclusions are suffix rules, not string fragments. A disabled root
        // also excludes its subdomains even if a public list includes a parent.
        public static string[] Exclusions(AppSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            var result = new SortedSet<string>(NormalizeDisabled(settings.DisabledDomains), StringComparer.Ordinal);
            var selected = new HashSet<string>(settings.Services ?? new List<string>(), StringComparer.Ordinal);
            foreach (var service in ServiceCatalog.All())
                if (!selected.Contains(service.Id))
                    foreach (string domain in service.Domains) result.Add(domain);
            if (!settings.UseCustomDomains)
                foreach (string domain in Custom(settings)) result.Add(domain);
            var compact = new List<string>();
            foreach (string domain in result)
                if (!HasParent(result, domain)) compact.Add(domain);
            return compact.ToArray();
        }

        public static bool IsExcluded(string domain, IEnumerable<string> exclusions)
        {
            string normalized = PublicLists.NormalizeDomain(domain);
            if (normalized == null) throw new ArgumentException("Некорректный домен.", "domain");
            var rules = new HashSet<string>(exclusions ?? new string[0], StringComparer.Ordinal);
            return rules.Contains(normalized) || HasParent(rules, normalized);
        }

        // A separate immutable snapshot; never replaces the public-list pointer.
        // The caller uses a worker and the engine keeps OpenVerified's read guard.
        public static string WriteExclusions(AppSettings settings, string folder, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string[] domains = Exclusions(settings);
            if (domains.Length == 0) return "";
            string full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
            string user = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalLink", "lists")).TrimEnd(Path.DirectorySeparatorChar);
            string bundled = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lists")).TrimEnd(Path.DirectorySeparatorChar);
            if (!String.Equals(full, user, StringComparison.OrdinalIgnoreCase) && !String.Equals(full, bundled, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Исключения должны находиться в папке списков LocalLink.");
            RejectReparse(full);
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(String.Join("\n", domains) + "\n");
            string digest;
            using (var sha = SHA256.Create()) digest = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            string name = "sites-excluded-" + digest.Substring(0, 24) + ".lst";
            string path = Path.Combine(full, name), passport = path + ".json";
            RejectReparse(path); RejectReparse(passport);
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(full);
            token.ThrowIfCancellationRequested();
            WriteNewIfMissing(path, bytes, token);
            var snapshot = new ListSnapshot { FileName = name, Sha256 = digest, DomainCount = domains.Length,
                SourceCount = 0, RejectedLines = 0, Sources = new string[0], UpdatedUtc = DateTime.UtcNow.ToString("o") };
            WriteNewIfMissing(passport, new UTF8Encoding(false, true).GetBytes(new JavaScriptSerializer().Serialize(snapshot)), token);
            int verifiedCount;
            using (var guard = PublicLists.OpenVerified(path, token, out verifiedCount))
            {
                if (verifiedCount != domains.Length) throw new IOException("Неверное число исключений доменов.");
                if (guard.Length != bytes.Length) throw new IOException("Содержимое исключений не соответствует настройкам.");
                var actual = new byte[8192]; int position = 0, count;
                while ((count = guard.Read(actual, 0, actual.Length)) != 0)
                {
                    token.ThrowIfCancellationRequested();
                    for (int index = 0; index < count; index++)
                        if (actual[index] != bytes[position + index]) throw new IOException("Содержимое исключений не соответствует настройкам.");
                    position += count;
                }
                if (position != bytes.Length) throw new IOException("Содержимое исключений не соответствует настройкам.");
            }
            return path;
        }

        private static void WriteNewIfMissing(string path, byte[] bytes, CancellationToken token)
        {
            RejectReparse(path);
            if (File.Exists(path)) return;
            string stage = path + ".stage-" + Guid.NewGuid().ToString("N");
            RejectReparse(stage);
            using (var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                token.ThrowIfCancellationRequested();
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            RejectReparse(path);
            try { File.Move(stage, path); }
            catch (IOException) { if (!File.Exists(path)) throw; }
        }

        private static void RejectReparse(string path)
        {
            for (string current = Path.GetFullPath(path); !String.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                FileAttributes flags;
                try { flags = File.GetAttributes(current); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                if ((flags & FileAttributes.ReparsePoint) != 0) throw new IOException("Путь исключений не должен быть символической ссылкой.");
            }
        }

        // Restore only the selected rule. Never silently remove a wider rule
        // (or enable a whole category) while the user clicks a child domain.
        public static void SetEnabled(AppSettings settings, string domain, bool enabled)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            domain = PublicLists.NormalizeDomain(domain);
            if (domain == null) throw new ArgumentException("Некорректный домен.", "domain");
            var rules = new HashSet<string>(NormalizeDisabled(settings.DisabledDomains), StringComparer.Ordinal);
            if (enabled && HasParent(rules, domain))
                throw new InvalidOperationException("Сначала включите родительский домен: " + ParentRule(rules, domain));
            if (enabled) rules.Remove(domain);
            else rules.Add(domain);
            settings.DisabledDomains = NormalizeDisabled(rules);
        }

        public static DomainSearchResult Search(AppSettings settings, string publicListPath,
            string categoryId, string query, int skip, int take, CancellationToken token)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            token.ThrowIfCancellationRequested();
            if (skip < 0 || skip > PublicLists.MaxDomains || take < 1 || take > MaximumPageSize)
                throw new ArgumentOutOfRangeException("take", "Размер страницы должен быть от 1 до " + MaximumPageSize + ".");
            categoryId = categoryId ?? "";
            query = (query ?? "").Trim();
            if (query.Length > 253) throw new ArgumentException("Слишком длинный запрос.", "query");
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var category in Categories()) names.Add(category.Id, category.Name);
            if (categoryId != "" && !names.ContainsKey(categoryId)) throw new ArgumentException("Неизвестная категория.", "categoryId");
            var selected = new HashSet<string>(settings.Services ?? new List<string>(), StringComparer.Ordinal);
            var custom = new HashSet<string>(Custom(settings), StringComparer.Ordinal);
            var disabled = new HashSet<string>(NormalizeDisabled(settings.DisabledDomains), StringComparer.Ordinal);
            var local = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var service in ServiceCatalog.All()) foreach (string domain in service.Domains) local.Add(domain);
            foreach (string domain in custom) local.Add(domain);
            foreach (string domain in disabled) local.Add(domain);
            var result = new DomainSearchResult();
            int matched = 0;
            foreach (string domain in local)
            {
                token.ThrowIfCancellationRequested();
                if (Append(result, domain, categoryId, query, skip, take, ref matched, names, selected, custom, disabled, settings))
                    return result;
            }
            if (!String.IsNullOrEmpty(publicListPath))
            {
                using (var stream = PublicLists.OpenVerified(publicListPath, token))
                using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 32768))
                {
                    string domain; int lines = 0;
                    while ((domain = reader.ReadLine()) != null)
                    {
                        if ((++lines & 255) == 0) token.ThrowIfCancellationRequested();
                        if (local.Contains(domain)) continue;
                        if (Append(result, domain, categoryId, query, skip, take, ref matched, names, selected, custom, disabled, settings))
                            return result;
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            return result;
        }

        private static bool Append(DomainSearchResult result, string domain, string filterCategory, string query,
            int skip, int take, ref int matched, Dictionary<string, string> names, HashSet<string> selected,
            HashSet<string> custom, HashSet<string> disabled, AppSettings settings)
        {
            string category = CategoryOf(domain, custom);
            if (filterCategory != "" && filterCategory != category) return false;
            if (query.Length > 0 && domain.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (matched++ < skip) return false;
            if (result.Rows.Count == take) { result.HasMore = true; return true; }
            bool enabledCategory = category == "public" ? settings.UsePublicLists :
                category == "custom" ? settings.UseCustomDomains : selected.Contains(category);
            bool inherited = HasParent(disabled, domain);
            // Custom category exclusions can also cover entries classified as
            // service/public domains. Keep displayed and effective state equal.
            if (settings.UseCustomDomains == false && (custom.Contains(domain) || HasParent(custom, domain)))
                enabledCategory = false;
            result.Rows.Add(new DomainSearchRow { Domain = domain, CategoryId = category, CategoryName = names[category],
                CategoryEnabled = enabledCategory, InheritedExclusion = inherited,
                Enabled = enabledCategory && !disabled.Contains(domain) && !inherited });
            return false;
        }

        private static string CategoryOf(string domain, HashSet<string> custom)
        {
            string current = domain, id;
            while (true)
            {
                if (KnownRoots.TryGetValue(current, out id)) return id;
                int dot = current.IndexOf('.');
                if (dot < 0) break;
                current = current.Substring(dot + 1);
            }
            if (custom.Contains(domain) || HasParent(custom, domain)) return "custom";
            return "public";
        }

        private static Dictionary<string, string> BuildRoots()
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var service in ServiceCatalog.All())
                foreach (string root in service.Domains) result[root] = service.Id;
            return result;
        }

        private static string[] Custom(AppSettings settings)
        {
            if (String.IsNullOrWhiteSpace(settings.CustomDomains)) return new string[0];
            return Profiles.ValidateDomains(settings.CustomDomains);
        }

        private static bool HasParent(ICollection<string> rules, string domain)
        { return ParentRule(rules, domain) != null; }

        private static string ParentRule(ICollection<string> rules, string domain)
        {
            int dot = domain.IndexOf('.');
            while (dot >= 0)
            {
                domain = domain.Substring(dot + 1);
                if (rules.Contains(domain)) return domain;
                dot = domain.IndexOf('.');
            }
            return null;
        }
    }
}
