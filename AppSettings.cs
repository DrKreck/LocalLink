using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace LocalLink
{
    // Only this application's preferences. Never loads VPN profiles or browser data.
    public sealed class AppSettings
    {
        internal const int MaximumSettingsBytes = 1024 * 1024;
        const int MaximumRecoveryCandidates = 32;
        static readonly UTF8Encoding SettingsEncoding = new UTF8Encoding(false, true);
        public List<string> Services = new List<string> { "discord", "youtube", "telegram", "steam", "arma", "tarkov", "dcs" };
        public int Mode;
        public string Strategy = "split";
        public string DiscordStrategy = "";
        public bool Quic;
        public bool DiscordVoice = true;
        public bool TelegramNative = true;
        public bool UsePublicLists;
        public bool UseCustomDomains = true;
        public List<string> DisabledDomains = new List<string>();
        public string CustomDomains = "";
        public string Endpoint = "";
        public int Port = 443;
        public string Appearance = "system";
        public string SelectedPresetId = "";
        [ScriptIgnore]
        public bool RecoveredFromBackup { get; private set; }

        public static AppSettings Normalize(AppSettings value)
        {
            if(value==null)return new AppSettings();
            var valid=new HashSet<string>(StringComparer.Ordinal);
            foreach(var entry in ServiceCatalog.All())valid.Add(entry.Id);
            var selected=new List<string>();
            foreach(var id in value.Services??new List<string>())
                if(id!=null&&valid.Contains(id)&&!selected.Contains(id))selected.Add(id);
            value.Services=selected;
            value.DisabledDomains=DomainSelection.NormalizeDisabled(value.DisabledDomains);
            Guid presetId;
            value.SelectedPresetId=Guid.TryParse(value.SelectedPresetId,out presetId)?presetId.ToString("N"):"";
            if(value.Mode<0||value.Mode>2)value.Mode=0;
            if(!Profiles.IsTlsStrategy(value.Strategy))value.Strategy="split";
            if(!Profiles.IsTlsStrategy(value.DiscordStrategy))value.DiscordStrategy="";
            if(value.Appearance!="system"&&value.Appearance!="light"&&value.Appearance!="dark")value.Appearance="system";
            if(value.Port<1||value.Port>65535)value.Port=443;
            if(value.Endpoint==null||value.Endpoint.Length>45)value.Endpoint="";
            if(value.Endpoint.Length>0)
            {
                try{Profiles.Build(new ProfileSpec{Mode=HelperMode.VpnTls,Endpoint=value.Endpoint,Port=value.Port,Strategy="split"});}
                catch(ArgumentException){value.Endpoint="";}
            }
            if(value.CustomDomains==null)value.CustomDomains="";
            if(value.CustomDomains.Length>0)
            {
                try{value.CustomDomains=String.Join(Environment.NewLine,Profiles.ValidateDomains(value.CustomDomains));}
                catch(ArgumentException){value.CustomDomains="";}
            }
            return value;
        }
        static string SettingsPath()
        {return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LocalLink","settings-v3.json");}
        public static AppSettings Load()
        {
            string path=SettingsPath();
            return LoadFromPath(path,Path.Combine(Path.GetDirectoryName(path),"settings-v2.json"));
        }
        public void Save()
        {
            SaveToPath(SettingsPath(),null);
        }
        // Paths are supplied only by the app or an isolated workspace fixture.
        // Loading a backup never rewrites the damaged primary or the v2 source.
        internal static AppSettings LoadFromPath(string path,string legacyPath)
        {
            try
            {
                path=CanonicalPath(path);
                RejectReparse(path);
                AppSettings value;
                if(TryReadSettings(path,out value))return value;
                foreach(string backup in RecoveryCandidates(path))
                    if(TryReadSettings(backup,out value)){value.RecoveredFromBackup=true;return value;}
                // Read v2 for migration only. Save never overwrites or removes it.
                if(!String.IsNullOrEmpty(legacyPath)&&TryReadSettings(CanonicalPath(legacyPath),out value))return value;
            }
            catch(IOException){}
            catch(UnauthorizedAccessException){}
            catch(ArgumentException){}
            catch(InvalidOperationException){}
            catch(System.Security.SecurityException){}
            return new AppSettings();
        }
        // The optional hook is a test-only seam for failures immediately before
        // commit; production Save never supplies it. There is no cleanup/delete.
        internal void SaveToPath(string path,Action<string> beforeCommit)
        {
            path=CanonicalPath(path);
            RejectReparse(path);
            string folder=Path.GetDirectoryName(path);
            Directory.CreateDirectory(folder);
            RejectReparse(path);
            // Explicit user choices only. No auto-start or persisted permission.
            string json=SerializeSettings(Normalize(this));
            byte[] bytes=SettingsEncoding.GetBytes(json);
            if(bytes.Length>MaximumSettingsBytes)
                throw new IOException("Настройки превышают допустимый размер. Предыдущий файл сохранён.");
            // Hiding to tray or closing an unchanged window is not a new
            // preference revision. Compare EXACT serialized bytes, never just
            // equivalent normalized JSON; a damaged/old-format file must still
            // be replaced safely and retained as a backup on explicit save.
            if(File.Exists(path)&&PrimaryMatches(path,bytes))return;
            string suffix=DateTime.UtcNow.Ticks.ToString("D19",CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N");
            string stage=path+".staged-"+suffix;
            string backup=path+".backup-"+suffix;
            RejectReparse(stage);
            using(var stream=new FileStream(stage,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                stream.Write(bytes,0,bytes.Length);
                stream.Flush(true);
            }
            if(beforeCommit!=null)beforeCommit(stage);
            // Check again after writing, including the byte limit. If anything
            // fails, the existing primary and the staged file are left intact.
            ReadSettings(stage);
            RejectReparse(path);
            RejectReparse(stage);
            RejectReparse(backup);
            if(File.Exists(backup)||Directory.Exists(backup))
                throw new IOException("Резервный путь уже существует. Сохранение отменено.");
            if(File.Exists(path))
                // Same-directory atomic replace keeps the preceding file under
                // a UNIQUE name; older backups are never overwritten/removed.
                File.Replace(stage,path,backup);
            else
                File.Move(stage,path);
        }
        static JavaScriptSerializer Serializer()
        {
            return new JavaScriptSerializer{MaxJsonLength=MaximumSettingsBytes,RecursionLimit=16};
        }
        static string SerializeSettings(AppSettings value)
        {
            // JavaScriptSerializer's reflection order is not stable after the
            // type has been deserialized. Explicit sorted schema fields make
            // identical choices byte-identical, without persisting status or
            // relying on semantic equality of an existing file.
            var fields=new SortedDictionary<string,object>(StringComparer.Ordinal)
            {
                {"Services",value.Services},{"Mode",value.Mode},{"Strategy",value.Strategy},
                {"DiscordStrategy",value.DiscordStrategy},
                {"Quic",value.Quic},{"DiscordVoice",value.DiscordVoice},{"TelegramNative",value.TelegramNative},
                {"UsePublicLists",value.UsePublicLists},{"UseCustomDomains",value.UseCustomDomains},
                {"DisabledDomains",value.DisabledDomains},{"CustomDomains",value.CustomDomains},
                {"Endpoint",value.Endpoint},{"Port",value.Port},{"Appearance",value.Appearance},
                {"SelectedPresetId",value.SelectedPresetId}
            };
            return Serializer().Serialize(fields);
        }
        static AppSettings ReadSettings(string path)
        {
            RejectReparse(path);
            byte[] bytes;
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
                bytes=ReadBoundedBytes(stream);
            string json=SettingsEncoding.GetString(bytes);
            if(json.Length>0&&json[0]=='\uFEFF')json=json.Substring(1);
            AppSettings value=Serializer().Deserialize<AppSettings>(json);
            if(value==null)throw new IOException("Файл настроек не содержит объект настроек.");
            return Normalize(value);
        }
        static bool PrimaryMatches(string path,byte[] expected)
        {
            RejectReparse(path);
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                // A differently sized file is not equal. Avoid allocating or
                // reading any oversized primary during this optional check.
                if(stream.Length!=expected.Length)return false;
                byte[] actual=ReadBoundedBytes(stream);
                for(int i=0;i<actual.Length;i++)if(actual[i]!=expected[i])return false;
                return true;
            }
        }
        static byte[] ReadBoundedBytes(FileStream stream)
        {
            if(stream.Length<1||stream.Length>MaximumSettingsBytes)
                throw new IOException("Недопустимый размер файла настроек.");
            byte[] bytes=new byte[(int)stream.Length];
            int offset=0;
            while(offset<bytes.Length)
            {
                int count=stream.Read(bytes,offset,bytes.Length-offset);
                if(count==0)throw new IOException("Файл настроек изменился во время чтения.");
                offset+=count;
            }
            if(stream.ReadByte()!=-1)throw new IOException("Недопустимый размер файла настроек.");
            return bytes;
        }
        static bool TryReadSettings(string path,out AppSettings value)
        {
            value=null;
            try{value=ReadSettings(path);return true;}
            catch(IOException){}
            catch(UnauthorizedAccessException){}
            catch(ArgumentException){}
            catch(InvalidOperationException){}
            catch(FormatException){}
            catch(System.Security.SecurityException){}
            return false;
        }
        static List<string> RecoveryCandidates(string path)
        {
            var result=new List<string>();
            string folder=Path.GetDirectoryName(path);
            if(!Directory.Exists(folder))return result;
            foreach(string candidate in Directory.EnumerateFiles(folder,Path.GetFileName(path)+".backup-*",SearchOption.TopDirectoryOnly))
            {
                // The timestamp prefix makes lexical order chronological. Keep
                // memory and recovery reads bounded even after many saves.
                result.Add(candidate);
                result.Sort(delegate(string left,string right){return StringComparer.Ordinal.Compare(right,left);});
                if(result.Count>MaximumRecoveryCandidates)result.RemoveAt(result.Count-1);
            }
            return result;
        }
        static string CanonicalPath(string path)
        {
            if(String.IsNullOrWhiteSpace(path))throw new ArgumentException("Путь настроек не указан.","path");
            string full=Path.GetFullPath(path);
            string name=Path.GetFileName(full);
            if(String.IsNullOrEmpty(name)||name.IndexOf(':')>=0)
                throw new ArgumentException("Недопустимый путь настроек.","path");
            return full;
        }
        static void RejectReparse(string path)
        {
            for(string current=Path.GetFullPath(path);!String.IsNullOrEmpty(current);current=Path.GetDirectoryName(current))
            {
                FileAttributes attributes;
                try{attributes=File.GetAttributes(current);}
                catch(FileNotFoundException){continue;}
                catch(DirectoryNotFoundException){continue;}
                if((attributes&FileAttributes.ReparsePoint)!=0)
                    throw new IOException("Папка настроек содержит перенаправление. Сохранение отменено.");
            }
        }
    }
}
