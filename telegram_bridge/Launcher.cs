// Local Telegram transport controller.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal sealed class TelegramBridgeWindow : Form
{
    private readonly Label state = new Label(), details = new Label();
    private readonly Button connect = new Button(), stop = new Button(), restart = new Button();
    private readonly System.Windows.Forms.Timer networkTimer = new System.Windows.Forms.Timer { Interval = 8000 };
    private readonly NotifyIcon tray = new NotifyIcon();
    private Process child;
    private IntPtr job;
    private byte[] secret;
    private bool ready, quitting, cleaned;
    private bool checkingNetwork, lastFailure;
    private readonly bool automaticSetup;
    private bool startupVisibilityHandled, startupStarted, automaticOfferAttempted, automaticFailureShown;
    private int generation, connectedCount, restartAttempts;
    private DateTime restartWindow = DateTime.UtcNow;
    private DateTime childStartedUtc;
    private Tuple<int, IPAddress> currentBinding;
    private const int Port = 1443;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LocalLink.TelegramBridge.preview.v1");

    [STAThread]
    public static int Main(string[] args)
    {
        bool automatic, selfTest;
        if (!TryParseArguments(args, out automatic, out selfTest)) return 2;
        if (selfTest)
        {
            byte[] test = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(test);
            byte[] wrapped = ProtectedData.Protect(test, Entropy, DataProtectionScope.CurrentUser);
            byte[] back = ProtectedData.Unprotect(wrapped, Entropy, DataProtectionScope.CurrentUser);
            var one = Tuple.Create(9, IPAddress.Parse("192.0.2.9"));
            var two = Tuple.Create(10, IPAddress.Parse("192.0.2.10"));
            return test.SequenceEqual(back) && Hex(test).Length == 32 &&
                ChooseBinding(new List<Tuple<int,IPAddress>> { one }, null) == one &&
                ChooseBinding(new List<Tuple<int,IPAddress>> { one,two }, one) == one &&
                ChooseBinding(new List<Tuple<int,IPAddress>> { one,two }, null) == null &&
                ChooseBinding(new List<Tuple<int,IPAddress>>(), one) == null ? 0 : 1;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        bool first;
        using (var instance = new Mutex(true, @"Local\LocalLink.TelegramBridge.preview.v1", out first))
        {
            if (!first)
            {
                if (!automatic) MessageBox.Show("Модуль Telegram уже запущен. Откройте его значок в трее.", "LocalLink — Telegram");
                return 0;
            }
            try { Application.Run(new TelegramBridgeWindow(automatic)); }
            finally { instance.ReleaseMutex(); }
        }
        return 0;
    }

    internal static bool TryParseArguments(string[] args, out bool automatic, out bool selfTest)
    {
        automatic = selfTest = false;
        if (args == null) return false;
        if (args.Length == 0) return true;
        if (args.Length != 1) return false;
        if (args[0] == "--auto-setup") { automatic = true; return true; }
        if (args[0] == "--self-test") { selfTest = true; return true; }
        return false;
    }

    internal TelegramBridgeWindow() : this(false) { }
    internal TelegramBridgeWindow(bool autoSetup)
    {
        automaticSetup = autoSetup;
        ShowInTaskbar = !automaticSetup;
        Text = "LocalLink — Telegram";
        Size = new Size(590, 340); MinimumSize = Size; MaximumSize = Size;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10F);
        BackColor = SystemColors.Window; ForeColor = SystemColors.WindowText;
        var title = new Label { Text = "Telegram · локальный WebSocket-мост", AutoSize = true,
            Font = new Font("Segoe UI", 15F, FontStyle.Bold), Location = new Point(22, 20) };
        state.Text = "Запуск…"; state.SetBounds(24, 70, 530, 30);
        details.Text = "Сообщения и файлы Telegram через локальный прокси.";
        details.SetBounds(24, 107, 530, 86);
        connect.Text = "Подключить Telegram"; connect.SetBounds(24, 209, 210, 37); connect.Enabled = false;
        restart.Text = "Перезапустить"; restart.SetBounds(246, 209, 145, 37);
        stop.Text = "Остановить"; stop.SetBounds(403, 209, 138, 37);
        var hint = new Label { Text = "Закрытие окна сворачивает в трей. Откат: Telegram → Прокси → выключить.",
            Location = new Point(24, 261), Size = new Size(535, 34), Font = new Font("Segoe UI", 9F) };
        Controls.AddRange(new Control[] { title, state, details, connect, restart, stop, hint });
        connect.Click += delegate { OpenTelegram(); };
        stop.Click += delegate { Quit(); };
        restart.Click += async delegate { restartAttempts = 0; restartWindow = DateTime.UtcNow; await RefreshNetwork(true); };
        tray.Icon = SystemIcons.Application; tray.Text = "LocalLink — Telegram";
        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть", null, delegate { Show(); WindowState = FormWindowState.Normal; Activate(); });
        menu.Items.Add("Остановить и выйти", null, delegate { Quit(); });
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += delegate { Show(); WindowState = FormWindowState.Normal; Activate(); };
        tray.Visible = true;
        Shown += delegate { BeginController(); };
        networkTimer.Tick += async delegate { if (!cleaned) await RefreshNetwork(false); };
        FormClosing += delegate(object sender, FormClosingEventArgs e) {
            if (!quitting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
            else Cleanup();
        };
    }

    protected override void SetVisibleCore(bool value)
    {
        if (automaticSetup && !startupVisibilityHandled && value)
        {
            startupVisibilityHandled = true;
            if (!IsHandleCreated) CreateHandle();
            BeginInvoke((Action)BeginController);
            base.SetVisibleCore(false);
            return;
        }
        base.SetVisibleCore(value);
    }
    private void BeginController()
    {
        if (startupStarted || cleaned) return;
        startupStarted = true;
        StartBridge();
        if (!cleaned) networkTimer.Start();
    }
    private void RevealAutomaticFailure()
    {
        if (!automaticSetup || automaticFailureShown || IsDisposed) return;
        automaticFailureShown = true;
        ShowInTaskbar = true;
        Show(); WindowState = FormWindowState.Normal;
    }

    private void StartBridge()
    {
        if (cleaned) return;
        try
        {
            string root = AppDomain.CurrentDomain.BaseDirectory;
            string runtime = Path.Combine(root, @"runtime\python.exe");
            string script = Path.Combine(root, "bridge.py");
            if (!File.Exists(runtime) || !File.Exists(script))
            {
                state.Text = "Нужно восстановить компоненты.";
                details.Text = "Откройте LocalLink и снова нажмите «Открыть модуль Telegram».";
                restart.Enabled = false;
                RevealAutomaticFailure();
                return;
            }
            string keyFile = Path.Combine(root, "proxy-secret.dpapi");
            if (File.Exists(keyFile))
            {
                if (new FileInfo(keyFile).Length > 4096) throw new InvalidOperationException();
                secret = ProtectedData.Unprotect(File.ReadAllBytes(keyFile), Entropy, DataProtectionScope.CurrentUser);
                if (secret.Length != 16) throw new InvalidOperationException();
            }
            else
            {
                secret = new byte[16];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(secret);
                byte[] data = ProtectedData.Protect(secret, Entropy, DataProtectionScope.CurrentUser);
                using (var file = new FileStream(keyFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    file.Write(data, 0, data.Length);
                }
            }
            var binding = SelectBinding(null);
            if (binding == null) { state.Text = "Нужен один активный физический IPv4-адаптер."; RevealAutomaticFailure(); return; }
            StartChild(binding);
        }
        catch { quitting = true; Cleanup(); state.Text = "Не удалось запустить модуль. Проверьте комплект runtime."; RevealAutomaticFailure(); }
    }

    private static Tuple<int,IPAddress> ChooseBinding(List<Tuple<int,IPAddress>> bindings, Tuple<int,IPAddress> previous)
    {
        if (previous != null)
            foreach (var item in bindings)
                if (item.Item1 == previous.Item1 && item.Item2.Equals(previous.Item2)) return item;
        return bindings.Count == 1 ? bindings[0] : null;
    }

    private static Tuple<int,IPAddress> SelectBinding(Tuple<int,IPAddress> previous)
    {
            var physical = new HashSet<int>();
            // Win32_NetworkAdapter.PhysicalAdapter misclassifies some Wintun/VPN
            // devices on modern Windows. MSFT_NetAdapter is the same hardware
            // inventory used by Get-NetAdapter -Physical. Fail closed if absent.
            using (var query = new ManagementObjectSearcher(@"root\StandardCimv2",
                "SELECT InterfaceIndex FROM MSFT_NetAdapter WHERE HardwareInterface = True AND InterfaceOperationalStatus = 1"))
            using (var rows = query.Get())
                foreach (ManagementObject row in rows) using (row)
                    if (row["InterfaceIndex"] != null) physical.Add(Convert.ToInt32(row["InterfaceIndex"]));
            var bindings = new List<Tuple<int, IPAddress>>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || !nic.Supports(NetworkInterfaceComponent.IPv4)) continue;
                var props = nic.GetIPProperties(); int index = props.GetIPv4Properties().Index;
                if (!physical.Contains(index)) continue;
                foreach (var local in props.UnicastAddresses)
                    if (local.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(local.Address) &&
                        !local.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal) &&
                        local.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred)
                        bindings.Add(Tuple.Create(index, local.Address));
            }
            return ChooseBinding(bindings, previous);
    }

    private void StartChild(Tuple<int,IPAddress> binding)
    {
        if (cleaned) return;
        string root = AppDomain.CurrentDomain.BaseDirectory;
        string runtime = Path.Combine(root, @"runtime\python.exe"), script = Path.Combine(root, "bridge.py");
        ready = false; connect.Enabled = false; connectedCount = 0; lastFailure = false;
        int ownGeneration = ++generation;
        if (DateTime.UtcNow - restartWindow > TimeSpan.FromMinutes(1)) { restartAttempts = 0; restartWindow = DateTime.UtcNow; }
        restartAttempts++;
        try
        {
            var start = new ProcessStartInfo(runtime, "-I -B -u \"" + script + "\" --secret-stdin --port " + Port +
                " --ifindex " + binding.Item1 + " --source " + binding.Item2.ToString()) {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw new InvalidOperationException();
            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION(); limits.BasicLimitInformation.LimitFlags = 0x2000;
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(limits))) throw new InvalidOperationException();
            Process ownedChild = new Process { StartInfo = start, EnableRaisingEvents = true };
            child = ownedChild;
            ownedChild.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { OnStatus(ownedChild, ownGeneration, e.Data); };
            ownedChild.ErrorDataReceived += delegate { }; // Never retain exception text or raw diagnostics.
            ownedChild.Exited += delegate { Ui(delegate {
                if (cleaned || ownGeneration != generation || child != ownedChild) return;
                ready = false; connectedCount = 0; connect.Enabled = false;
                state.Text = "Модуль остановлен. Проверяем восстановление…";
            }); };
            if (!child.Start()) throw new InvalidOperationException();
            if (!AssignProcessToJobObject(job, child.Handle)) { child.Kill(); throw new InvalidOperationException(); }
            child.BeginOutputReadLine(); child.BeginErrorReadLine();
            child.StandardInput.WriteLine(Hex(secret)); child.StandardInput.Close();
            currentBinding = binding; childStartedUtc = DateTime.UtcNow;
        }
        catch { StopChild(); throw; }
    }

    private async Task RefreshNetwork(bool force)
    {
        if (cleaned || checkingNetwork || secret == null) return;
        checkingNetwork = true; restart.Enabled = false;
        try
        {
            var previous = currentBinding;
            var next = await Task.Run(delegate { return SelectBinding(previous); });
            if (cleaned) return;
            if (next == null)
            {
                state.Text = "Ждём однозначного физического IPv4-подключения.";
                return; // Do not select a VPN or guess between new physical adapters.
            }
            bool changed = previous == null || previous.Item1 != next.Item1 || !previous.Item2.Equals(next.Item2);
            if (!force && !changed && child != null && !child.HasExited)
            {
                if (!ready && DateTime.UtcNow - childStartedUtc > TimeSpan.FromSeconds(20))
                { StopChild(); state.Text = "Модуль не подтвердил готовность. Повторяем запуск…"; RevealAutomaticFailure(); }
                return;
            }
            if (!force && !changed && restartAttempts >= 5 && DateTime.UtcNow - restartWindow < TimeSpan.FromMinutes(1))
            { state.Text = "Повторные запуски не помогли. Нажмите «Перезапустить»."; RevealAutomaticFailure(); return; }
            state.Text = changed ? "Сеть изменилась. Восстанавливаем мост…" : "Перезапуск моста…";
            StopChild();
            if (!cleaned) StartChild(next);
        }
        catch { if (!cleaned) { state.Text = "Восстановление пока не удалось. Можно повторить вручную."; RevealAutomaticFailure(); } }
        finally { checkingNetwork = false; if (!cleaned) restart.Enabled = true; }
    }

    private void StopChild()
    {
        generation++; ready = false; connectedCount = 0; connect.Enabled = false;
        Process previous = child; child = null;
        if (job != IntPtr.Zero) { CloseHandle(job); job = IntPtr.Zero; }
        if (previous != null)
        {
            try { if (!previous.HasExited && !previous.WaitForExit(3000)) throw new InvalidOperationException(); }
            finally { previous.Dispose(); }
        }
    }

    private void OnStatus(Process owner, int ownerGeneration, string line)
    {
        if (String.IsNullOrEmpty(line) || line.Length > 1024) return;
        try
        {
            var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(line);
            object value;
            if (!data.TryGetValue("event", out value)) return;
            string code = value as string;
            Ui(delegate {
                if (cleaned || ownerGeneration != generation || child != owner || owner.HasExited) return;
                object count;
                if (data.TryGetValue("connected", out count) && count is int && (int)count >= 0 && (int)count <= 8) connectedCount = (int)count;
                if (code == "listening") { ready = true; connect.Enabled = true; state.Text = "Готов · 127.0.0.1:" + Port; OfferAutomaticProxy(); }
                else if (code == "upstream_connected" || code == "connection_closed")
                {
                    if (code == "upstream_connected") lastFailure = false;
                    state.Text = connectedCount > 0 ? "WSS-подключений: " + connectedCount + ". Проверьте Telegram." :
                        lastFailure ? "Пока нет ответа от Telegram. Клиент может повторить попытку." : "Готов · ждём подключения Telegram.";
                }
                else if (code == "client_rejected") state.Text = "Входящее подключение отклонено. Проверьте настройки прокси.";
                else if (code == "connection_failed") { lastFailure = true; state.Text = connectedCount > 0 ?
                    "WSS-подключений: " + connectedCount + "; одна попытка не удалась." : "Пока нет ответа от Telegram. Клиент может повторить попытку."; }
                else if (code == "fatal") { ready = false; connect.Enabled = false; state.Text = "Модуль не запущен: порт занят или параметры недоступны."; RevealAutomaticFailure(); }
            });
        }
        catch { }
    }

    private void OfferAutomaticProxy()
    {
        if (!automaticSetup || automaticOfferAttempted || cleaned || !ready || secret == null) return;
        automaticOfferAttempted = true;
        try
        {
            bool offered = TryOfferAutomaticProxy(AppDomain.CurrentDomain.BaseDirectory, secret, delegate {
                Process.Start(new ProcessStartInfo(BuildProxyUri(secret)) { UseShellExecute = true });
            });
            if (offered) details.Text = "Подтвердите подключение прокси в окне Telegram. Подтверждение клиента LocalLink не читает.";
        }
        catch
        {
            state.Text = "Не удалось предложить настройку Telegram.";
            details.Text = "Нажмите «Подключить Telegram», чтобы повторить. Настройки аккаунта не изменялись.";
            RevealAutomaticFailure();
        }
    }

    internal static bool TryOfferAutomaticProxy(string folder, byte[] ownSecret, Action launch)
    {
        if (ownSecret == null || ownSecret.Length != 16) throw new ArgumentException("Нужен секрет собственного локального модуля.", "ownSecret");
        if (launch == null) throw new ArgumentNullException("launch");
        string marker = OfferMarkerPath(folder), fingerprint;
        using (var sha = SHA256.Create()) fingerprint = Hex(sha.ComputeHash(ownSecret));
        if (File.Exists(marker) && ReadOfferFingerprint(marker) == fingerprint) return false;
        // Mark the offer attempt before external UI launch. Even a crash or a
        // rejected Telegram confirmation cannot create repeated prompts on boot.
        // This is not evidence that Telegram accepted or enabled its proxy.
        byte[] bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
            { "Schema", 1 }, { "SecretSha256", fingerprint }, { "Offered", true } }));
        string temporary = marker + ".staged-" + Guid.NewGuid().ToString("N");
        SafeOfferPath(temporary);
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
        SafeOfferPath(marker);
        if (File.Exists(marker)) File.Replace(temporary, marker, marker + ".backup-" + Guid.NewGuid().ToString("N"));
        else File.Move(temporary, marker);
        launch(); return true;
    }
    private static string BuildProxyUri(byte[] ownSecret)
    {
        if (ownSecret == null || ownSecret.Length != 16) throw new ArgumentException("Недопустимый секрет локального модуля.");
        return "tg://proxy?server=127.0.0.1&port=" + Port + "&secret=" + Hex(ownSecret);
    }
    private static string OfferMarkerPath(string folder)
    {
        if (String.IsNullOrWhiteSpace(folder) || folder.Length < 3 || folder[1] != ':' || (folder[2] != '\\' && folder[2] != '/') || folder.IndexOf(':', 2) >= 0)
            throw new ArgumentException("Нужна локальная папка модуля.", "folder");
        string root = Path.GetFullPath(folder);
        SafeOfferPath(root);
        if (!Directory.Exists(root)) throw new IOException("Папка модуля отсутствует.");
        string marker = Path.Combine(root, "proxy-link-offered-v1.json"); SafeOfferPath(marker); return marker;
    }
    private static string ReadOfferFingerprint(string marker)
    {
        SafeOfferPath(marker); byte[] bytes;
        using (var stream = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length < 1 || stream.Length > 512) throw new IOException("Некорректная отметка настройки Telegram.");
            bytes = new byte[(int)stream.Length]; int offset = 0, read;
            while (offset < bytes.Length && (read = stream.Read(bytes, offset, bytes.Length - offset)) > 0) offset += read;
            if (offset != bytes.Length || stream.ReadByte() != -1) throw new IOException("Отметка настройки изменилась при чтении.");
        }
        var value = new JavaScriptSerializer { MaxJsonLength = 512, RecursionLimit = 4 }.DeserializeObject(new UTF8Encoding(false, true).GetString(bytes)) as Dictionary<string, object>;
        object schema, offered, hash;
        if (value == null || value.Count != 3 || !value.TryGetValue("Schema", out schema) || !(schema is int) || (int)schema != 1 ||
            !value.TryGetValue("Offered", out offered) || !(offered is bool) || !(bool)offered || !value.TryGetValue("SecretSha256", out hash))
            throw new IOException("Некорректная отметка настройки Telegram.");
        string fingerprint = hash as string;
        if (fingerprint == null || fingerprint.Length != 64 || fingerprint.Any(c => !(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')))
            throw new IOException("Некорректный отпечаток настройки Telegram.");
        return fingerprint;
    }
    private static void SafeOfferPath(string file)
    {
        for (string path = Path.GetFullPath(file); !String.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); } catch (FileNotFoundException) { continue; } catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Перенаправления папки модуля не поддерживаются.");
        }
    }

    private void OpenTelegram()
    {
        if (cleaned) return;
        try
        {
            if (cleaned || !ready || secret == null || child == null || child.HasExited) return;
            // Own loopback credential only; never read account/session keys.
            // Telegram presents its own confirmation before changing its proxy.
            string uri = BuildProxyUri(secret);
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch { state.Text = "Не удалось открыть Telegram. Настройка приложения не менялась."; }
    }
    private static string Hex(byte[] data) { return BitConverter.ToString(data).Replace("-", "").ToLowerInvariant(); }
    private void Ui(Action action) { if (!IsDisposed && IsHandleCreated) try { BeginInvoke(action); } catch (InvalidOperationException) { } }
    private void Quit()
    {
        if (MessageBox.Show(this, "Остановить модуль? Для обычного подключения выключите прокси в Telegram. VPN останется включён.",
            "LocalLink — Telegram", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        quitting = true; Close();
    }
    private void Cleanup()
    {
        if (cleaned) return;
        cleaned = true;
        networkTimer.Stop(); networkTimer.Dispose();
        ready = false; connect.Enabled = false;
        restart.Enabled = false;
        try { StopChild(); } catch { } // Closing the owned job is the final cleanup mechanism.
        if (secret != null) { Array.Clear(secret, 0, secret.Length); secret = null; }
        tray.Visible = false; if (tray.ContextMenuStrip != null) tray.ContextMenuStrip.Dispose(); tray.Dispose();
    }
    protected override void Dispose(bool disposing)
    { if (disposing) Cleanup(); base.Dispose(disposing); }
    [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_BASIC_LIMIT_INFORMATION {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int type, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
