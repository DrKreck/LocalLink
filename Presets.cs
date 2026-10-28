using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace LocalLink
{
    public sealed class PresetEntry
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public override string ToString() { return Name; }
    }

    public sealed class PresetStore
    {
        public const int MaximumNameLength = 80;
        public const int MaximumPresets = 256;
        const int MaximumBytes = AppSettings.MaximumSettingsBytes + 4096;
        readonly string folder;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        public static string DefaultFolder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalLink", "presets"); } }

        public PresetStore(string directory)
        {
            if (String.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory) || directory.StartsWith(@"\\", StringComparison.Ordinal) ||
                (directory.Length > 2 && directory.IndexOf(':', 2) >= 0))
                throw new ArgumentException("Не указана локальная папка пресетов.", "directory");
            folder = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (folder.Length < 3 || folder.Substring(2).IndexOf(':') >= 0)
                throw new ArgumentException("Недопустимый путь пресетов.", "directory");
            RejectReparse(folder);
        }

        sealed class Document
        {
            public int Version = 1;
            public string Name;
            public AppSettings Settings;
        }

        public List<PresetEntry> List()
        {
            RejectReparse(folder);
            var result = new List<PresetEntry>();
            if (!Directory.Exists(folder)) return result;
            int candidates = 0;
            foreach (string path in Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly))
            {
                string id = Path.GetFileNameWithoutExtension(path); Guid parsed;
                if (!Guid.TryParseExact(id, "N", out parsed)) continue;
                if (++candidates > MaximumPresets) throw new IOException("Слишком много пресетов в папке.");
                Document document;
                if (TryRead(path, out document)) result.Add(new PresetEntry { Id = parsed.ToString("N"), Name = document.Name });
            }
            result.Sort(delegate(PresetEntry left, PresetEntry right) { int order = StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name); return order != 0 ? order : StringComparer.Ordinal.Compare(left.Id, right.Id); });
            return result;
        }

        public string Save(string name, AppSettings settings, string existingId = null)
        {
            name = ValidateName(name);
            if (settings == null) throw new ArgumentNullException("settings");
            string id = String.IsNullOrEmpty(existingId) ? Guid.NewGuid().ToString("N") : ValidateId(existingId);
            RejectReparse(folder);
            if (String.IsNullOrEmpty(existingId) && List().Count >= MaximumPresets)
                throw new IOException("Достигнут лимит пресетов: " + MaximumPresets + ".");
            var serializer = Serializer();
            var copy = AppSettings.Normalize(serializer.Deserialize<AppSettings>(serializer.Serialize(settings)));
            copy.SelectedPresetId = id;
            byte[] bytes = Utf8.GetBytes(serializer.Serialize(new Document { Name = name, Settings = copy }));
            if (bytes.Length > MaximumBytes) throw new IOException("Пресет слишком большой. Предыдущая версия сохранена.");
            Directory.CreateDirectory(folder); RejectReparse(folder);
            string path = Path.Combine(folder, id + ".json"); RejectReparse(path);
            string suffix = DateTime.UtcNow.Ticks.ToString("D19", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
            string stage = path + ".staged-" + suffix, backup = path + ".backup-" + suffix;
            RejectReparse(stage); RejectReparse(backup);
            using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes, 0, bytes.Length); output.Flush(true); }
            Read(stage); RejectReparse(path); RejectReparse(stage); RejectReparse(backup);
            if (File.Exists(backup) || Directory.Exists(backup)) throw new IOException("Резервный путь пресета уже существует.");
            if (File.Exists(path)) File.Replace(stage, path, backup);
            else File.Move(stage, path);
            return id;
        }

        public AppSettings Load(string id)
        {
            id = ValidateId(id); RejectReparse(folder);
            Document document = Read(Path.Combine(folder, id + ".json"));
            document.Settings.SelectedPresetId = id;
            return document.Settings;
        }

        static JavaScriptSerializer Serializer()
        { return new JavaScriptSerializer { MaxJsonLength = MaximumBytes, RecursionLimit = 20 }; }
        static string ValidateName(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length < 1 || name.Length > MaximumNameLength) throw new ArgumentException("Имя пресета должно содержать от 1 до " + MaximumNameLength + " символов.", "name");
            foreach (char character in name) if (Char.IsControl(character)) throw new ArgumentException("Имя пресета не должно содержать управляющие символы.", "name");
            return name;
        }
        static string ValidateId(string id)
        {
            Guid parsed;
            if (id == null || !Guid.TryParseExact(id, "N", out parsed)) throw new ArgumentException("Некорректный идентификатор пресета.", "id");
            return parsed.ToString("N");
        }
        static bool TryRead(string path, out Document document)
        {
            document = null;
            try { document = Read(path); return true; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.Security.SecurityException) { }
            return false;
        }
        static Document Read(string path)
        {
            RejectReparse(path);
            byte[] bytes;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length < 1 || input.Length > MaximumBytes) throw new IOException("Недопустимый размер пресета.");
                bytes = new byte[(int)input.Length]; int offset = 0, read;
                while (offset < bytes.Length && (read = input.Read(bytes, offset, bytes.Length - offset)) > 0) offset += read;
                if (offset != bytes.Length || input.ReadByte() != -1) throw new IOException("Пресет изменился при чтении.");
            }
            string json = Utf8.GetString(bytes); if (json.Length > 0 && json[0] == '\uFEFF') json = json.Substring(1);
            Document document = Serializer().Deserialize<Document>(json);
            if (document == null || document.Version != 1 || document.Settings == null) throw new IOException("Некорректный файл пресета.");
            document.Name = ValidateName(document.Name); document.Settings = AppSettings.Normalize(document.Settings);
            return document;
        }
        static void RejectReparse(string path)
        {
            for (string current = Path.GetFullPath(path); !String.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(current); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Папка пресетов содержит перенаправление.");
            }
        }
    }
}
