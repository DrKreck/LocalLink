using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace LocalLink
{
    public sealed class PortablePreset
    {
        public string Name {get;internal set;}
        public AppSettings Settings {get;internal set;}
    }

    public static class PresetExchange
    {
        public const int MaximumBytes=1024*1024+4096;
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        static readonly string[] SettingNames={"Services","Mode","Strategy","DiscordStrategy","Quic","DiscordVoice","TelegramNative",
            "UsePublicLists","UseCustomDomains","DisabledDomains","CustomDomains","Endpoint","Port","Appearance","AutomaticRecovery","AutomaticSetup"};

        public static PortablePreset Read(string file)
        {
            string path=LocalPath(file);RejectReparse(path);
            byte[] bytes;
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                if(stream.Length<1||stream.Length>MaximumBytes)throw new IOException("Недопустимый размер пресета.");
                bytes=new byte[(int)stream.Length];int offset=0,read;
                while(offset<bytes.Length&&(read=stream.Read(bytes,offset,bytes.Length-offset))>0)offset+=read;
                if(offset!=bytes.Length||stream.ReadByte()!=-1)throw new IOException("Пресет изменился при чтении.");
            }
            return Decode(bytes);
        }

        public static void Write(string file,string name,AppSettings settings,bool overwrite=false)
        {
            string path=LocalPath(file);RejectReparse(path);
            if(settings==null)throw new ArgumentNullException("settings");
            byte[] bytes=Encode(name,settings);
            if(Directory.Exists(path))throw new IOException("Вместо файла пресета находится папка.");
            if(File.Exists(path)&&!overwrite)throw new IOException("Файл уже существует. Выберите другое имя.");
            string folder=Path.GetDirectoryName(path);Directory.CreateDirectory(folder);RejectReparse(folder);
            if(File.Exists(path)&&SameBytes(path,bytes))return;
            string suffix=DateTime.UtcNow.Ticks.ToString("D19",CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N");
            string stage=path+".staged-"+suffix,backup=path+".backup-"+suffix;
            RejectReparse(stage);RejectReparse(backup);
            using(var output=new FileStream(stage,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {output.Write(bytes,0,bytes.Length);output.Flush(true);}
            Decode(bytes);RejectReparse(path);RejectReparse(stage);RejectReparse(backup);
            if(File.Exists(path))
            {
                if(!overwrite)throw new IOException("Файл появился во время сохранения. Экспорт отменён.");
                File.Replace(stage,path,backup);
            }
            else File.Move(stage,path);
        }

        internal static AppSettings DetachedConnectionSettings(AppSettings settings)
        {return Decode(Encode("Рабочая конфигурация",settings)).Settings;}

        static byte[] Encode(string name,AppSettings settings)
        {
            if(settings==null)throw new ArgumentNullException("settings");
            var fields=new SortedDictionary<string,object>(StringComparer.Ordinal)
            {
                {"Services",new List<string>(settings.Services??new List<string>())},{"Mode",settings.Mode},
                {"Strategy",settings.Strategy},{"DiscordStrategy",settings.DiscordStrategy??""},{"Quic",settings.Quic},
                {"DiscordVoice",settings.DiscordVoice},{"TelegramNative",settings.TelegramNative},{"UsePublicLists",settings.UsePublicLists},
                {"UseCustomDomains",settings.UseCustomDomains},{"DisabledDomains",new List<string>(settings.DisabledDomains??new List<string>())},
                {"CustomDomains",settings.CustomDomains??""},{"Endpoint",settings.Endpoint??""},{"Port",settings.Port},
                {"Appearance",settings.Appearance},{"AutomaticRecovery",settings.AutomaticRecovery},{"AutomaticSetup",settings.AutomaticSetup}
            };
            var document=new SortedDictionary<string,object>(StringComparer.Ordinal)
            {{"Schema","locallink-preset-v1"},{"Name",ValidateName(name)},{"Settings",fields}};
            byte[] bytes=Utf8.GetBytes(Serializer().Serialize(document));
            if(bytes.Length>MaximumBytes)throw new IOException("Пресет слишком большой.");
            // Validate before creating a destination or replacing an existing file.
            Decode(bytes);return bytes;
        }

        internal static PortablePreset Decode(byte[] bytes)
        {
            if(bytes==null||bytes.Length<1||bytes.Length>MaximumBytes)throw new IOException("Недопустимый размер пресета.");
            string text=Utf8.GetString(bytes);if(text.Length>0&&text[0]=='\uFEFF')text=text.Substring(1);
            Dictionary<string,object> value;
            try{value=Serializer().DeserializeObject(text) as Dictionary<string,object>;}
            catch(ArgumentException){throw new IOException("Некорректный JSON пресета.");}
            catch(InvalidOperationException){throw new IOException("Некорректный JSON пресета.");}
            RequireKeys(value,new[]{"Schema","Name","Settings"});
            if(StringValue(value,"Schema",40)!="locallink-preset-v1")throw new IOException("Неподдерживаемый файл пресета.");
            string name=ValidateName(StringValue(value,"Name",PresetStore.MaximumNameLength));
            var fields=value["Settings"] as Dictionary<string,object>;RequireKeys(fields,SettingNames);
            var services=Strings(fields,"Services",ServiceCatalog.All().Count());
            var allowed=new HashSet<string>(ServiceCatalog.All().Select(x=>x.Id),StringComparer.Ordinal);
            if(services.Any(x=>!allowed.Contains(x))||services.Distinct(StringComparer.Ordinal).Count()!=services.Count)
                throw new IOException("Неизвестная или повторяющаяся категория пресета.");
            string method=StringValue(fields,"Strategy",32),discord=StringValue(fields,"DiscordStrategy",32);
            if(!Profiles.IsTlsStrategy(method)||discord.Length>0&&!Profiles.IsTlsStrategy(discord))throw new IOException("Неизвестный метод подключения.");
            var disabled=Strings(fields,"DisabledDomains",DomainSelection.MaximumRules);
            if(disabled.Any(x=>PublicLists.NormalizeDomain(x)==null))throw new IOException("Некорректное исключение домена.");
            string domains=StringValue(fields,"CustomDomains",8192);
            if(domains.Length>0)try{Profiles.ValidateDomains(domains);}catch(ArgumentException){throw new IOException("Некорректные домены пресета.");}
            string endpoint=StringValue(fields,"Endpoint",45);
            int port=Number(fields,"Port",1,65535),mode=Number(fields,"Mode",0,2);
            if(endpoint.Length>0)try{Profiles.Build(new ProfileSpec{Mode=HelperMode.VpnTls,Endpoint=endpoint,Port=port,Strategy="split"});}
                catch(ArgumentException){throw new IOException("Некорректный IP-адрес пресета.");}
            string appearance=StringValue(fields,"Appearance",8);
            if(appearance!="system"&&appearance!="light"&&appearance!="dark")throw new IOException("Неизвестная тема пресета.");
            var settings=new AppSettings{Services=services,Mode=mode,Strategy=method,DiscordStrategy=discord,
                Quic=Boolean(fields,"Quic"),DiscordVoice=Boolean(fields,"DiscordVoice"),TelegramNative=Boolean(fields,"TelegramNative"),
                UsePublicLists=Boolean(fields,"UsePublicLists"),UseCustomDomains=Boolean(fields,"UseCustomDomains"),DisabledDomains=disabled,
                CustomDomains=domains,Endpoint=endpoint,Port=port,Appearance=appearance,AutomaticRecovery=Boolean(fields,"AutomaticRecovery"),
                AutomaticSetup=Boolean(fields,"AutomaticSetup"),ConnectOnOpen=false,SelectedPresetId=""};
            return new PortablePreset{Name=name,Settings=AppSettings.Normalize(settings)};
        }

        static JavaScriptSerializer Serializer(){return new JavaScriptSerializer{MaxJsonLength=MaximumBytes,RecursionLimit=12};}
        static void RequireKeys(Dictionary<string,object> value,IEnumerable<string> names)
        {var expected=names.ToArray();if(value==null||value.Count!=expected.Length||expected.Any(x=>!value.ContainsKey(x)))throw new IOException("Неизвестные или пропущенные поля пресета.");}
        static string StringValue(Dictionary<string,object> value,string name,int maximum)
        {var text=value[name] as string;if(text==null||text.Length>maximum||text.IndexOf('\0')>=0)throw new IOException("Некорректное текстовое поле пресета.");return text;}
        static bool Boolean(Dictionary<string,object> value,string name)
        {if(!(value[name] is bool))throw new IOException("Некорректный переключатель пресета.");return (bool)value[name];}
        static int Number(Dictionary<string,object> value,string name,int minimum,int maximum)
        {if(!(value[name] is int)||(int)value[name]<minimum||(int)value[name]>maximum)throw new IOException("Некорректное число пресета.");return (int)value[name];}
        static List<string> Strings(Dictionary<string,object> value,string name,int maximum)
        {
            var array=value[name] as object[];if(array==null||array.Length>maximum)throw new IOException("Некорректный список пресета.");
            var result=new List<string>();foreach(object item in array){var text=item as string;if(text==null||text.Length>253||text.Any(Char.IsControl))throw new IOException("Некорректное значение списка пресета.");result.Add(text);}return result;
        }
        static string ValidateName(string name)
        {name=(name??"").Trim();if(name.Length<1||name.Length>PresetStore.MaximumNameLength||name.Any(Char.IsControl))throw new IOException("Некорректное имя пресета.");return name;}
        internal static string LocalPath(string file)
        {
            if(String.IsNullOrWhiteSpace(file)||!Regex.IsMatch(file,@"\A[A-Za-z]:[\\/]")||file.IndexOf(':',2)>=0)throw new ArgumentException("Нужен локальный файл пресета.","file");
            string path=Path.GetFullPath(file);if(!String.Equals(Path.GetExtension(path),".json",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Нужен файл JSON.","file");
            RejectReparse(path);return path;
        }
        static bool SameBytes(string path,byte[] bytes)
        {
            RejectReparse(path);
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                if(stream.Length!=bytes.Length)return false;
                for(int i=0;i<bytes.Length;i++)if(stream.ReadByte()!=bytes[i])return false;
                return stream.ReadByte()==-1;
            }
        }
        internal static void RejectReparse(string path)
        {
            for(string current=Path.GetFullPath(path);!String.IsNullOrEmpty(current);current=Path.GetDirectoryName(current))
            {
                FileAttributes flags;try{flags=File.GetAttributes(current);}catch(FileNotFoundException){continue;}catch(DirectoryNotFoundException){continue;}
                if((flags&FileAttributes.ReparsePoint)!=0)throw new IOException("Перенаправления в пути пресета не поддерживаются.");
            }
        }
    }

    public sealed class WorkingSettingsStore
    {
        readonly string path;
        public static string DefaultFolder{get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LocalLink","working");}}
        public WorkingSettingsStore(string folder)
        {
            if(String.IsNullOrWhiteSpace(folder))throw new ArgumentException("Нужна папка рабочих настроек.","folder");
            path=PresetExchange.LocalPath(Path.Combine(folder,"working.json"));
        }
        public void Remember(AppSettings settings){PresetExchange.Write(path,"Рабочая конфигурация",settings,true);}
        public AppSettings Load()
        {
            try{return PresetExchange.Read(path).Settings;}
            catch(IOException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}catch(System.Security.SecurityException){}
            string folder=Path.GetDirectoryName(path);PresetExchange.RejectReparse(folder);
            if(!Directory.Exists(folder))return null;
            foreach(string backup in Directory.EnumerateFiles(folder,"working.json.backup-*",SearchOption.TopDirectoryOnly).OrderByDescending(x=>Path.GetFileName(x),StringComparer.Ordinal).Take(16))
            {
                try
                {
                    // Backup names are never accepted as arbitrary import paths.
                    // Decode via a guarded read using the normal JSON extension.
                    var value=ReadBackup(backup);if(value!=null)return value;
                }
                catch(IOException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}catch(System.Security.SecurityException){}
            }
            return null;
        }
        static AppSettings ReadBackup(string file)
        {
            PresetExchange.RejectReparse(file);
            // PresetExchange.Read intentionally requires .json for user-chosen files;
            // retain one controlled internal read path for its own atomic backups.
            byte[] bytes;
            using(var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                if(stream.Length<1||stream.Length>PresetExchange.MaximumBytes)throw new IOException("Некорректный размер резервного пресета.");
                bytes=new byte[(int)stream.Length];int position=0,count;
                while(position<bytes.Length&&(count=stream.Read(bytes,position,bytes.Length-position))>0)position+=count;
                if(position!=bytes.Length||stream.ReadByte()!=-1)throw new IOException("Резервный пресет изменился при чтении.");
            }
            return PresetExchange.Decode(bytes).Settings;
        }
    }
}
