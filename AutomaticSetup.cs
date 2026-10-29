using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace LocalLink
{
    public sealed class AutomaticMethodChoice
    {
        public string Strategy { get; internal set; }
        public string DiscordStrategy { get; internal set; }
        public string Evidence { get; internal set; }
        public DateTime VerifiedUtc { get; internal set; }
    }

    // Pure evidence decisions. A running process, a website alone or a TCP
    // connection never becomes a claim that Discord voice/MTProto/games work.
    public static class AutomaticSetupPolicy
    {
        public static bool PublicReachabilityReady(AppSettings settings,IEnumerable<CheckRow> observations)
        {
            if(settings==null||observations==null)return false;
            var selected=new HashSet<string>((settings.Services??new List<string>()).Where(x=>x!="telegram"),StringComparer.Ordinal);
            if(settings.Mode!=0||selected.Count==0||selected.Any(x=>!ServiceCatalog.All().Any(service=>service.Id==x)))return false;
            var rows=new List<CheckRow>();var map=new Dictionary<string,CheckRow>(StringComparer.Ordinal);
            foreach(var row in observations)
            {
                if(rows.Count>=256||row==null||String.IsNullOrEmpty(row.Key)||map.ContainsKey(row.Key))return false;
                map.Add(row.Key,row);rows.Add(row);
                if((selected.Contains(row.ServiceId)||row.ServiceId=="control")&&row.Passed==false)return false;
            }
            foreach(string id in selected)
            {
                if(!Passed(map,id,id+".web.tls")||!Passed(map,id,id+".web.http"))return false;
                if(id=="steam"&&(!Passed(map,id,"steam.community.tls")||!Passed(map,id,"steam.community.http")))return false;
            }
            return !selected.Contains("discord")||Tuning.DiscordBootstrapReady(rows);
        }
        static bool Passed(Dictionary<string,CheckRow> rows,string service,string key)
        {CheckRow value;return rows.TryGetValue(key,out value)&&value.ServiceId==service&&value.Passed==true;}

        public static bool ShouldTryDiscordMethod(AppSettings settings,IEnumerable<CheckRow> observations)
        {
            if(settings==null||settings.Mode!=0||settings.Services==null||!settings.Services.Contains("discord")||observations==null)return false;
            var rows=observations.Take(257).ToList();
            if(rows.Count>256||rows.Any(x=>x==null))return false;
            return rows.Any(x=>x.ServiceId=="discord"&&x.Passed==false)&&!Tuning.DiscordBootstrapReady(rows);
        }
    }

    // This is a short-lived routing hint, never permission to start a filter.
    // Lookups contain hashes, fixed method labels and evidence kind only. The
    // caller rechecks a cached candidate before claiming current availability.
    public sealed class AutomaticSetupCache
    {
        public const int MaximumEntries=128,MaximumBytes=128*1024,MaximumAgeDays=7;
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        readonly string path;
        public bool ReadProblem {get;private set;}
        public static string DefaultPath {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LocalLink","automatic-methods-v1.json");}}
        public AutomaticSetupCache():this(DefaultPath){}
        public AutomaticSetupCache(string file)
        {
            if(String.IsNullOrWhiteSpace(file)||!Regex.IsMatch(file,@"\A[A-Za-z]:[\\/]")||file.IndexOf(':',2)>=0||!String.Equals(Path.GetExtension(file),".json",StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Нужен локальный JSON-файл методов.","file");
            path=Path.GetFullPath(file);RejectReparse(path);
        }

        public AutomaticMethodChoice Find(string networkKey,AppSettings settings,DateTime utcNow,string environmentFingerprint="")
        {
            RequireUtc(utcNow);string fingerprint=SettingsFingerprint(settings,environmentFingerprint);
            if(!IsKey(networkKey)||settings.Mode!=0)return null;
            try
            {
                var rows=Read(path,true);ReadProblem=false;
                var found=rows.FirstOrDefault(x=>x.Network==networkKey&&x.Settings==fingerprint&&Fresh(x.VerifiedUtc,utcNow));
                return found==null?null:found.Choice();
            }
            catch(IOException){ReadProblem=true;return null;}
            catch(UnauthorizedAccessException){ReadProblem=true;return null;}
            catch(ArgumentException){ReadProblem=true;return null;}
            catch(InvalidOperationException){ReadProblem=true;return null;}
            catch(System.Security.SecurityException){ReadProblem=true;return null;}
        }

        public bool RememberVerified(string networkKey,AppSettings settings,TuningResult result,DateTime utcNow,string environmentFingerprint="")
        {
            RequireUtc(utcNow);
            if(settings==null)throw new ArgumentNullException("settings");
            if(result==null||!result.Enabled||!result.Verified||result.Cancelled||result.TimedOut||result.VpnOverride||settings.Mode!=0)return false;
            string effective=result.Scope=="general"?settings.Strategy:result.Scope=="discord"?
                (String.IsNullOrEmpty(settings.DiscordStrategy)?settings.Strategy:settings.DiscordStrategy):null;
            if(result.Strategy!=effective||!Profiles.IsTlsStrategy(effective)||result.Trials==null||
                !result.Trials.Any(x=>x!=null&&x.Stage=="verification"&&x.Strategy==effective&&x.MatrixValid&&x.PreservedBaseline&&x.Improved&&x.Verified))return false;
            return Remember(networkKey,settings,utcNow,environmentFingerprint,"repeated-public-probes");
        }
        public bool RememberReachable(string networkKey,AppSettings settings,IEnumerable<CheckRow> observations,DateTime utcNow,string environmentFingerprint="")
        {
            RequireUtc(utcNow);
            if(!AutomaticSetupPolicy.PublicReachabilityReady(settings,observations))return false;
            return Remember(networkKey,settings,utcNow,environmentFingerprint,"public-reachable");
        }
        // Called only by an explicit user action, never as a substitute for a
        // failed/unknown automatic probe or for a successful process start.
        public bool RememberUserConfirmed(string networkKey,AppSettings settings,DateTime utcNow,string environmentFingerprint="")
        {RequireUtc(utcNow);return Remember(networkKey,settings,utcNow,environmentFingerprint,"user-confirmed");}

        public bool Forget(string networkKey,AppSettings settings,string environmentFingerprint="")
        {
            RequireKey(networkKey,"networkKey");string fingerprint=SettingsFingerprint(settings,environmentFingerprint);
            var rows=Read(path,true);int removed=rows.RemoveAll(x=>x.Network==networkKey&&x.Settings==fingerprint);
            if(removed>0)Save(rows);return removed>0;
        }

        bool Remember(string networkKey,AppSettings settings,DateTime now,string environment,string evidence)
        {
            RequireKey(networkKey,"networkKey");
            if(settings==null)throw new ArgumentNullException("settings");
            if(settings.Mode!=0||!Profiles.IsTlsStrategy(settings.Strategy)||
                (!String.IsNullOrEmpty(settings.DiscordStrategy)&&!Profiles.IsTlsStrategy(settings.DiscordStrategy)))return false;
            string fingerprint=SettingsFingerprint(settings,environment);
            var rows=Read(path,true);var old=rows.FirstOrDefault(x=>x.Network==networkKey&&x.Settings==fingerprint);
            // Unchanged startup hints do not create a new disk revision every
            // time Connect is pressed. The observation ages out normally.
            if(old!=null&&old.Strategy==settings.Strategy&&old.DiscordStrategy==(settings.DiscordStrategy??"")&&old.Evidence==evidence&&
                Fresh(old.VerifiedUtc,now)&&now-old.VerifiedUtc<TimeSpan.FromHours(24))return true;
            rows.RemoveAll(x=>!Fresh(x.VerifiedUtc,now)||(x.Network==networkKey&&x.Settings==fingerprint));
            rows.Add(new Entry{Network=networkKey,Settings=fingerprint,Strategy=settings.Strategy,DiscordStrategy=settings.DiscordStrategy??"",Evidence=evidence,VerifiedUtc=now});
            while(rows.Count>MaximumEntries)
            {
                var oldest=rows.OrderBy(x=>x.VerifiedUtc).ThenBy(x=>x.Network,StringComparer.Ordinal).ThenBy(x=>x.Settings,StringComparer.Ordinal).First();
                rows.Remove(oldest);
            }
            Save(rows);ReadProblem=false;return true;
        }

        public static string SettingsFingerprint(AppSettings settings,string environmentFingerprint="")
        {
            if(settings==null)throw new ArgumentNullException("settings");
            if(environmentFingerprint==null||environmentFingerprint.Length>256||environmentFingerprint.Any(Char.IsControl))
                throw new ArgumentException("Некорректная версия окружения.","environmentFingerprint");
            var value=AppSettings.Normalize(new AppSettings{Services=new List<string>(settings.Services??new List<string>()),Mode=settings.Mode,
                Strategy=settings.Strategy,DiscordStrategy=settings.DiscordStrategy,Quic=settings.Quic,DiscordVoice=settings.DiscordVoice,TelegramNative=settings.TelegramNative,
                UsePublicLists=settings.UsePublicLists,UseCustomDomains=settings.UseCustomDomains,DisabledDomains=new List<string>(settings.DisabledDomains??new List<string>()),
                CustomDomains=settings.CustomDomains,Endpoint=settings.Endpoint,Port=settings.Port});
            var document=new SortedDictionary<string,object>(StringComparer.Ordinal){
                {"Schema","automatic-routing-v1"},{"Mode",value.Mode},{"Services",value.Services.OrderBy(x=>x,StringComparer.Ordinal).ToArray()},
                {"Quic",value.Quic},{"DiscordVoice",value.DiscordVoice},{"TelegramNative",value.TelegramNative},{"UsePublicLists",value.UsePublicLists},
                {"UseCustomDomains",value.UseCustomDomains},{"DisabledDomains",value.DisabledDomains.OrderBy(x=>x,StringComparer.Ordinal).ToArray()},
                {"CustomDomains",value.UseCustomDomains&&!String.IsNullOrWhiteSpace(value.CustomDomains)?Profiles.ValidateDomains(value.CustomDomains).OrderBy(x=>x,StringComparer.Ordinal).ToArray():new string[0]},
                {"Endpoint",value.Mode==0?"":value.Endpoint},{"Port",value.Mode==0?443:value.Port},{"Environment",environmentFingerprint}};
            byte[] bytes=Utf8.GetBytes(new JavaScriptSerializer{MaxJsonLength=2*1024*1024}.Serialize(document));
            if(bytes.Length>2*1024*1024)throw new ArgumentException("Слишком большие настройки.","settings");
            using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();
        }

        sealed class Entry
        {
            internal string Network,Settings,Strategy,DiscordStrategy,Evidence;
            internal DateTime VerifiedUtc;
            internal AutomaticMethodChoice Choice(){return new AutomaticMethodChoice{Strategy=Strategy,DiscordStrategy=DiscordStrategy,Evidence=Evidence,VerifiedUtc=VerifiedUtc};}
        }
        static bool Fresh(DateTime when,DateTime now)
        {TimeSpan age=now-when;return age>=TimeSpan.FromMinutes(-5)&&age<=TimeSpan.FromDays(MaximumAgeDays);}
        static void RequireUtc(DateTime value){if(value.Kind!=DateTimeKind.Utc)throw new ArgumentException("Нужно время UTC.","utcNow");}
        static bool IsKey(string value){return value!=null&&Regex.IsMatch(value,@"\A[a-f0-9]{64}\z",RegexOptions.CultureInvariant);}
        static void RequireKey(string value,string name){if(!IsKey(value))throw new ArgumentException("Некорректный хеш настройки сети.",name);}
        static bool IsEvidence(string value){return value=="repeated-public-probes"||value=="public-reachable"||value=="user-confirmed";}
        List<Entry> Read(string file,bool missingAllowed)
        {
            RejectReparse(file);byte[] bytes;
            try
            {
                using(var input=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read))
                {
                    if(input.Length<1||input.Length>MaximumBytes)throw new IOException("Недопустимый размер кеша методов.");
                    bytes=new byte[(int)input.Length];int count=0,read;
                    while(count<bytes.Length&&(read=input.Read(bytes,count,bytes.Length-count))>0)count+=read;
                    if(count!=bytes.Length||input.ReadByte()!=-1)throw new IOException("Кеш методов изменился при чтении.");
                }
            }
            catch(FileNotFoundException){if(missingAllowed)return new List<Entry>();throw;}
            catch(DirectoryNotFoundException){if(missingAllowed)return new List<Entry>();throw;}
            string json=Utf8.GetString(bytes);if(json.Length>0&&json[0]=='\uFEFF')json=json.Substring(1);
            var data=new JavaScriptSerializer{MaxJsonLength=MaximumBytes,RecursionLimit=8}.DeserializeObject(json) as Dictionary<string,object>;
            object schema,items;
            if(data==null||data.Count!=2||!data.TryGetValue("Schema",out schema)||!(schema is int)||(int)schema!=1||!data.TryGetValue("Entries",out items)||!(items is object[]))
                throw new IOException("Неподдерживаемый кеш методов.");
            var rows=(object[])items;if(rows.Length>MaximumEntries)throw new IOException("Слишком много сохранённых методов.");
            var result=new List<Entry>();var keys=new HashSet<string>(StringComparer.Ordinal);
            foreach(object item in rows)
            {
                var row=item as Dictionary<string,object>;string[] expected={"Network","Settings","Strategy","DiscordStrategy","Evidence","VerifiedUtc"};
                if(row==null||row.Count!=expected.Length||expected.Any(x=>!row.ContainsKey(x)||!(row[x] is string)))throw new IOException("Некорректный метод сети.");
                var entry=new Entry{Network=(string)row["Network"],Settings=(string)row["Settings"],Strategy=(string)row["Strategy"],DiscordStrategy=(string)row["DiscordStrategy"],Evidence=(string)row["Evidence"]};
                RequireKey(entry.Network,"networkKey");RequireKey(entry.Settings,"settingsFingerprint");
                if(!Profiles.IsTlsStrategy(entry.Strategy)||(entry.DiscordStrategy.Length>0&&!Profiles.IsTlsStrategy(entry.DiscordStrategy))||!IsEvidence(entry.Evidence)||
                    !DateTime.TryParseExact((string)row["VerifiedUtc"],"o",CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out entry.VerifiedUtc)||entry.VerifiedUtc.Kind!=DateTimeKind.Utc||
                    !keys.Add(entry.Network+entry.Settings))throw new IOException("Некорректное подтверждение метода.");
                result.Add(entry);
            }
            return result;
        }
        void Save(List<Entry> values)
        {
            var rows=values.OrderBy(x=>x.Network,StringComparer.Ordinal).ThenBy(x=>x.Settings,StringComparer.Ordinal).Select(x=>new SortedDictionary<string,object>(StringComparer.Ordinal){
                {"Network",x.Network},{"Settings",x.Settings},{"Strategy",x.Strategy},{"DiscordStrategy",x.DiscordStrategy},{"Evidence",x.Evidence},{"VerifiedUtc",x.VerifiedUtc.ToString("o",CultureInfo.InvariantCulture)}}).ToArray();
            byte[] bytes=Utf8.GetBytes(new JavaScriptSerializer{MaxJsonLength=MaximumBytes,RecursionLimit=8}.Serialize(new SortedDictionary<string,object>{{"Schema",1},{"Entries",rows}}));
            if(bytes.Length>MaximumBytes)throw new IOException("Кеш методов слишком большой.");
            RejectReparse(path);string folder=Path.GetDirectoryName(path);Directory.CreateDirectory(folder);RejectReparse(folder);
            string stage=path+".staged-"+Guid.NewGuid().ToString("N").Substring(0,12),backup=path+".previous";
            RejectReparse(stage);RejectReparse(backup);
            // A foreign/corrupt file is never overwritten as an incidental
            // backup. Failed writes leave the valid primary intact.
            if(File.Exists(backup))Read(backup,false);
            if(Directory.Exists(backup))throw new IOException("Резервный путь занят папкой.");
            using(var output=new FileStream(stage,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(bytes,0,bytes.Length);output.Flush(true);}
            Read(stage,false);RejectReparse(path);RejectReparse(stage);RejectReparse(backup);
            if(File.Exists(path))File.Replace(stage,path,backup);else File.Move(stage,path);
        }
        static void RejectReparse(string value)
        {
            for(string current=Path.GetFullPath(value);!String.IsNullOrEmpty(current);current=Path.GetDirectoryName(current))
            {
                FileAttributes attributes;
                try{attributes=File.GetAttributes(current);}catch(FileNotFoundException){continue;}catch(DirectoryNotFoundException){continue;}
                if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Кеш методов содержит перенаправление.");
            }
        }
    }
}
