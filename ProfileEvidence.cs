using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace LocalLink
{
    // A deliberately lossy, immutable projection of ACTUAL prepared arguments.
    // No argument string/hash, hostname, address, endpoint, local path, process
    // identifier, certificate, account or packet is exposed by these models.
    public sealed class ProfileSummary
    {
        // Up to 128 selected hosts plus fixed protocol-specific host groups.
        const int MaximumInlineSummaryDomains=160;
        public string Mode { get; private set; }
        public string Strategy { get; private set; }
        public string DiscordStrategy { get; private set; }
        public string TcpPorts { get; private set; }
        public string UdpPorts { get; private set; }
        public int ProfileCount { get; private set; }
        public int InlineDomainCount { get; private set; }
        public int? ExternalListDomainCount { get; private set; }
        public bool UsesExternalList { get; private set; }
        public bool EndpointScoped { get; private set; }
        public bool Quic { get; private set; }
        public bool DiscordVoice { get; private set; }
        public bool TelegramNative { get; private set; }
        public bool EngineFilesVerifiedAtPreflight { get; private set; }
        public string KnownEngineSha256 { get; private set; }
        private readonly Dictionary<string,string> targetCoverage;
        // Fixed public diagnostic target IDs only. A fresh copy preserves the
        // summary's immutability. Static hostlist scope is NOT observed SNI,
        // packet matching, driver effectiveness or a connectivity proof.
        public Dictionary<string,string> TargetCoverage
        {get{return new Dictionary<string,string>(targetCoverage,StringComparer.Ordinal);}}
        static readonly string[] CoverageIds={"discord-site","discord-gateway","youtube-site","youtube-www","youtube-image"};
        static readonly string[] CoverageHosts={"discord.com","gateway.discord.gg","youtube.com","www.youtube.com","i.ytimg.com"};

        internal ProfileSummary(string mode,string strategy,string tcp,string udp,int profiles,int inline,
            int? external,bool list,bool endpoint,bool quic,bool voice,bool telegram,bool verified,string sha,
            Dictionary<string,string> coverage,string discordStrategy="")
        {
            Mode=mode;Strategy=strategy;TcpPorts=tcp;UdpPorts=udp;ProfileCount=profiles;InlineDomainCount=inline;
            ExternalListDomainCount=external;UsesExternalList=list;EndpointScoped=endpoint;Quic=quic;
            DiscordVoice=voice;TelegramNative=telegram;EngineFilesVerifiedAtPreflight=verified;KnownEngineSha256=sha;
            targetCoverage=new Dictionary<string,string>(coverage,StringComparer.Ordinal);
            DiscordStrategy=discordStrategy;
        }
        public string ToDisplayString()
        {
            string domains=InlineDomainCount.ToString(CultureInfo.InvariantCulture);
            if(UsesExternalList)domains+=" + список "+(ExternalListDomainCount.HasValue?
                ExternalListDomainCount.Value.ToString(CultureInfo.InvariantCulture):"неизвестно");
            return Strategy+(DiscordStrategy.Length==0?"":" · Discord "+DiscordStrategy)+" · TCP "+(TcpPorts.Length==0?"—":TcpPorts)+
                (UdpPorts.Length==0?"":" · UDP "+UdpPorts)+" · профилей "+ProfileCount.ToString(CultureInfo.InvariantCulture)+
                " · доменов "+domains;
        }
        internal Dictionary<string,object> Fields()
        {
            var fields=new Dictionary<string,object>(StringComparer.Ordinal)
            {
                {"Mode",Mode},{"Strategy",Strategy},{"TcpPorts",TcpPorts},{"UdpPorts",UdpPorts},
                {"ProfileCount",ProfileCount},{"InlineDomainCount",InlineDomainCount},
                {"ExternalListDomainCount",ExternalListDomainCount},{"UsesExternalList",UsesExternalList},
                {"EndpointScoped",EndpointScoped},{"Quic",Quic},{"DiscordVoice",DiscordVoice},
                {"TelegramNative",TelegramNative},{"EngineFilesVerifiedAtPreflight",EngineFilesVerifiedAtPreflight},
                {"KnownEngineSha256",KnownEngineSha256},{"TargetCoverage",TargetCoverage}
            };
            if(DiscordStrategy.Length>0)fields.Add("DiscordStrategy",DiscordStrategy);
            return fields;
        }
        internal static ProfileSummary FromFields(Dictionary<string,object> fields)
        {
            bool separate=fields!=null&&fields.ContainsKey("DiscordStrategy");
            var expected=new List<string>(new[]{"Mode","Strategy","TcpPorts","UdpPorts","ProfileCount","InlineDomainCount",
                "ExternalListDomainCount","UsesExternalList","EndpointScoped","Quic","DiscordVoice","TelegramNative",
                "EngineFilesVerifiedAtPreflight","KnownEngineSha256","TargetCoverage"});
            if(separate)expected.Add("DiscordStrategy");
            CheckKeys(fields,expected.ToArray());
            string mode=Text(fields,"Mode"),strategy=Text(fields,"Strategy");
            string discord=separate?Text(fields,"DiscordStrategy"):"";
            if(separate&&(!KnownTlsMethod(discord)&&discord!="unrecognized"||mode!="direct"))throw Invalid();
            if(mode!="direct"&&mode!="vpn-tls"&&mode!="vpn-wireguard")throw Invalid();
            if(strategy!="split"&&strategy!="sni-split"&&strategy!="disorder"&&strategy!="fake-split"&&
                strategy!="fake-disorder"&&strategy!="wireguard"&&strategy!="unrecognized")throw Invalid();
            string tcp=NormalizePorts(Text(fields,"TcpPorts")),udp=NormalizePorts(Text(fields,"UdpPorts"));
            if(tcp!=Text(fields,"TcpPorts")||udp!=Text(fields,"UdpPorts"))throw Invalid();
            int profiles=Integer(fields,"ProfileCount",1,32),inline=Integer(fields,"InlineDomainCount",0,MaximumInlineSummaryDomains);
            int? external=fields["ExternalListDomainCount"]==null?(int?)null:Integer(fields,"ExternalListDomainCount",1,2000000);
            bool list=Flag(fields,"UsesExternalList"),endpoint=Flag(fields,"EndpointScoped"),quic=Flag(fields,"Quic"),
                voice=Flag(fields,"DiscordVoice"),telegram=Flag(fields,"TelegramNative"),verified=Flag(fields,"EngineFilesVerifiedAtPreflight");
            string sha=Text(fields,"KnownEngineSha256");
            var coverageFields=fields["TargetCoverage"] as Dictionary<string,object>;
            // Fields() supplies a typed map; JSON deserialization supplies the
            // object-valued map. Both paths enforce the same exact five IDs.
            var typedCoverage=fields["TargetCoverage"] as Dictionary<string,string>;
            if(coverageFields==null&&typedCoverage!=null)
            {
                coverageFields=new Dictionary<string,object>(StringComparer.Ordinal);
                foreach(var entry in typedCoverage)coverageFields.Add(entry.Key,entry.Value);
            }
            CheckKeys(coverageFields,CoverageIds);
            var coverage=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(string id in CoverageIds)
            {
                string status=Text(coverageFields,id);
                if(status!="inline-domain-match"&&status!="external-list-unchecked"&&status!="no-domain-match")throw Invalid();
                if(!list&&status=="external-list-unchecked")throw Invalid();
                coverage.Add(id,status);
            }
            if(!IsSha256(sha)||(!list&&external.HasValue)||(mode=="direct"&&endpoint)||
                (mode!="direct"&&(!endpoint||quic||voice||telegram||list))||!verified)throw Invalid();
            return new ProfileSummary(mode,strategy,tcp,udp,profiles,inline,external,list,endpoint,quic,voice,telegram,verified,sha,coverage,discord);
        }

        // Pure; input strings are only inspected locally, not returned, hashed
        // or persisted. A future unknown action is explicitly unrecognized.
        public static ProfileSummary FromArguments(IEnumerable<string> arguments,int? externalListDomains,
            bool engineFilesVerified,string knownEngineSha256)
        {
            if(arguments==null)throw new ArgumentNullException("arguments");
            if(externalListDomains.HasValue&&(externalListDomains.Value<1||externalListDomains.Value>2000000))throw Invalid();
            if(!String.IsNullOrEmpty(knownEngineSha256)&&!IsSha256(knownEngineSha256))throw Invalid();
            var tcp=new List<string>();var udp=new List<string>();var domains=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var first=new List<string>();bool inFirst=true,list=false,endpoint=false,quic=false,voice=false,telegram=false,wireguard=false;
            var groups=new List<List<string>>();var group=new List<string>();groups.Add(group);
            int profiles=1,count=0;
            foreach(string argument in arguments)
            {
                if(argument==null||argument.Length>16384||++count>512)throw Invalid();
                if(argument=="--new"){profiles++;inFirst=false;group=new List<string>();groups.Add(group);continue;}
                group.Add(argument);
                if(inFirst)first.Add(argument);
                if(argument.StartsWith("--filter-tcp=",StringComparison.Ordinal))tcp.Add(argument.Substring(13));
                if(argument.StartsWith("--filter-udp=",StringComparison.Ordinal))udp.Add(argument.Substring(13));
                if(argument.StartsWith("--hostlist-domains=",StringComparison.Ordinal))
                    foreach(string domain in argument.Substring(19).Split(','))if(domain.Length>0)domains.Add(domain);
                if(argument.StartsWith("--hostlist=",StringComparison.Ordinal))list=true;
                if(argument.StartsWith("--wf-raw-filter=",StringComparison.Ordinal))endpoint=true;
                if(argument=="--filter-l7=quic")quic=true;
                if(argument=="--filter-l7=discord,stun")voice=true;
                if(argument=="--filter-l7=mtproto,unknown")telegram=true;
                if(argument=="--filter-l7=wireguard")wireguard=true;
            }
            if(profiles>32||domains.Count>MaximumInlineSummaryDomains||count==0)throw Invalid();
            // With a Discord override the first TLS profile is dedicated.
            // Infer the GENERAL method from the second TLS443 profile, never
            // from the UI setting or the override's method by accident.
            string discordStrategy="";var tls443=new List<List<string>>();
            foreach(var item in groups)if(item.Contains("--filter-tcp=443")&&item.Contains("--filter-l7=tls"))tls443.Add(item);
            if(!endpoint&&tls443.Count==2&&tls443[0].Contains("--hostlist-domains="+Profiles.DiscordDomains))
            {
                discordStrategy=InferStrategy(tls443[0],false);first=tls443[1];
            }
            string strategy=InferStrategy(first,wireguard);
            string mode=endpoint?(wireguard?"vpn-wireguard":"vpn-tls"):"direct";
            var coverage=new Dictionary<string,string>(StringComparer.Ordinal);
            for(int target=0;target<CoverageIds.Length;target++)
            {
                bool matched=false;
                foreach(string domain in domains)
                    if(String.Equals(CoverageHosts[target],domain,StringComparison.OrdinalIgnoreCase)||
                        CoverageHosts[target].EndsWith("."+domain,StringComparison.OrdinalIgnoreCase))
                    {matched=true;break;}
                coverage.Add(CoverageIds[target],matched?"inline-domain-match":list?"external-list-unchecked":"no-domain-match");
            }
            return new ProfileSummary(mode,strategy,NormalizePorts(String.Join(",",tcp.ToArray())),
                NormalizePorts(String.Join(",",udp.ToArray())),profiles,domains.Count,list?externalListDomains:null,list,
                endpoint,quic,voice,telegram,engineFilesVerified,(knownEngineSha256??String.Empty).ToLowerInvariant(),coverage,discordStrategy);
        }
        static string InferStrategy(List<string> first,bool wireguard)
        {
            bool fake=first.Contains("--lua-desync=fake:blob=fake_default_tls:tcp_md5:repeats=2");
            string strategy=wireguard?"wireguard":first.Contains("--lua-desync=multidisorder:pos=1,midsld")?
                (fake?"fake-disorder":"disorder"):first.Contains("--lua-desync=multisplit:pos=1,midsld")?
                (fake?"unrecognized":"sni-split"):first.Contains("--lua-desync=multisplit:pos=2")?
                (fake?"fake-split":"split"):"unrecognized";
            int actions=0;
            foreach(string argument in first)
                if(argument.StartsWith("--lua-desync=",StringComparison.Ordinal))
                {
                    actions++;
                    if(argument!="--lua-desync=fake:blob=fake_default_tls:tcp_md5:repeats=2"&&
                        argument!="--lua-desync=multisplit:pos=2"&&argument!="--lua-desync=multisplit:pos=1,midsld"&&
                        argument!="--lua-desync=multidisorder:pos=1,midsld"&&
                        !(wireguard&&argument=="--lua-desync=fake:blob=0x00000000000000000000000000000000:repeats=2"))
                        strategy="unrecognized";
                }
            if(actions!=(wireguard?1:fake?2:1))strategy="unrecognized";
            return strategy;
        }
        static bool KnownTlsMethod(string value)
        {return value=="split"||value=="sni-split"||value=="disorder"||value=="fake-split"||value=="fake-disorder";}
        internal static string NormalizePorts(string value)
        {
            if(value==null||value.Length>256)throw Invalid();
            var ports=new SortedDictionary<int,string>();
            if(value.Length==0)return String.Empty;
            foreach(string part in value.Split(','))
            {
                string[] ends=part.Split('-');int start,end;
                if(ends.Length<1||ends.Length>2||!Int32.TryParse(ends[0],NumberStyles.None,CultureInfo.InvariantCulture,out start)||
                    start<1||start>65535)throw Invalid();
                end=start;
                if(ends.Length==2&&(!Int32.TryParse(ends[1],NumberStyles.None,CultureInfo.InvariantCulture,out end)||end<start||end>65535))throw Invalid();
                string normalized=start.ToString(CultureInfo.InvariantCulture)+(end==start?"":"-"+end.ToString(CultureInfo.InvariantCulture));
                string previous;if(ports.TryGetValue(start,out previous)&&previous!=normalized)throw Invalid();
                ports[start]=normalized;
            }
            return String.Join(",",new List<string>(ports.Values).ToArray());
        }
        internal static bool IsSha256(string value)
        {
            if(value==null||value.Length!=64)return false;
            foreach(char c in value)if(!(c>='0'&&c<='9')&&!(c>='a'&&c<='f'))return false;
            return true;
        }
        internal static void CheckKeys(Dictionary<string,object> fields,string[] expected)
        {
            if(fields==null||fields.Count!=expected.Length)throw Invalid();
            foreach(string key in expected)if(!fields.ContainsKey(key))throw Invalid();
        }
        internal static string Text(Dictionary<string,object> fields,string key)
        {string value=fields[key] as string;if(value==null)throw Invalid();return value;}
        internal static bool Flag(Dictionary<string,object> fields,string key)
        {if(!(fields[key] is bool))throw Invalid();return (bool)fields[key];}
        internal static int Integer(Dictionary<string,object> fields,string key,int minimum,int maximum)
        {
            object value=fields[key];if(!(value is int))throw Invalid();int number=(int)value;
            if(number<minimum||number>maximum)throw Invalid();return number;
        }
        internal static IOException Invalid(){return new IOException("Некорректная запись профиля; исходные данные не выводятся.");}
    }

    public sealed class ProfileLaunchEvidence
    {
        public ProfileSummary Profile { get; private set; }
        public string Status { get; private set; }
        public string ReceiptStatus { get; private set; }
        public string Scope { get { return "Actual argument snapshot supplied to this owned process; not packet/driver effectiveness or connectivity proof."; } }
        internal ProfileLaunchEvidence(ProfileSummary profile,string status,string receipt)
        {Profile=profile;Status=status;ReceiptStatus=receipt;}
        public string ToDisplayString(){return Profile.ToDisplayString()+" · "+Status+(ReceiptStatus=="saved"?"":" · запись недоступна");}
    }

    public sealed class ProfileReceiptEvidence
    {
        public string Status { get; private set; }
        public bool Historical { get; private set; }
        public ProfileSummary Profile { get; private set; }
        public string Scope { get { return "Unverified local receipt, not cryptographic attestation. A matching process name and creation time do not prove its current arguments, packets or successful bypass. TargetCoverage is a static inline-domain suffix projection; external file contents are unchecked and actual SNI/packet matches are not observed. No PID, process start time, path, raw arguments, domains or endpoint exported."; } }
        internal ProfileReceiptEvidence(string status,bool historical,ProfileSummary profile)
        {Status=status;Historical=historical;Profile=profile;}
    }

    public static class ProfileReceiptStore
    {
        public const int MaximumReceiptBytes=16384;
        const string FileName="profile-receipt-v1.json";
        static readonly UTF8Encoding EncodingUtf8=new UTF8Encoding(false,true);
        internal sealed class Record
        {
            internal string Identity,State;
            internal int ProcessId;
            internal long StartedUtcTicks;
            internal ProfileSummary Summary;
        }
        internal static string DefaultPath()
        {
            string folder=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if(String.IsNullOrEmpty(folder))throw ProfileSummary.Invalid();
            return Path.Combine(folder,"LocalLink",FileName);
        }
        internal static string WriteStart(ProfileSummary summary,int pid,long startedUtcTicks,string identity)
        {
            return TryWrite(new Record{Summary=summary,ProcessId=pid,StartedUtcTicks=startedUtcTicks,Identity=identity,State="running"});
        }
        internal static string WriteStop(ProfileSummary summary,int pid,long startedUtcTicks,string identity)
        {
            return TryWrite(new Record{Summary=summary,ProcessId=pid,StartedUtcTicks=startedUtcTicks,Identity=identity,State="stopped"});
        }
        static string TryWrite(Record record)
        {
            try{WriteToPath(DefaultPath(),record,null);return "saved";}
            catch(IOException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}
            catch(InvalidOperationException){}catch(System.Security.SecurityException){}
            return "unavailable";
        }
        public static ProfileReceiptEvidence ReadCurrent()
        {
            try{return ReadFromPath(DefaultPath(),ObserveProcess);}
            catch(IOException){return new ProfileReceiptEvidence("unavailable",false,null);}
            catch(System.Security.SecurityException){return new ProfileReceiptEvidence("unavailable",false,null);}
        }
        internal static ProfileReceiptEvidence ReadFromPath(string path,Func<int,long,string> observe)
        {
            try
            {
                path=CanonicalPath(path);RejectReparse(path);
                if(!File.Exists(path))return new ProfileReceiptEvidence("missing",false,null);
                Record record=ReadRecord(path);
                if(record.State=="stopped")return new ProfileReceiptEvidence("stopped-history",true,record.Summary);
                string match=observe(record.ProcessId,record.StartedUtcTicks);
                if(match=="match")return new ProfileReceiptEvidence("running-match-unverified",false,record.Summary);
                if(match=="absent"||match=="mismatch")return new ProfileReceiptEvidence("stale-history",true,record.Summary);
                return new ProfileReceiptEvidence("unverified-history",true,record.Summary);
            }
            catch(IOException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}
            catch(InvalidOperationException){}catch(System.Security.SecurityException){}
            return new ProfileReceiptEvidence("invalid",true,null);
        }
        static string ObserveProcess(int pid,long startedUtcTicks)
        {
            try
            {
                using(var process=Process.GetProcessById(pid))
                {
                    if(process.HasExited)return "absent";
                    return process.ProcessName=="winws2"&&process.StartTime.ToUniversalTime().Ticks==startedUtcTicks?"match":"mismatch";
                }
            }
            catch(ArgumentException){return "absent";}
            catch(InvalidOperationException){return "absent";}
            catch(System.ComponentModel.Win32Exception){return "unavailable";}
            catch(System.Security.SecurityException){return "unavailable";}
        }
        internal static void WriteToPath(string path,Record record,Action<string> beforeCommit)
        {
            path=CanonicalPath(path);RejectReparse(path);
            Validate(record);
            Directory.CreateDirectory(Path.GetDirectoryName(path));RejectReparse(path);
            byte[] bytes=EncodingUtf8.GetBytes(Serializer().Serialize(new Dictionary<string,object>
            {
                {"Schema","LocalLink.profile-receipt.v1"},{"Identity",record.Identity},{"State",record.State},
                {"InternalProcessId",record.ProcessId},{"InternalStartedUtcTicks",record.StartedUtcTicks},{"Profile",record.Summary.Fields()}
            }));
            if(bytes.Length>MaximumReceiptBytes)throw ProfileSummary.Invalid();
            string suffix=DateTime.UtcNow.Ticks.ToString("D19",CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N");
            string stage=path+".staged-"+suffix,backup=path+".backup-"+suffix;
            RejectReparse(stage);
            using(var stream=new FileStream(stage,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {stream.Write(bytes,0,bytes.Length);stream.Flush(true);}
            if(beforeCommit!=null)beforeCommit(stage);
            ReadRecord(stage);RejectReparse(path);RejectReparse(stage);RejectReparse(backup);
            if(File.Exists(backup)||Directory.Exists(backup))throw ProfileSummary.Invalid();
            if(File.Exists(path))File.Replace(stage,path,backup);
            else File.Move(stage,path);
        }
        static Record ReadRecord(string path)
        {
            RejectReparse(path);byte[] bytes;
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                if(stream.Length<1||stream.Length>MaximumReceiptBytes)throw ProfileSummary.Invalid();
                bytes=new byte[(int)stream.Length];int offset=0;
                while(offset<bytes.Length){int count=stream.Read(bytes,offset,bytes.Length-offset);if(count==0)throw ProfileSummary.Invalid();offset+=count;}
            }
            var fields=Serializer().DeserializeObject(EncodingUtf8.GetString(bytes)) as Dictionary<string,object>;
            ProfileSummary.CheckKeys(fields,new[]{"Schema","Identity","State","InternalProcessId","InternalStartedUtcTicks","Profile"});
            if(ProfileSummary.Text(fields,"Schema")!="LocalLink.profile-receipt.v1")throw ProfileSummary.Invalid();
            object ticksValue=fields["InternalStartedUtcTicks"];
            if(!(ticksValue is long))throw ProfileSummary.Invalid();
            var record=new Record{Identity=ProfileSummary.Text(fields,"Identity"),State=ProfileSummary.Text(fields,"State"),
                ProcessId=ProfileSummary.Integer(fields,"InternalProcessId",1,Int32.MaxValue),StartedUtcTicks=(long)ticksValue,
                Summary=ProfileSummary.FromFields(fields["Profile"] as Dictionary<string,object>)};
            Validate(record);return record;
        }
        static void Validate(Record record)
        {
            if(record==null||record.Summary==null||record.ProcessId<1||record.StartedUtcTicks<new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).Ticks||
                record.StartedUtcTicks>DateTime.UtcNow.AddMinutes(5).Ticks||
                (record.State!="running"&&record.State!="stopped")||record.Identity==null||record.Identity.Length!=32)throw ProfileSummary.Invalid();
            foreach(char c in record.Identity)if(!(c>='0'&&c<='9')&&!(c>='a'&&c<='f'))throw ProfileSummary.Invalid();
            ProfileSummary.FromFields(record.Summary.Fields());
        }
        static JavaScriptSerializer Serializer(){return new JavaScriptSerializer{MaxJsonLength=MaximumReceiptBytes,RecursionLimit=8};}
        static string CanonicalPath(string path)
        {
            if(String.IsNullOrEmpty(path))throw ProfileSummary.Invalid();
            string full=Path.GetFullPath(path);
            if(full.Length<4||!Char.IsLetter(full[0])||full[1]!=':'||full[2]!='\\'||
                !String.Equals(Path.GetFileName(full),FileName,StringComparison.OrdinalIgnoreCase))throw ProfileSummary.Invalid();
            return full;
        }
        static void RejectReparse(string path)
        {
            string current=Path.GetFullPath(path);
            while(!String.IsNullOrEmpty(current))
            {
                try{if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw ProfileSummary.Invalid();}
                catch(FileNotFoundException){}catch(DirectoryNotFoundException){}
                string parent=Path.GetDirectoryName(current);
                if(parent==current)break;current=parent;
            }
        }
    }
}
