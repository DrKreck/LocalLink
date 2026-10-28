using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LocalLink
{
    // Metadata only: never stdout/stderr, command lines, packet contents or
    // credentials. An unavailable Windows exit code is represented by null.
    public sealed class EngineExitInfo
    {
        public int? ExitCode { get; private set; }
        public DateTime ObservedUtc { get; private set; }
        internal EngineExitInfo(int? code) { ExitCode=code; ObservedUtc=DateTime.UtcNow; }
    }
    // A one-use local verification result. Read handles prevent components and
    // snapshots from changing between worker preflight and launch commit.
    public sealed class PreparedStart : IDisposable
    {
        internal readonly Engine Owner;
        internal readonly long Epoch;
        private readonly string[] arguments;
        public ProfileSummary Summary { get; private set; }
        internal readonly CancellationToken Token;
        private readonly object gate=new object();
        private List<FileStream> guards;
        private FileStream listGuard;
        private bool consumed;
        internal PreparedStart(Engine owner,long epoch,List<string> arguments,List<FileStream> files,FileStream list,CancellationToken token,ProfileSummary summary)
        {Owner=owner;Epoch=epoch;this.arguments=arguments.ToArray();guards=files;listGuard=list;Token=token;Summary=summary;}
        internal List<string> CopyArguments(){return new List<string>(arguments);}
        internal void Claim(Engine owner,out List<FileStream> files,out FileStream list)
        {
            lock(gate)
            {
                if(!Object.ReferenceEquals(owner,Owner))throw new InvalidOperationException("Подготовка принадлежит другому экземпляру движка.");
                if(consumed||guards==null)throw new InvalidOperationException("Подготовка уже использована или закрыта. Повторите запуск.");
                consumed=true;
                files=guards;list=listGuard;guards=null;listGuard=null;
            }
        }
        public void Dispose()
        {
            lock(gate)
            {
                if(listGuard!=null){listGuard.Dispose();listGuard=null;}
                if(guards!=null){foreach(var stream in guards)stream.Dispose();guards=null;}
            }
        }
    }
    // No driver is opened by construction, verification, diagnostics or tests.
    public sealed class Engine : IDisposable
    {
        private IntPtr job;
        private Process child;
        private FileStream hostListGuard;
        private List<FileStream> engineFileGuards;
        private readonly object lifecycle=new object();
        private long preparationEpoch;
        private int disposed;
        private volatile bool running;
        private bool exitObserved;
        private EngineExitInfo unexpectedExit;
        private ProfileLaunchEvidence lastProfileEvidence;
        private string profileIdentity;
        private int profileProcessId;
        private long profileStartedUtcTicks;
        private readonly string root;
        public Engine(string appRoot) { root = Path.GetFullPath(appRoot); }
        public bool IsRunning
        {
            get
            {
                // Never make a status/timer callback wait behind process cleanup.
                if(!Monitor.TryEnter(lifecycle))return running;
                try{ObserveExit();return running;}finally{Monitor.Exit(lifecycle);}
            }
        }
        public ProfileLaunchEvidence LastProfileEvidence
        {
            get
            {
                if(!Monitor.TryEnter(lifecycle))return lastProfileEvidence;
                try{ObserveExit();return lastProfileEvidence;}finally{Monitor.Exit(lifecycle);}
            }
        }
        private void ObserveExit()
        {
            if(child==null){running=false;return;}
            if(!child.HasExited){running=true;return;}
            running=false;
            if(exitObserved)return;
            int? code=null;
            try{code=child.ExitCode;}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
            unexpectedExit=new EngineExitInfo(code);exitObserved=true;
            if(lastProfileEvidence!=null&&lastProfileEvidence.Status=="running")
                lastProfileEvidence=new ProfileLaunchEvidence(lastProfileEvidence.Profile,"unexpected-exit",lastProfileEvidence.ReceiptStatus);
        }
        public EngineExitInfo TakeUnexpectedExit()
        {
            if(!Monitor.TryEnter(lifecycle))return null;
            try{ObserveExit();var result=unexpectedExit;unexpectedExit=null;return result;}finally{Monitor.Exit(lifecycle);}
        }
        public static bool IsAdmin()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        public static string Quote(string value)
        {
            // Windows CreateProcess argument quoting, never a shell command.
            var b = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { b.Append('\\', slashes * 2 + 1); b.Append(c); }
                else { b.Append('\\', slashes); b.Append(c); }
                slashes = 0;
            }
            b.Append('\\', slashes * 2); return b.Append('"').ToString();
        }
        public string VerifyFiles()
        {return VerifyFiles(CancellationToken.None);}
        public string VerifyFiles(CancellationToken token)
        {
            var guards=VerifyComponentFiles(token);
            try{return "Контрольные суммы компонентов совпадают с закреплённой сборкой.";}
            finally{foreach(var stream in guards)stream.Dispose();}
        }
        private List<FileStream> VerifyComponentFiles(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            EngineManifest data;
            using (var stream = typeof(Engine).Assembly.GetManifestResourceStream("LocalLink.EngineManifest"))
            {
                if (stream == null) throw new IOException("Нет встроенного списка компонентов. Пересоберите пакет.");
                using (var reader = new StreamReader(stream))
                    data = new JavaScriptSerializer().Deserialize<EngineManifest>(reader.ReadToEnd());
            }
            if (data == null || data.files == null || data.files.Length < 6)
                throw new IOException("Неполный список файлов движка.");
            string folder = Path.GetFullPath(Path.Combine(root, "engine")) + Path.DirectorySeparatorChar;
            var guards=new List<FileStream>();
            try
            {
                foreach (var f in data.files)
                {
                    token.ThrowIfCancellationRequested();
                    const string prefix = "vendor/engine/";
                    if (f.path == null || !f.path.StartsWith(prefix, StringComparison.Ordinal))
                        throw new IOException("Некорректный путь в списке движка.");
                    string relative = f.path.Substring(prefix.Length);
                    string path = Path.GetFullPath(Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar)));
                    if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Некорректный путь в списке движка.");
                    if (!File.Exists(path)) throw new IOException("Нужны компоненты. Нажмите «Включить» для установки.");
                    var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);guards.Add(stream);
                    string hash=PublicLists.HashStream(stream,token);
                    if (!String.Equals(hash, f.sha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Контрольная сумма не совпала: " + f.path + ". Запуск запрещён.");
                    stream.Position=0;
                }
                token.ThrowIfCancellationRequested();return guards;
            }
            catch{foreach(var stream in guards)stream.Dispose();throw;}
        }
        public Task<PreparedStart> PrepareStartAsync(ProfileSpec spec,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if(Interlocked.CompareExchange(ref disposed,0,0)!=0)throw new ObjectDisposedException("Engine");
            // Capture before scheduling: a Stop issued while the worker is queued
            // must invalidate the result, not become the worker's new baseline.
            long epoch=Interlocked.Read(ref preparationEpoch);
            if(spec==null)throw new ArgumentNullException("spec");
            var snapshot=new ProfileSpec{Mode=spec.Mode,Domains=spec.Domains,Endpoint=spec.Endpoint,Port=spec.Port,
                Strategy=spec.Strategy,DiscordStrategy=spec.DiscordStrategy,Quic=spec.Quic,DiscordVoice=spec.DiscordVoice,
                TelegramNative=spec.TelegramNative,HostListFile=spec.HostListFile,HostListExcludeFile=spec.HostListExcludeFile};
            var args=Profiles.Build(snapshot);
            return Task.Run(()=>PrepareCore(snapshot,args,epoch,token),token);
        }
        private PreparedStart PrepareCore(ProfileSpec spec,List<string> args,long epoch,CancellationToken token)
        {
            List<FileStream> guards=null;FileStream list=null;
            try
            {
                token.ThrowIfCancellationRequested();guards=VerifyComponentFiles(token);
                int verifiedDomains=0;
                if(!String.IsNullOrEmpty(spec.HostListFile))list=PublicLists.OpenVerified(spec.HostListFile,token,out verifiedDomains);
                // Retain the independently verified exclusion snapshot with the
                // component guards through preflight, launch and engine stop.
                if(!String.IsNullOrEmpty(spec.HostListExcludeFile))
                {
                    int verifiedExclusions;
                    guards.Add(PublicLists.OpenVerified(spec.HostListExcludeFile,token,out verifiedExclusions));
                }
                token.ThrowIfCancellationRequested();
                if(Interlocked.CompareExchange(ref disposed,0,0)!=0)throw new ObjectDisposedException("Engine");
                int? externalDomains=list==null?(int?)null:verifiedDomains;
                var summary=ProfileSummary.FromArguments(args,externalDomains,true,KnownEngineSha256());
                var result=new PreparedStart(this,epoch,args,guards,list,token,summary);guards=null;list=null;return result;
            }
            finally{if(list!=null)list.Dispose();if(guards!=null)foreach(var stream in guards)stream.Dispose();}
        }
        public void Start(ProfileSpec spec)
        {
            using(var prepared=PrepareStartAsync(spec,CancellationToken.None).GetAwaiter().GetResult())StartPrepared(prepared);
        }
        public void StartPrepared(PreparedStart prepared)
        {
            if(prepared==null)throw new ArgumentNullException("prepared");
            lock(lifecycle)
            {
                List<FileStream> guards;FileStream list;prepared.Claim(this,out guards,out list);
                try
                {
                    prepared.Token.ThrowIfCancellationRequested();
                    if(Interlocked.CompareExchange(ref disposed,0,0)!=0)throw new ObjectDisposedException("Engine");
                    if(prepared.Epoch!=Interlocked.Read(ref preparationEpoch))
                        throw new InvalidOperationException("Подготовка устарела после остановки. Повторите запуск.");
                    ObserveExit();if(running)throw new InvalidOperationException("Сначала остановите текущий режим.");
                    if(!IsAdmin())throw new InvalidOperationException("Для сетевого компонента нужны права администратора.");
                    foreach(string name in new[]{"winws","winws2","goodbyedpi"})
                    {
                        var found=Process.GetProcessesByName(name);
                        try{if(found.Length>0)throw new InvalidOperationException("Уже запущен "+name+". Не запускаем второй фильтр. Остановите другой обходчик вручную.");}
                        finally{foreach(var p in found)p.Dispose();}
                    }
                    prepared.Token.ThrowIfCancellationRequested();
                    if(prepared.Epoch!=Interlocked.Read(ref preparationEpoch))
                        throw new InvalidOperationException("Подготовка устарела после остановки. Повторите запуск.");
                    // Summary and launch use the SAME immutable preflight snapshot,
                    // not current controls, persisted settings or a reconstructed spec.
                    var actualArguments=prepared.CopyArguments();
                    LaunchOwnedProcess(Path.Combine(root,"engine","winws2.exe"),actualArguments,Path.Combine(root,"engine"));
                    engineFileGuards=guards;hostListGuard=list;guards=null;list=null;
                    profileIdentity=Guid.NewGuid().ToString("N");
                    profileProcessId=child.Id;
                    try{profileStartedUtcTicks=child.StartTime.ToUniversalTime().Ticks;}
                    catch(InvalidOperationException){profileStartedUtcTicks=0;}
                    catch(System.ComponentModel.Win32Exception){profileStartedUtcTicks=0;}
                    string receipt=profileStartedUtcTicks==0?"unavailable":ProfileReceiptStore.WriteStart(
                        prepared.Summary,profileProcessId,profileStartedUtcTicks,profileIdentity);
                    lastProfileEvidence=new ProfileLaunchEvidence(prepared.Summary,"running",receipt);
                    ObserveExit();
                }
                finally{if(list!=null)list.Dispose();if(guards!=null)foreach(var stream in guards)stream.Dispose();prepared.Dispose();}
            }
        }
        private static string KnownEngineSha256()
        {
            using(var stream=typeof(Engine).Assembly.GetManifestResourceStream("LocalLink.EngineManifest"))
            using(var reader=new StreamReader(stream))
            {
                var manifest=new JavaScriptSerializer().Deserialize<EngineManifest>(reader.ReadToEnd());
                foreach(var file in manifest.files)
                    if(file.path=="vendor/engine/winws2.exe"&&ProfileSummary.IsSha256(file.sha256))return file.sha256;
            }
            throw new IOException("Нет закреплённой контрольной суммы движка.");
        }
        // Separated so offline lifetime tests can use a benign test child,
        // never a WinDivert/anti-DPI process.
        private void LaunchOwnedProcess(string exe, List<string> args, string cwd)
        {
            lock(lifecycle){LaunchOwnedProcessCore(exe,args,cwd);}
        }
        private void LaunchOwnedProcessCore(string exe,List<string> args,string cwd)
        {
            StopCore();Interlocked.Increment(ref preparationEpoch);unexpectedExit=null;exitObserved=false;
            var cmd = new StringBuilder(Quote(exe));
            foreach (string a in args) cmd.Append(' ').Append(Quote(a));
            job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags = 0x2000; // KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(limits)))
            { StopCore(); throw new System.ComponentModel.Win32Exception(); }
            var si = new STARTUPINFO(); si.cb = Marshal.SizeOf(si);
            PROCESS_INFORMATION pi;
            // Suspended start guarantees there is no orphan filter before job ownership.
            if (!CreateProcess(exe, cmd, IntPtr.Zero, IntPtr.Zero, false, 0x08000004, IntPtr.Zero, cwd, ref si, out pi))
            { int err = Marshal.GetLastWin32Error(); StopCore(); throw new System.ComponentModel.Win32Exception(err); }
            try
            {
                if (!AssignProcessToJobObject(job, pi.hProcess))
                {
                    int err = Marshal.GetLastWin32Error(); TerminateProcess(pi.hProcess, 1);
                    throw new System.ComponentModel.Win32Exception(err);
                }
                child = Process.GetProcessById((int)pi.dwProcessId);
                // Hold an actual process handle before resuming: never act on a reused PID.
                IntPtr heldProcessHandle = child.Handle;
                if (ResumeThread(pi.hThread) == UInt32.MaxValue) throw new System.ComponentModel.Win32Exception();
                running=true;
            }
            catch { StopCore(); throw; }
            finally { CloseHandle(pi.hThread); CloseHandle(pi.hProcess); }
        }
        public void Stop()
        {
            // Invalidates queued preflight immediately, even with no child.
            Interlocked.Increment(ref preparationEpoch);
            lock(lifecycle){StopCore();}
        }
        private void StopCore()
        {
            exitObserved=true; // intentional cleanup never manufactures an exit event
            bool handleClosed = true;
            if (job != IntPtr.Zero)
            {
                TerminateJobObject(job, 0);
                handleClosed = CloseHandle(job);
                if (handleClosed) job = IntPtr.Zero;
            }
            if (child != null)
            {
                // Only our own child; never terminate another VPN or filter.
                if (!child.HasExited && !child.WaitForExit(2000))
                {
                    child.Kill();
                    if (!child.WaitForExit(2000))
                        throw new IOException("Остановка движка пока не подтверждена. Не закрывайте окно; повторите остановку.");
                }
                child.Dispose(); child = null;
            }
            if (hostListGuard != null) { hostListGuard.Dispose(); hostListGuard = null; }
            if(engineFileGuards!=null){foreach(var stream in engineFileGuards)stream.Dispose();engineFileGuards=null;}
            running=false;
            if(lastProfileEvidence!=null&&lastProfileEvidence.Status!="stopped")
            {
                string receipt=profileStartedUtcTicks==0?"unavailable":ProfileReceiptStore.WriteStop(
                    lastProfileEvidence.Profile,profileProcessId,profileStartedUtcTicks,profileIdentity);
                lastProfileEvidence=new ProfileLaunchEvidence(lastProfileEvidence.Profile,"stopped",receipt);
            }
            if (!handleClosed) throw new IOException("Процесс остановлен, но закрытие ресурса Windows не подтверждено. Повторите остановку.");
        }
        public void Dispose() { Interlocked.Exchange(ref disposed,1);Stop(); }
        public sealed class EngineManifest { public EngineFile[] files; }
        public sealed class EngineFile { public string path; public string sha256; }
        [StructLayout(LayoutKind.Sequential)] struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)] struct IO_COUNTERS
        { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
        [StructLayout(LayoutKind.Sequential)] struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        { public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct STARTUPINFO
        { public int cb; public string lpReserved, lpDesktop, lpTitle; public uint dwX,dwY,dwXSize,dwYSize,dwXCountChars,dwYCountChars,dwFillAttribute,dwFlags; public short wShowWindow,cbReserved2; public IntPtr lpReserved2,hStdInput,hStdOutput,hStdError; }
        [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess,hThread; public uint dwProcessId,dwThreadId; }
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr CreateJobObject(IntPtr attrs, string name);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetInformationJobObject(IntPtr job, int type, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint size);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool CreateProcess(string app, StringBuilder args, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
        [DllImport("kernel32.dll", SetLastError=true)] static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool TerminateJobObject(IntPtr job, uint code);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    }
}
