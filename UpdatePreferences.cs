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
    public enum UpdatePromptChoice { Later, Update, IgnoreVersion }

    // Deliberately separate from connection settings and named network presets.
    // Loading a preset must never re-enable automatic checks or clear a refusal.
    public sealed class UpdatePreferences
    {
        public const int AutomaticCheckIntervalHours=24,LaterHours=24;
        public bool AutoCheck=true;
        public string IgnoredVersion="";
        public string LastAutomaticCheckUtc="";
        public string SnoozedUntilUtc="";
        [ScriptIgnore] public bool RecoveredFromBackup { get; internal set; }
        [ScriptIgnore] public bool ReadProblem { get; internal set; }

        public bool ShouldCheck(DateTime utcNow)
        {
            RequireUtc(utcNow);
            if(!AutoCheck||!Elapsed(SnoozedUntilUtc,utcNow))return false;
            DateTime previous;
            return String.IsNullOrEmpty(LastAutomaticCheckUtc)||
                TryUtc(LastAutomaticCheckUtc,out previous)&&utcNow>=AddHours(previous,AutomaticCheckIntervalHours);
        }
        public bool ShouldPrompt(string version,DateTime utcNow,bool manual=false)
        {
            RequireVersion(version);RequireUtc(utcNow);
            return manual||(AutoCheck&&IgnoredVersion!=version&&Elapsed(SnoozedUntilUtc,utcNow));
        }
        public void MarkAutomaticCheck(DateTime utcNow)
        {RequireUtc(utcNow);LastAutomaticCheckUtc=utcNow.ToString("o",CultureInfo.InvariantCulture);}
        public void ApplyChoice(string version,UpdatePromptChoice choice,DateTime utcNow)
        {
            RequireVersion(version);RequireUtc(utcNow);
            switch(choice)
            {
                case UpdatePromptChoice.Update:
                    SnoozedUntilUtc="";if(IgnoredVersion==version)IgnoredVersion="";break;
                case UpdatePromptChoice.Later:
                    SnoozedUntilUtc=AddHours(utcNow,LaterHours).ToString("o",CultureInfo.InvariantCulture);break;
                case UpdatePromptChoice.IgnoreVersion:
                    IgnoredVersion=version;SnoozedUntilUtc="";break;
                default:throw new ArgumentOutOfRangeException("choice");
            }
        }
        internal static void RequireVersion(string version)
        {
            if(version==null||!Regex.IsMatch(version,@"\A(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4})\z",RegexOptions.CultureInvariant))
                throw new ArgumentException("Нужен номер версии X.Y.Z.","version");
        }
        internal static void RequireUtc(DateTime value)
        {if(value.Kind!=DateTimeKind.Utc)throw new ArgumentException("Время обновления должно быть UTC.","utcNow");}
        internal static bool TryUtc(string text,out DateTime value)
        {
            value=DateTime.MinValue;
            return text!=null&&text.Length<=35&&DateTime.TryParseExact(text,"o",CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out value)&&value.Kind==DateTimeKind.Utc;
        }
        static bool Elapsed(string deadline,DateTime now)
        {DateTime until;return String.IsNullOrEmpty(deadline)||TryUtc(deadline,out until)&&now>=until;}
        static DateTime AddHours(DateTime value,int hours)
        {return value>DateTime.MaxValue.AddHours(-hours)?DateTime.SpecifyKind(DateTime.MaxValue,DateTimeKind.Utc):value.AddHours(hours);}
        internal UpdatePreferences CopyValidated()
        {
            string ignored=IgnoredVersion??"",last=LastAutomaticCheckUtc??"",snooze=SnoozedUntilUtc??"";
            if(ignored.Length>0)RequireVersion(ignored);
            DateTime parsed;
            if(last.Length>0&&!TryUtc(last,out parsed)||snooze.Length>0&&!TryUtc(snooze,out parsed))
                throw new ArgumentException("Некорректное время напоминания.");
            return new UpdatePreferences{AutoCheck=AutoCheck,IgnoredVersion=ignored,LastAutomaticCheckUtc=last,SnoozedUntilUtc=snooze};
        }
    }

    public sealed class UpdatePreferenceStore
    {
        public const int MaximumBytes=8192,MaximumRecoveryCandidates=16;
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        readonly string path;
        public static string DefaultPath {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LocalLink","update-preferences.json");}}
        public UpdatePreferenceStore():this(DefaultPath){}
        public UpdatePreferenceStore(string file)
        {
            if(String.IsNullOrWhiteSpace(file)||!Regex.IsMatch(file,@"\A[A-Za-z]:[\\/]")||file.IndexOf(':',2)>=0)
                throw new ArgumentException("Нужен локальный файл настроек обновления.","file");
            path=Path.GetFullPath(file);
            if(!String.Equals(Path.GetExtension(path),".json",StringComparison.OrdinalIgnoreCase)||String.IsNullOrEmpty(Path.GetFileNameWithoutExtension(path)))
                throw new ArgumentException("Нужен файл JSON.","file");
            RejectReparse(path);
        }
        public UpdatePreferences Load()
        {
            try
            {
                RejectReparse(path);
                UpdatePreferences value;
                if(TryRead(path,out value))return value;
                string folder=Path.GetDirectoryName(path);
                if(!Directory.Exists(folder))return new UpdatePreferences();
                var backups=Directory.EnumerateFiles(folder,Path.GetFileName(path)+".backup-*",SearchOption.TopDirectoryOnly)
                    .OrderByDescending(x=>Path.GetFileName(x),StringComparer.Ordinal).Take(MaximumRecoveryCandidates);
                foreach(string backup in backups)
                    if(TryRead(backup,out value)){value.RecoveredFromBackup=true;return value;}
                // If a formerly saved refusal can't be recovered, fail quiet.
                // A manual check remains available; damaged files are preserved.
                if(File.Exists(path)||Directory.Exists(path))return new UpdatePreferences{AutoCheck=false,ReadProblem=true};
                return new UpdatePreferences();
            }
            catch(IOException){return QuietFailure();}
            catch(UnauthorizedAccessException){return QuietFailure();}
            catch(System.Security.SecurityException){return QuietFailure();}
        }
        static UpdatePreferences QuietFailure(){return new UpdatePreferences{AutoCheck=false,ReadProblem=true};}
        public void Save(UpdatePreferences preferences)
        {
            if(preferences==null)throw new ArgumentNullException("preferences");
            var value=preferences.CopyValidated();
            var document=new Dictionary<string,object>{{"Schema",1},{"AutoCheck",value.AutoCheck},{"IgnoredVersion",value.IgnoredVersion},
                {"LastAutomaticCheckUtc",value.LastAutomaticCheckUtc},{"SnoozedUntilUtc",value.SnoozedUntilUtc}};
            byte[] bytes=Utf8.GetBytes(new JavaScriptSerializer().Serialize(document));
            if(bytes.Length>MaximumBytes)throw new IOException("Настройки обновления слишком велики.");
            RejectReparse(path);string folder=Path.GetDirectoryName(path);
            Directory.CreateDirectory(folder);RejectReparse(folder);
            // No-op saves do not generate an unbounded stream of backups.
            if(File.Exists(path))
            {
                try{if(ReadBytes(path).SequenceEqual(bytes))return;}catch(IOException){}
            }
            string suffix=DateTime.UtcNow.Ticks.ToString("D19",CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N");
            string stage=path+".staged-"+suffix,backup=path+".backup-"+suffix;
            RejectReparse(stage);RejectReparse(backup);
            using(var stream=new FileStream(stage,FileMode.CreateNew,FileAccess.Write,FileShare.None)){stream.Write(bytes,0,bytes.Length);stream.Flush(true);}
            Read(stage);RejectReparse(path);RejectReparse(stage);RejectReparse(backup);
            if(File.Exists(path))File.Replace(stage,path,backup);else File.Move(stage,path);
        }
        static bool TryRead(string file,out UpdatePreferences preferences)
        {
            preferences=null;
            try{preferences=Read(file);return true;}
            catch(IOException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}
            catch(InvalidOperationException){}catch(System.Security.SecurityException){}
            return false;
        }
        static UpdatePreferences Read(string file)
        {
            string json=Utf8.GetString(ReadBytes(file));if(json.Length>0&&json[0]=='\uFEFF')json=json.Substring(1);
            var serializer=new JavaScriptSerializer{MaxJsonLength=MaximumBytes,RecursionLimit=8};
            var data=serializer.DeserializeObject(json) as Dictionary<string,object>;
            string[] expected={"Schema","AutoCheck","IgnoredVersion","LastAutomaticCheckUtc","SnoozedUntilUtc"};
            if(data==null||data.Count!=expected.Length||expected.Any(x=>!data.ContainsKey(x))||!(data["Schema"] is int)||(int)data["Schema"]!=1||!(data["AutoCheck"] is bool)||
                !(data["IgnoredVersion"] is string)||!(data["LastAutomaticCheckUtc"] is string)||!(data["SnoozedUntilUtc"] is string))throw new IOException("Некорректные настройки обновления.");
            return new UpdatePreferences{AutoCheck=(bool)data["AutoCheck"],IgnoredVersion=(string)data["IgnoredVersion"],LastAutomaticCheckUtc=(string)data["LastAutomaticCheckUtc"],SnoozedUntilUtc=(string)data["SnoozedUntilUtc"]}.CopyValidated();
        }
        static byte[] ReadBytes(string file)
        {
            RejectReparse(file);
            using(var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                if(stream.Length<1||stream.Length>MaximumBytes)throw new IOException("Некорректный размер настроек обновления.");
                byte[] bytes=new byte[(int)stream.Length];int offset=0,read;
                while(offset<bytes.Length&&(read=stream.Read(bytes,offset,bytes.Length-offset))>0)offset+=read;
                if(offset!=bytes.Length||stream.ReadByte()!=-1)throw new IOException("Файл настроек обновления изменился при чтении.");
                return bytes;
            }
        }
        static void RejectReparse(string file)
        {
            for(string current=Path.GetFullPath(file);!String.IsNullOrEmpty(current);current=Path.GetDirectoryName(current))
            {
                FileAttributes attributes;
                try{attributes=File.GetAttributes(current);}catch(FileNotFoundException){continue;}catch(DirectoryNotFoundException){continue;}
                if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Перенаправление папки настроек обновления не поддерживается.");
            }
        }
    }
}
