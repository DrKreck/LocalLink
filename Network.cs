using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LocalLink
{
    public sealed class VpnConnection
    {
        public string ProcessName;
        public string Address;
        public int Port;
        public int ProcessId;

        public override string ToString()
        {
            string endpoint = Address ?? String.Empty;
            if (endpoint.IndexOf(':') >= 0)
                endpoint = "[" + endpoint + "]";
            return String.Format(CultureInfo.InvariantCulture, "{0} (PID {1}) - {2}:{3}",
                ProcessName, ProcessId, endpoint, Port);
        }
    }

    public sealed class ProbeResult
    {
        public string Host;
        public bool TlsReached;
        public bool HttpOk;
        public int StatusCode;
        public long Milliseconds;
        public string Error;
    }

    // Read-only observations. A process name, adapter label, or TCP endpoint is
    // only a hint: none proves that this application is using a VPN tunnel.
    public static class NetworkInfo
    {
        private const int ProbeTimeoutMilliseconds = 8000;
        private const int MaxNativeTableBytes = 16 * 1024 * 1024;
        private const int MaxCandidateConnections = 512;
        private const uint ErrorInsufficientBuffer = 122;
        private const int AfInet = 2;
        private const int AfInet6 = 23;
        private const int TcpTableOwnerPidAll = 5;
        private const int TcpStateSynSent = 3;
        private const int TcpStateEstablished = 5;

        private static readonly HashSet<string> KnownVpnNames = new HashSet<string>(
            new string[]
            {
                "thronecore", "throne", "sing-box", "sing_box", "singbox",
                "xray", "v2ray", "v2rayn", "v2rayng", "openvpn", "openvpn-gui",
                "openvpnserv", "wireguard", "wireguard-go", "happ", "nekoray",
                "nekobox", "nekobox_core", "mihomo", "hiddify", "hiddifynext",
                "hiddify-next", "hiddify-core", "hysteria", "hysteria2",
                "trojan", "trojan-go", "outline-client", "outline-service"
            }, StringComparer.OrdinalIgnoreCase);

        private static readonly Regex HostPattern = new Regex(
            @"\A[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)*\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex TunnelLabelPattern = new Regex(
            @"(^|[\s_\-])(?:tun|tap|utun)(?:[0-9]+|[\s_\-]|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        [DllImport("iphlpapi.dll", SetLastError = false)]
        private static extern uint GetExtendedTcpTable(
            IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
            int addressFamily, int tableClass, uint reserved);

        public static List<VpnConnection> FindVpnConnections()
        {
            List<VpnConnection> result = new List<VpnConnection>();
            Dictionary<int, string> processes = GetKnownVpnProcesses();
            if (processes.Count == 0)
                return result;

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            ReadTcpConnections(AfInet, processes, result, seen);
            ReadTcpConnections(AfInet6, processes, result, seen);
            result.Sort(delegate(VpnConnection left, VpnConnection right)
            {
                int comparison = StringComparer.OrdinalIgnoreCase.Compare(left.ProcessName, right.ProcessName);
                if (comparison == 0)
                    comparison = StringComparer.Ordinal.Compare(left.Address, right.Address);
                if (comparison == 0)
                    comparison = left.Port.CompareTo(right.Port);
                if (comparison == 0)
                    comparison = left.ProcessId.CompareTo(right.ProcessId);
                return comparison;
            });
            return result;
        }

        public static string Summary()
        {
            // Do not expose adapter names, descriptions, IP addresses or MACs.
            SortedDictionary<string, int> adapterTypes = new SortedDictionary<string, int>(StringComparer.Ordinal);
            bool adaptersAvailable = true;
            try
            {
                foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up ||
                        adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;
                    string type = adapter.NetworkInterfaceType.ToString();
                    int count;
                    adapterTypes.TryGetValue(type, out count);
                    adapterTypes[type] = count + 1;
                }
            }
            catch (NetworkInformationException) { adaptersAvailable = false; }
            catch (System.Security.SecurityException) { adaptersAvailable = false; }
            catch (PlatformNotSupportedException) { adaptersAvailable = false; }

            StringBuilder text = new StringBuilder("Active adapter types: ");
            if (!adaptersAvailable)
                text.Append("unavailable");
            else if (adapterTypes.Count == 0)
                text.Append("none detected");
            else
            {
                bool first = true;
                foreach (KeyValuePair<string, int> type in adapterTypes)
                {
                    if (!first) text.Append(", ");
                    text.Append(type.Key).Append(" (").Append(type.Value).Append(")");
                    first = false;
                }
            }

            SortedSet<string> clients = new SortedSet<string>(GetKnownVpnProcesses().Values, StringComparer.OrdinalIgnoreCase);
            text.Append(". Known VPN process candidates: ");
            if (clients.Count == 0)
                text.Append("none detected");
            else
                text.Append(String.Join(", ", clients));
            text.Append(". These are hints, not proof of an active VPN route.");
            return text.ToString();
        }

        public static bool HasLikelyVpn()
        {
            if (GetKnownVpnProcesses().Count == 0)
                return false;
            try
            {
                foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up)
                        continue;
                    if (adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                        return true;
                    string label = (adapter.Name ?? String.Empty) + " " + (adapter.Description ?? String.Empty);
                    if (TunnelLabelPattern.IsMatch(label) || HasVpnAdapterMarker(label))
                        return true;
                }
            }
            catch (NetworkInformationException) { }
            catch (System.Security.SecurityException) { }
            catch (PlatformNotSupportedException) { }
            return false;
        }

        public static async Task<ProbeResult> ProbeAsync(string host, CancellationToken token)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            ProbeResult result = new ProbeResult { Host = host ?? String.Empty, Error = String.Empty };
            try
            {
                string normalized;
                if (!TryNormalizeHost(host, out normalized))
                {
                    result.Error = "Enter a DNS hostname only, without a URL, port, path or IP address.";
                    return result;
                }
                result.Host = normalized;
                token.ThrowIfCancellationRequested();

                // Proxy = null / UseProxy = true preserve the .NET system proxy.
                // We never change routes, proxy settings, TLS policy, certificate
                // validation callbacks, or ServicePointManager global settings.
                using (HttpClientHandler handler = new HttpClientHandler())
                {
                    handler.AllowAutoRedirect = false;
                    handler.UseCookies = false;
                    handler.UseProxy = true;
                    using (HttpClient client = new HttpClient(handler))
                    using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token))
                    using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get,
                        new Uri("https://" + normalized + "/", UriKind.Absolute)))
                    {
                        client.Timeout = TimeSpan.FromMilliseconds(ProbeTimeoutMilliseconds);
                        Task<HttpResponseMessage> pending = client.SendAsync(request,
                            HttpCompletionOption.ResponseHeadersRead, linked.Token);
                        // An additional race bounds the caller even if a platform
                        // DNS operation is slow to honor HttpClient cancellation.
                        Task deadline = Task.Delay(ProbeTimeoutMilliseconds, linked.Token);
                        if (await Task.WhenAny(pending, deadline).ConfigureAwait(false) != pending)
                        {
                            linked.Cancel();
                            DisposeResponseWhenFinished(pending);
                            result.Error = token.IsCancellationRequested ? "Cancelled." : "Request timed out.";
                            return result;
                        }
                        using (HttpResponseMessage response = await pending.ConfigureAwait(false))
                        {
                            // Headers from HTTPS establish TLS reachability with
                            // normal platform certificate validation. Never read
                            // or retain the response body or follow Location.
                            result.TlsReached = true;
                            result.StatusCode = (int)response.StatusCode;
                            result.HttpOk = result.StatusCode >= 200 && result.StatusCode < 400;
                            if (!result.HttpOk)
                                result.Error = "HTTP status " + result.StatusCode.ToString(CultureInfo.InvariantCulture) + ".";
                        }
                        linked.Cancel();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                result.Error = token.IsCancellationRequested ? "Cancelled." : "Request timed out.";
            }
            catch (HttpRequestException failure)
            {
                result.Error = ClassifyFailure(failure);
            }
            catch (System.Security.SecurityException)
            {
                result.Error = "Network access was denied by the system.";
            }
            finally
            {
                elapsed.Stop();
                result.Milliseconds = elapsed.ElapsedMilliseconds;
            }
            return result;
        }

        public static string ClassifyFailure(Exception failure)
        {
            // Do not expose raw messages, URLs or proxy credentials in logs.
            Exception current=failure;
            for(int i=0;i<8&&current!=null;i++,current=current.InnerException)
            {
                var web=current as WebException;
                if(web==null)continue;
                switch(web.Status)
                {
                    case WebExceptionStatus.NameResolutionFailure:return "DNS: имя сервиса не разрешилось.";
                    case WebExceptionStatus.ProxyNameResolutionFailure:return "DNS: имя системного прокси не разрешилось.";
                    case WebExceptionStatus.TrustFailure:return "TLS: сертификат не прошёл проверку.";
                    case WebExceptionStatus.SecureChannelFailure:return "TLS: не удалось установить защищённое соединение.";
                    case WebExceptionStatus.Timeout:return "Истекло время ожидания.";
                    case WebExceptionStatus.ConnectFailure:return "Не удалось установить соединение (сеть, прокси или удалённый узел).";
                    case WebExceptionStatus.ConnectionClosed:return "Соединение закрыто до ответа.";
                }
            }
            return "Нет HTTPS-ответа: причина не определена. Это не доказательство блокировки.";
        }

        private static bool TryNormalizeHost(string host, out string normalized)
        {
            normalized = String.Empty;
            if (String.IsNullOrWhiteSpace(host) || host.Length > 1024)
                return false;
            try
            {
                IdnMapping idn = new IdnMapping { UseStd3AsciiRules = true };
                string ascii = idn.GetAscii(host.Trim()).ToLowerInvariant();
                if (ascii.EndsWith(".", StringComparison.Ordinal))
                    ascii = ascii.Substring(0, ascii.Length - 1);
                if (ascii.Length == 0 || ascii.Length > 253 ||
                    !HostPattern.IsMatch(ascii) || Uri.CheckHostName(ascii) != UriHostNameType.Dns)
                    return false;
                normalized = ascii;
                return true;
            }
            catch (ArgumentException) { return false; }
        }

        private static void DisposeResponseWhenFinished(Task<HttpResponseMessage> pending)
        {
            // Observe a cancelled/failed task and dispose a late response without
            // keeping its content or letting an abandoned exception escape.
            pending.ContinueWith(delegate(Task<HttpResponseMessage> finished)
            {
                if (finished.Status == TaskStatus.RanToCompletion)
                    finished.Result.Dispose();
                else if (finished.IsFaulted)
                {
                    AggregateException observed = finished.Exception;
                    GC.KeepAlive(observed);
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private static Dictionary<int, string> GetKnownVpnProcesses()
        {
            Dictionary<int, string> known = new Dictionary<int, string>();
            Process[] processes;
            try { processes = Process.GetProcesses(); }
            catch (System.ComponentModel.Win32Exception) { return known; }
            catch (InvalidOperationException) { return known; }
            catch (System.Security.SecurityException) { return known; }
            foreach (Process process in processes)
            {
                using (process)
                {
                    try
                    {
                        string name = process.ProcessName;
                        if (KnownVpnNames.Contains(name) || name.StartsWith("clash", StringComparison.OrdinalIgnoreCase))
                            known[process.Id] = name;
                    }
                    catch (System.ComponentModel.Win32Exception) { }
                    catch (InvalidOperationException) { }
                    catch (System.Security.SecurityException) { }
                }
            }
            return known;
        }

        private static bool HasVpnAdapterMarker(string label)
        {
            string[] markers = { "wintun", "wireguard", "openvpn", "clash", "mihomo",
                "sing-box", "singbox", "nekoray", "nekobox", "hiddify", "throne", "happ" };
            foreach (string marker in markers)
            {
                if (label.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static void ReadTcpConnections(int family, Dictionary<int, string> processes,
            List<VpnConnection> result, HashSet<string> seen)
        {
            IntPtr buffer = IntPtr.Zero;
            try
            {
                int size = 0;
                uint error = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidAll, 0);
                if (error != 0 && error != ErrorInsufficientBuffer)
                    return;
                // A live table can grow between calls. Retry at most three times,
                // and never allocate an unbounded amount based on native data.
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (size < 4 || size > MaxNativeTableBytes)
                        return;
                    int allocated = size;
                    buffer = Marshal.AllocHGlobal(allocated);
                    error = GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidAll, 0);
                    if (error == ErrorInsufficientBuffer)
                    {
                        Marshal.FreeHGlobal(buffer);
                        buffer = IntPtr.Zero;
                        continue;
                    }
                    if (error != 0)
                        return;

                    int rowSize = family == AfInet ? 24 : 56;
                    int count = Marshal.ReadInt32(buffer);
                    if (count < 0 || count > (allocated - 4) / rowSize)
                        return;
                    for (int i = 0; i < count && result.Count < MaxCandidateConnections; i++)
                    {
                        IntPtr row = IntPtr.Add(buffer, 4 + i * rowSize);
                        int state = Marshal.ReadInt32(row, family == AfInet ? 0 : 48);
                        if (state != TcpStateEstablished && state != TcpStateSynSent)
                            continue;
                        int pid = Marshal.ReadInt32(row, family == AfInet ? 20 : 52);
                        string processName;
                        if (!processes.TryGetValue(pid, out processName))
                            continue;
                        int networkPort = Marshal.ReadInt32(row, family == AfInet ? 16 : 44);
                        int port = ((networkPort & 255) << 8) | ((networkPort >> 8) & 255);
                        if (port == 0)
                            continue;
                        byte[] addressBytes = new byte[family == AfInet ? 4 : 16];
                        Marshal.Copy(IntPtr.Add(row, family == AfInet ? 12 : 24), addressBytes, 0, addressBytes.Length);
                        IPAddress address = family == AfInet ? new IPAddress(addressBytes) :
                            new IPAddress(addressBytes, (long)(uint)Marshal.ReadInt32(row, 40));
                        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
                            continue;
                        string addressText = address.ToString();
                        string key = pid.ToString(CultureInfo.InvariantCulture) + "|" + addressText + "|" + port.ToString(CultureInfo.InvariantCulture);
                        if (seen.Add(key))
                            result.Add(new VpnConnection { ProcessName = processName, ProcessId = pid,
                                Address = addressText, Port = port });
                    }
                    return;
                }
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            catch (System.Security.SecurityException) { }
            finally
            {
                if (buffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
