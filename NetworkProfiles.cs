using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace LocalLink
{
    public sealed class NetworkConnectionObservation
    {
        public Guid NetworkId;
        public bool Connected;
        public bool Physical;
    }

    public sealed class NetworkSnapshot
    {
        public bool Available { get; private set; }
        public string Key { get; private set; }
        public int NetworkCount { get; private set; }
        public string DisplayName { get { return !Available ? "Сеть не определена" : NetworkCount == 1 ? "Текущая сеть" : "Несколько подключённых сетей"; } }
        NetworkSnapshot(bool available, string key, int count) { Available = available; Key = key; NetworkCount = count; }
        public static NetworkSnapshot Unavailable() { return new NetworkSnapshot(false, "", 0); }

        // NLM network IDs distinguish Windows connection profiles, not ISPs.
        // No network names, addresses, MACs, adapter IDs or machine IDs are saved.
        public static NetworkSnapshot FromNetworkIds(IEnumerable<Guid> networkIds)
        {
            if (networkIds == null) throw new ArgumentNullException("networkIds");
            var ids = new SortedSet<string>(StringComparer.Ordinal);
            int observed = 0;
            foreach (Guid id in networkIds)
            {
                if (++observed > 64) return Unavailable();
                if (id == Guid.Empty) continue;
                ids.Add(id.ToString("N"));
            }
            if (ids.Count == 0) return Unavailable();
            byte[] bytes = Encoding.UTF8.GetBytes("LocalLink/network-v1/" + String.Join("/", ids));
            using (var hash = SHA256.Create())
                return new NetworkSnapshot(true, BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(), ids.Count);
        }
        public static NetworkSnapshot FromConnections(IEnumerable<NetworkConnectionObservation> observations)
        {
            if (observations == null) throw new ArgumentNullException("observations");
            var ids = new List<Guid>(); int count = 0;
            foreach (var item in observations)
            {
                if (++count > 256) return Unavailable();
                if (item != null && item.Connected && item.Physical) ids.Add(item.NetworkId);
            }
            return FromNetworkIds(ids);
        }
    }

    public static class NetworkIdentityObserver
    {
        // Read-only Network List Manager interfaces. Member order follows the
        // installed Windows SDK netlistmgr.h; no setter is ever invoked.
        [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        interface INetworkListManager
        {
            IEnumNetworks GetNetworks(int flags);
            INetwork GetNetwork(Guid id);
            IEnumNetworkConnections GetNetworkConnections();
        }
        [ComImport, Guid("DCB00003-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        interface IEnumNetworks
        {
            [return: MarshalAs(UnmanagedType.IUnknown)] object GetNewEnum();
            [PreserveSig] int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out INetwork network, out uint fetched);
        }
        [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        interface INetwork
        {
            [return: MarshalAs(UnmanagedType.BStr)] string GetName();
            void SetName([MarshalAs(UnmanagedType.BStr)] string value);
            [return: MarshalAs(UnmanagedType.BStr)] string GetDescription();
            void SetDescription([MarshalAs(UnmanagedType.BStr)] string value);
            Guid GetNetworkId();
        }
        [ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        interface IEnumNetworkConnections
        {
            [return: MarshalAs(UnmanagedType.IUnknown)] object GetNewEnum();
            [PreserveSig] int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out INetworkConnection connection, out uint fetched);
        }
        [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        interface INetworkConnection
        {
            INetwork GetNetwork();
            [return: MarshalAs(UnmanagedType.VariantBool)] bool IsConnectedToInternet();
            [return: MarshalAs(UnmanagedType.VariantBool)] bool IsConnected();
            int GetConnectivity();
            Guid GetConnectionId();
            Guid GetAdapterId();
        }

        public static NetworkSnapshot Capture()
        {
            object manager = null; IEnumNetworkConnections connections = null;
            try
            {
                var physical = PhysicalAdapters();
                if (physical.Count == 0) return NetworkSnapshot.Unavailable();
                Type type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"), true);
                manager = Activator.CreateInstance(type);
                connections = ((INetworkListManager)manager).GetNetworkConnections();
                var observations = new List<NetworkConnectionObservation>();
                for (int index = 0; index <= 256; index++)
                {
                    INetworkConnection connection = null; INetwork network = null; uint fetched;
                    int result = connections.Next(1, out connection, out fetched);
                    try
                    {
                        if (result < 0) Marshal.ThrowExceptionForHR(result);
                        if (fetched == 0 || connection == null) break;
                        if (index == 256) return NetworkSnapshot.Unavailable();
                        bool connected = connection.IsConnected(), isPhysical = physical.Contains(connection.GetAdapterId());
                        if (!connected || !isPhysical) continue;
                        network = connection.GetNetwork();
                        if (network != null) observations.Add(new NetworkConnectionObservation { NetworkId = network.GetNetworkId(), Connected = connected, Physical = isPhysical });
                    }
                    finally { Release(network); Release(connection); }
                }
                return NetworkSnapshot.FromConnections(observations);
            }
            catch (COMException) { return NetworkSnapshot.Unavailable(); }
            catch (InvalidCastException) { return NetworkSnapshot.Unavailable(); }
            catch (UnauthorizedAccessException) { return NetworkSnapshot.Unavailable(); }
            catch (TypeLoadException) { return NetworkSnapshot.Unavailable(); }
            catch (ManagementException) { return NetworkSnapshot.Unavailable(); }
            catch (System.Security.SecurityException) { return NetworkSnapshot.Unavailable(); }
            finally { Release(connections); Release(manager); }
        }
        static HashSet<Guid> PhysicalAdapters()
        {
            // Windows, rather than an adapter-name heuristic, identifies the
            // physical link. Adapter GUIDs live only in this observation and
            // are never written to a binding, UI label or diagnostic record.
            var result = new HashSet<Guid>();
            var options = new EnumerationOptions { ReturnImmediately = true, Timeout = TimeSpan.FromSeconds(3) };
            using (var search = new ManagementObjectSearcher(new ManagementScope(@"root\cimv2"),
                new ObjectQuery("SELECT GUID FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE AND NetEnabled = TRUE"), options))
            using (var rows = search.Get())
            {
                int count = 0;
                foreach (ManagementBaseObject row in rows)
                {
                    using (row)
                    {
                        if (++count > 256) return new HashSet<Guid>();
                        Guid id; if (Guid.TryParse(row["GUID"] as string, out id)) result.Add(id);
                    }
                }
            }
            return result;
        }
        static void Release(object value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    }

    public sealed class NetworkPresetStore
    {
        public const int MaximumBindings = 128;
        public const int MaximumBytes = 64 * 1024;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        readonly string path;
        public static string DefaultPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalLink", "network-presets-v1.json"); } }
        public NetworkPresetStore(string filePath) { path = Canonical(filePath); RejectReparse(path); }
        public int Count { get { return Read().Count; } }
        public string Find(string networkKey)
        {
            ValidateKey(networkKey); string id;
            return Read().TryGetValue(networkKey, out id) ? id : "";
        }
        public void Remember(string networkKey, string presetId)
        {
            ValidateKey(networkKey); ValidatePresetId(presetId);
            var values = Read();
            string previous;
            if (values.TryGetValue(networkKey, out previous) && previous == presetId) return;
            if (!values.ContainsKey(networkKey) && values.Count >= MaximumBindings) throw new IOException("Достигнут лимит сохранённых сетей.");
            values[networkKey] = presetId; Save(values);
        }
        public bool Forget(string networkKey)
        {
            ValidateKey(networkKey); var values = Read();
            if (!values.Remove(networkKey)) return false;
            Save(values); return true;
        }
        SortedDictionary<string, string> Read() { return ReadFile(path, true); }
        static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = MaximumBytes, RecursionLimit = 8 }; }
        static SortedDictionary<string, string> ReadFile(string file, bool missingAllowed)
        {
            RejectReparse(file);
            var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
            byte[] bytes;
            try
            {
                using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (input.Length < 1 || input.Length > MaximumBytes) throw new IOException("Недопустимый размер файла сетей.");
                    bytes = new byte[(int)input.Length]; int offset = 0, read;
                    while (offset < bytes.Length && (read = input.Read(bytes, offset, bytes.Length - offset)) > 0) offset += read;
                    if (offset != bytes.Length || input.ReadByte() != -1) throw new IOException("Файл сетей изменился во время чтения.");
                }
            }
            catch (FileNotFoundException) { if (missingAllowed) return values; throw; }
            catch (DirectoryNotFoundException) { if (missingAllowed) return values; throw; }
            string json = Utf8.GetString(bytes); if (json.Length > 0 && json[0] == '\uFEFF') json = json.Substring(1);
            var document = Serializer().DeserializeObject(json) as Dictionary<string, object>;
            object schema, rows;
            if (document == null || document.Count != 2 || !document.TryGetValue("Version", out schema) || !(schema is int) || (int)schema != 1 ||
                !document.TryGetValue("Bindings", out rows) || !(rows is object[])) throw new IOException("Неподдерживаемый файл сохранённых сетей.");
            object[] bindings = (object[])rows;
            if (bindings.Length > MaximumBindings) throw new IOException("Слишком много сохранённых сетей.");
            foreach (object row in bindings)
            {
                var entry = row as Dictionary<string, object>; object key, preset;
                if (entry == null || entry.Count != 2 || !entry.TryGetValue("NetworkKey", out key) || !(key is string) ||
                    !entry.TryGetValue("PresetId", out preset) || !(preset is string)) throw new IOException("Некорректная запись сети.");
                ValidateKey((string)key); ValidatePresetId((string)preset);
                if (values.ContainsKey((string)key)) throw new IOException("Повторяющаяся запись сети.");
                values.Add((string)key, (string)preset);
            }
            return values;
        }
        void Save(SortedDictionary<string, string> values)
        {
            var rows = new List<object>();
            foreach (var entry in values) rows.Add(new SortedDictionary<string, object>(StringComparer.Ordinal) { { "NetworkKey", entry.Key }, { "PresetId", entry.Value } });
            byte[] bytes = Utf8.GetBytes(Serializer().Serialize(new SortedDictionary<string, object>(StringComparer.Ordinal) { { "Version", 1 }, { "Bindings", rows } }));
            if (bytes.Length > MaximumBytes) throw new IOException("Файл сетей слишком большой.");
            RejectReparse(path); Directory.CreateDirectory(Path.GetDirectoryName(path)); RejectReparse(path);
            string suffix = DateTime.UtcNow.Ticks.ToString("D19", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
            string stage = path + ".staged-" + suffix, backup = path + ".backup-" + suffix;
            RejectReparse(stage); RejectReparse(backup);
            using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes, 0, bytes.Length); output.Flush(true); }
            ReadFile(stage, false); RejectReparse(path); RejectReparse(stage); RejectReparse(backup);
            if (File.Exists(backup) || Directory.Exists(backup)) throw new IOException("Резервный путь файла сетей уже существует.");
            if (File.Exists(path)) File.Replace(stage, path, backup); else File.Move(stage, path);
        }
        static void ValidateKey(string value)
        {
            if (value == null || value.Length != 64) throw new ArgumentException("Некорректный идентификатор сети.", "networkKey");
            foreach (char c in value) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) throw new ArgumentException("Некорректный идентификатор сети.", "networkKey");
        }
        static void ValidatePresetId(string value)
        {
            Guid id;
            if (value == null || !Guid.TryParseExact(value, "N", out id) || id.ToString("N") != value) throw new ArgumentException("Некорректный идентификатор пресета.", "presetId");
        }
        static string Canonical(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value) || value.StartsWith(@"\\", StringComparison.Ordinal) || (value.Length > 2 && value.IndexOf(':', 2) >= 0))
                throw new ArgumentException("Не указан локальный файл сохранённых сетей.", "filePath");
            string full = Path.GetFullPath(value);
            if (full.Length < 4 || full.Substring(2).IndexOf(':') >= 0) throw new ArgumentException("Недопустимый путь файла сетей.", "filePath");
            return full;
        }
        static void RejectReparse(string value)
        {
            for (string current = Path.GetFullPath(value); !String.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(current); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Файл сетей содержит перенаправление.");
            }
        }
    }
}
