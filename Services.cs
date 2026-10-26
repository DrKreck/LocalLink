using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LocalLink
{
    public sealed class ServiceEntry
    {
        public string Id, Name, Scope;
        public string[] Domains;
    }

    public static class ServiceCatalog
    {
        // A deliberately small, editable domain allowlist, not a complete list
        // of game servers or a promise that all application protocols are covered.
        // These suffixes include primary web/CDN domains; no IP-range guessing.
        public static List<ServiceEntry> All()
        {
            return new List<ServiceEntry>
            {
                Entry("discord", "Discord", "Сайт и Gateway. Для голоса — ограниченный профиль discovery/STUN и TLS; звук проверяется в канале.",
                    "discord.com", "discord.gg", "discordapp.com", "discordapp.net", "discord.media"),
                Entry("youtube", "YouTube", "Ответ сайта автоматически; воспроизведение видео вручную.",
                    "youtube.com", "youtu.be", "googlevideo.com", "ytimg.com", "youtubei.googleapis.com", "youtube-nocookie.com"),
                Entry("telegram", "Telegram", "Web и TCP до публичных DC. Отдельный профиль приложения; сообщения и звонки вручную.",
                    "telegram.org", "t.me", "tdesktop.com"),
                Entry("steam", "Steam", "Только сайты магазина и сообщества; клиент, загрузки и игры вручную.",
                    "steampowered.com", "steamcommunity.com", "steamstatic.com", "steamusercontent.com", "steamcontent.com"),
                Entry("arma", "Arma Reforger", "Только официальный сайт; список серверов и подключение к игре вручную.",
                    "reforger.armaplatform.com", "bohemia.net"),
                Entry("tarkov", "Tarkov", "Только официальный сайт; вход, матчмейкинг и рейд вручную.",
                    "escapefromtarkov.com", "battlestategames.com")
            };
        }

        public static string DomainsFor(IEnumerable<string> ids, string customDomains)
        {
            List<string> domains = new List<string>();
            foreach (ServiceEntry service in Select(ids)) domains.AddRange(service.Domains);
            if (!String.IsNullOrWhiteSpace(customDomains)) domains.Add(customDomains);
            if (domains.Count == 0) return String.Empty;
            return String.Join("\n", Profiles.ValidateDomains(String.Join("\n", domains.ToArray())));
        }

        internal static List<ServiceEntry> Select(IEnumerable<string> ids)
        {
            if (ids == null) throw new ArgumentNullException("ids");
            HashSet<string> requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string id in ids)
            {
                if (String.IsNullOrWhiteSpace(id)) throw new ArgumentException("Неизвестный сервис.", "ids");
                requested.Add(id);
            }
            List<ServiceEntry> selected = new List<ServiceEntry>();
            foreach (ServiceEntry service in All())
                if (requested.Remove(service.Id)) selected.Add(service);
            if (requested.Count != 0) throw new ArgumentException("Неизвестный сервис.", "ids");
            return selected;
        }

        private static ServiceEntry Entry(string id, string name, string scope, params string[] domains)
        {
            return new ServiceEntry { Id = id, Name = name, Scope = scope, Domains = domains };
        }
    }

    public sealed class CheckRow
    {
        public string Key, ServiceId, Name, State, Detail;
        public string DiagnosticCode;
        public bool? Passed;
        public long Milliseconds;
    }

    public static class ServiceDiagnostics
    {
        public const int AttemptCount = 3;
        public const int AttemptTimeoutMilliseconds = 8000;
        public const int MaxGatewayMessageBytes = 8192;
        private const string GatewayUri = "wss://gateway.discord.gg/?v=10&encoding=json";
        // Public bootstrap addresses from the official Telegram Desktop source,
        // checked 2026-09-27. No account, client state or discovery scan is used.
        internal static string[] TelegramProbeAddresses()
        {
            return new[] { "149.154.175.50", "149.154.167.51", "149.154.175.100" };
        }

        // Sources and limits:
        // https://docs.discord.com/developers/events/gateway (Hello precedes Identify).
        // https://core.telegram.org/mtproto (HTTPS web availability is not MTProto).
        // https://reforger.armaplatform.com/news/boot-camp-host-your-own-game
        // Game-server destinations vary; we never infer them from a website.
        // HTTPS probes use NetworkInfo: headers only, system proxy, normal TLS
        // validation, no cookies/credentials, no redirects and no body storage.
        // Framework ClientWebSocket does not expose redirect control. Its WSS
        // upgrade uses platform redirect defaults, unlike the HTTPS probes.
        // https://github.com/microsoft/referencesource/blob/main/System/net/System/Net/WebSockets/ClientWebSocket.cs
        public static async Task<List<CheckRow>> RunAsync(IEnumerable<string> ids,
            IProgress<CheckRow> progress, CancellationToken token)
        {
            List<ServiceEntry> selected = ServiceCatalog.Select(ids);
            token.ThrowIfCancellationRequested();
            List<Task<List<CheckRow>>> tasks = new List<Task<List<CheckRow>>>();
            // At most three services active at once; retries within each service
            // are sequential. Nothing is scheduled periodically or persisted.
            using (SemaphoreSlim limit = new SemaphoreSlim(3, 3))
            {
                foreach (ServiceEntry entry in selected)
                    tasks.Add(RunServiceAsync(entry, progress, limit, token));
                List<CheckRow>[] groups = await Task.WhenAll(tasks).ConfigureAwait(false);
                List<CheckRow> result = new List<CheckRow>();
                foreach (List<CheckRow> group in groups) result.AddRange(group);
                return result;
            }
        }

        public static string Classify(int succeeded, int attempted)
        {
            ValidateCounts(succeeded, attempted);
            if (attempted == 0) return "Не проверено";
            if (succeeded == 0) return "Нет ответа";
            return succeeded == attempted ? "Доступен" : "Нестабильно";
        }

        public static bool? PassedFor(int succeeded, int attempted)
        {
            ValidateCounts(succeeded, attempted);
            return attempted == 0 ? (bool?)null : succeeded == attempted;
        }

        // Pure aggregation: a TLS reply and an acceptable HTTP status are
        // different evidence. Neither proves login, media, messaging or gameplay.
        public static List<CheckRow> AggregateHttps(string serviceId, string key, string name,
            string host, IEnumerable<ProbeResult> samples)
        {
            if (samples == null) throw new ArgumentNullException("samples");
            int count = 0, tls = 0, http = 0, redirects = 0;
            long milliseconds = 0;
            SortedDictionary<int, int> statuses = new SortedDictionary<int, int>();
            foreach (ProbeResult sample in samples)
            {
                if (sample == null) throw new ArgumentException("Пустой результат проверки.", "samples");
                count++;
                if (count > AttemptCount) throw new ArgumentException("Слишком много результатов.", "samples");
                milliseconds += Math.Max(0, sample.Milliseconds);
                if (!sample.TlsReached) continue;
                tls++;
                int statusCount;
                statuses.TryGetValue(sample.StatusCode, out statusCount);
                statuses[sample.StatusCode] = statusCount + 1;
                if (sample.StatusCode >= 200 && sample.StatusCode < 400) http++;
                if (sample.StatusCode >= 300 && sample.StatusCode < 400) redirects++;
            }
            long mean = count == 0 ? 0 : milliseconds / count;
            List<string> statusText = new List<string>();
            foreach (KeyValuePair<int, int> status in statuses)
                statusText.Add(status.Key.ToString(CultureInfo.InvariantCulture) + " x" + status.Value.ToString(CultureInfo.InvariantCulture));
            string endpoint = "https://" + host + "/. ";
            string failures = tls == count ? String.Empty : " Нет HTTPS-ответа: причина может быть в DNS, прокси, сети, TLS или сертификате; это не доказательство блокировки.";
            CheckRow tlsRow = Automatic(serviceId, key + ".tls", name + " — HTTPS/TLS", tls, count, mean,
                endpoint + Counts(tls, count) + " ответов через HTTPS с проверкой сертификата." + failures);
            CheckRow httpRow = Automatic(serviceId, key + ".http", name + " — HTTP сайта", http, count, mean,
                endpoint + Counts(http, count) + " ответов HTTP 2xx/3xx. " +
                (statusText.Count == 0 ? "HTTP-код не получен. " : "Коды: " + String.Join(", ", statusText.ToArray()) + ". ") +
                (redirects > 0 ? "Перенаправления не выполнялись. " : String.Empty) +
                "Тело страницы не загружалось; работа приложения не подтверждена.");
            // 403/429/5xx are actual replies, so do not call them 'no response'.
            if (http < tls)
            {
                httpRow.State = "Ответ сервиса";
                httpRow.Passed = null;
                httpRow.Detail += " HTTP-отказ не считается доказательством DPI и не запускает подбор обхода.";
            }
            return new List<CheckRow> { tlsRow, httpRow };
        }

        public static CheckRow AggregateNativeTcp(int dc, int succeeded, int attempted, long milliseconds)
        {
            if (dc < 1 || dc > 3) throw new ArgumentOutOfRangeException("dc");
            CheckRow row = Automatic("telegram", "dc" + dc.ToString(CultureInfo.InvariantCulture),
                "Telegram — TCP DC " + dc.ToString(CultureInfo.InvariantCulture), succeeded, attempted, milliseconds,
                Counts(succeeded, attempted) + " TCP-соединений с публичным адресом Telegram DC " +
                dc.ToString(CultureInfo.InvariantCulture) + " на порту 443. Только установка TCP: без MTProto, TLS, учётной записи и отправки данных. " +
                "Это не проверка подлинности удалённого сервера или работы сообщений. Системный HTTP-прокси не используется; сокет идёт по маршрутам Windows/VPN.");
            if (attempted > 0 && succeeded == attempted) row.State = "TCP открыт";
            return row;
        }

        // Pure and bounded parser, used by the live Hello test and offline tests.
        // Only a positive heartbeat interval and opcode 10 constitute Hello.
        public static bool IsGatewayHello(byte[] bytes, int count)
        {
            if (bytes == null || count <= 0 || count > bytes.Length || count > MaxGatewayMessageBytes) return false;
            try
            {
                string json = new UTF8Encoding(false, true).GetString(bytes, 0, count);
                JavaScriptSerializer parser = new JavaScriptSerializer { MaxJsonLength = MaxGatewayMessageBytes, RecursionLimit = 8 };
                Dictionary<string, object> root = parser.DeserializeObject(json) as Dictionary<string, object>;
                object opcode, data, interval;
                if (root == null || !root.TryGetValue("op", out opcode) || !(opcode is int) || (int)opcode != 10 ||
                    !root.TryGetValue("d", out data)) return false;
                Dictionary<string, object> details = data as Dictionary<string, object>;
                if (details == null || !details.TryGetValue("heartbeat_interval", out interval)) return false;
                if (!(interval is int) && !(interval is long) && !(interval is decimal) && !(interval is double)) return false;
                double value = Convert.ToDouble(interval, CultureInfo.InvariantCulture);
                return value > 0 && !Double.IsNaN(value) && !Double.IsInfinity(value);
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (OverflowException) { return false; }
        }

        public static List<CheckRow> ManualRows(string serviceId)
        {
            List<CheckRow> rows = new List<CheckRow>();
            switch (serviceId)
            {
                case "discord":
                    rows.Add(Manual(serviceId, "voice", "Discord — голос", "В голосовом канале проверьте звук в обе стороны и переподключения 1–2 минуты. Если подключение стабильно, но звука нет: проверьте устройство вывода Discord; для Sonar — Chat, реальные наушники и ChatMix. Короткая проверка с физическими наушниками напрямую отделяет аудиомаршрут от сети. Сохраните старое устройство для отката. Gateway голос не проверяет."));
                    rows.Add(Manual(serviceId, "screen", "Discord — демонстрация экрана", "Запустите и посмотрите демонстрацию экрана. Успех сайта или Gateway не подтверждает передачу медиа."));
                    break;
                case "youtube":
                    rows.Add(Manual(serviceId, "playback", "YouTube — видео", "Откройте видео, выберите качество и проверьте воспроизведение и перемотку. Ответ сайта не проверяет видеосегменты CDN или QUIC."));
                    break;
                case "telegram":
                    rows.Add(Manual(serviceId, "mtproto", "Telegram — приложение / MTProto", "В приложении отправьте сообщение в «Избранное» и проверьте загрузку вложения. HTTPS Telegram Web не доказывает работу MTProto; звонки проверяются отдельно."));
                    break;
                case "steam":
                    rows.Add(Manual(serviceId, "client", "Steam — клиент, загрузки и игра", "Проверьте вход в клиент, загрузку и подключение к нужному игровому серверу. Ответы сайтов Steam не проверяют эти соединения; адреса серверов не угадываются."));
                    break;
                case "arma":
                    rows.Add(Manual(serviceId, "game", "Arma Reforger — игровой сервер", "Откройте браузер серверов, выберите свой сервер и подключитесь. IP и порт зависят от сервера; автоматического теста игрового соединения нет."));
                    break;
                case "tarkov":
                    rows.Add(Manual(serviceId, "game", "Tarkov — вход и рейд", "Проверьте вход через лаунчер, матчмейкинг и подключение к рейду. Сайт не подтверждает игровой backend; токены, файлы клиента и конфигурация не читаются."));
                    break;
                default: throw new ArgumentException("Неизвестный сервис.", "serviceId");
            }
            return rows;
        }

        private static async Task<List<CheckRow>> RunServiceAsync(ServiceEntry entry, IProgress<CheckRow> progress,
            SemaphoreSlim limit, CancellationToken token)
        {
            await limit.WaitAsync(token).ConfigureAwait(false);
            try
            {
                List<CheckRow> rows = new List<CheckRow>();
                foreach (CheckRow manual in ManualRows(entry.Id)) Add(rows, manual, progress);
                string host;
                switch (entry.Id)
                {
                    case "discord": host = "discord.com"; break;
                    case "youtube": host = "www.youtube.com"; break;
                    case "telegram": host = "web.telegram.org"; break;
                    case "steam": host = "store.steampowered.com"; break;
                    case "arma": host = "reforger.armaplatform.com"; break;
                    case "tarkov": host = "www.escapefromtarkov.com"; break;
                    default: throw new ArgumentException("Неизвестный сервис.");
                }
                await AddHttpsAsync(entry, "web", entry.Name + " (только сайт)", host, rows, progress, token).ConfigureAwait(false);
                if (entry.Id == "steam")
                    await AddHttpsAsync(entry, "community", "Steam Community (только сайт)", "steamcommunity.com", rows, progress, token).ConfigureAwait(false);
                if (entry.Id == "telegram")
                {
                    string[] addresses = TelegramProbeAddresses();
                    for (int dc = 0; dc < addresses.Length; dc++)
                    {
                        int succeeded = 0;
                        long milliseconds = 0;
                        for (int i = 0; i < AttemptCount; i++)
                        {
                            GatewayAttempt attempt = await ProbeNativeTcpAsync(addresses[dc], token).ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                            if (attempt.Passed) succeeded++;
                            milliseconds += attempt.Milliseconds;
                        }
                        Add(rows, AggregateNativeTcp(dc + 1, succeeded, AttemptCount, milliseconds / AttemptCount), progress);
                    }
                }
                if (entry.Id == "discord")
                {
                    // A green homepage/unauthenticated Gateway alone never
                    // established Desktop bootstrap or updater availability.
                    // Fixed public endpoints only; no account/session reads.
                    var desktop=await Diagnostics.DiscordDesktopDiagnostics.RunAsync(null,token).ConfigureAwait(false);
                    foreach(var probe in desktop)Add(rows,probe,progress);
                    int success = 0, unsupported = 0;
                    long milliseconds = 0;
                    string lastFailure = String.Empty;
                    for (int i = 0; i < AttemptCount; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        GatewayAttempt attempt = await ProbeGatewayAsync(token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        if (attempt.Passed) success++;
                        if (attempt.Unsupported) unsupported++;
                        if (!attempt.Passed) lastFailure = attempt.Detail;
                        milliseconds += attempt.Milliseconds;
                    }
                    CheckRow row = Automatic(entry.Id, "gateway", "Discord — Gateway WSS Hello", success,
                        AttemptCount, milliseconds / AttemptCount, Counts(success, AttemptCount) +
                        " корректных Hello (op 10) от публичного Gateway по WSS. Без входа, Identify, Resume и heartbeat; " +
                        "не проверяет отправку сообщений, голос или экран. " + lastFailure);
                    if (unsupported == AttemptCount) { row.State = "Не проверено"; row.Passed = null; }
                    Add(rows, row, progress);
                }
                return rows;
            }
            finally { limit.Release(); }
        }

        private static async Task AddHttpsAsync(ServiceEntry entry, string key, string name, string host,
            List<CheckRow> rows, IProgress<CheckRow> progress, CancellationToken token)
        {
            List<ProbeResult> samples = new List<ProbeResult>();
            for (int i = 0; i < AttemptCount; i++)
            {
                token.ThrowIfCancellationRequested();
                samples.Add(await NetworkInfo.ProbeAsync(host, token).ConfigureAwait(false));
                token.ThrowIfCancellationRequested();
            }
            foreach (CheckRow row in AggregateHttps(entry.Id, key, name, host, samples)) Add(rows, row, progress);
        }

        private sealed class GatewayAttempt
        {
            public bool Passed, Unsupported;
            public long Milliseconds;
            public string Detail = String.Empty;
        }

        private static async Task<GatewayAttempt> ProbeNativeTcpAsync(string address, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Stopwatch elapsed = Stopwatch.StartNew();
            GatewayAttempt result = new GatewayAttempt();
            try
            {
                using (TcpClient socket = new TcpClient(AddressFamily.InterNetwork))
                using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    Task pending = socket.ConnectAsync(IPAddress.Parse(address), 443);
                    Task deadline = Task.Delay(AttemptTimeoutMilliseconds, linked.Token);
                    if (await Task.WhenAny(pending, deadline).ConfigureAwait(false) == pending)
                    {
                        await pending.ConfigureAwait(false);
                        result.Passed = socket.Connected;
                        linked.Cancel();
                    }
                    else
                    {
                        socket.Close();
                        Observe(pending);
                    }
                    // No GetStream, Send, Receive, account or protocol payload.
                }
            }
            catch (SocketException) { }
            catch (System.Security.SecurityException) { }
            finally { elapsed.Stop(); result.Milliseconds = elapsed.ElapsedMilliseconds; }
            token.ThrowIfCancellationRequested();
            return result;
        }

        private static async Task<GatewayAttempt> ProbeGatewayAsync(CancellationToken token)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            GatewayAttempt result = new GatewayAttempt();
            try
            {
                token.ThrowIfCancellationRequested();
                using (ClientWebSocket socket = new ClientWebSocket())
                using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    socket.Options.UseDefaultCredentials = false;
                    socket.Options.Credentials = null;
                    socket.Options.Cookies = null;
                    socket.Options.Proxy = WebRequest.DefaultWebProxy;
                    socket.Options.KeepAliveInterval = TimeSpan.Zero;
                    socket.Options.SetBuffer(1024, 1024);
                    // No certificate callback, no client cert, no user token,
                    // no remote-discovered URL and no application-data SendAsync.
                    Task<bool> pending = ReceiveHelloAsync(socket, linked.Token);
                    Task deadline = Task.Delay(AttemptTimeoutMilliseconds, linked.Token);
                    if (await Task.WhenAny(pending, deadline).ConfigureAwait(false) != pending)
                    {
                        linked.Cancel();
                        socket.Abort();
                        Observe(pending);
                        result.Detail = token.IsCancellationRequested ? "Отменено." : "Лимит 8 секунд: Hello не получен.";
                    }
                    else
                    {
                        result.Passed = await pending.ConfigureAwait(false);
                        if (!result.Passed) result.Detail = "Gateway не прислал корректный текстовый Hello в пределах 8 КиБ.";
                        linked.Cancel();
                        socket.Abort();
                    }
                }
            }
            catch (PlatformNotSupportedException) { result.Unsupported = true; result.Detail = "WebSocket не поддерживается этой версией Windows/.NET; проверьте Discord вручную."; }
            catch (OperationCanceledException) { result.Detail = token.IsCancellationRequested ? "Отменено." : "Лимит времени WSS."; }
            catch (WebSocketException) { result.Detail = "WSS не установлен: возможны DNS, прокси, сеть, TLS или отказ Gateway; причина не определена."; }
            catch (System.Security.SecurityException) { result.Detail = "Система запретила сетевой доступ."; }
            catch (WebException) { result.Detail = "Ошибка системного прокси или соединения WSS."; }
            finally { elapsed.Stop(); result.Milliseconds = elapsed.ElapsedMilliseconds; }
            return result;
        }

        private static async Task<bool> ReceiveHelloAsync(ClientWebSocket socket, CancellationToken token)
        {
            byte[] message = new byte[MaxGatewayMessageBytes];
            try
            {
                await socket.ConnectAsync(new Uri(GatewayUri, UriKind.Absolute), token).ConfigureAwait(false);
                int length = 0;
                for (int frames = 0; frames < 128 && length < message.Length; frames++)
                {
                    WebSocketReceiveResult received = await socket.ReceiveAsync(
                        new ArraySegment<byte>(message, length, message.Length - length), token).ConfigureAwait(false);
                    if (received.MessageType != WebSocketMessageType.Text) return false;
                    length += received.Count;
                    if (received.EndOfMessage) return IsGatewayHello(message, length);
                }
                return false;
            }
            finally { Array.Clear(message, 0, message.Length); }
        }

        private static void Observe(Task task)
        {
            task.ContinueWith(delegate(Task finished)
            {
                if (finished.IsFaulted) { AggregateException observed = finished.Exception; GC.KeepAlive(observed); }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private static void ValidateCounts(int succeeded, int attempted)
        {
            if (attempted < 0 || attempted > AttemptCount || succeeded < 0 || succeeded > attempted)
                throw new ArgumentOutOfRangeException("succeeded", "Некорректное число попыток.");
        }

        private static string Counts(int succeeded, int attempted)
        {
            return succeeded.ToString(CultureInfo.InvariantCulture) + "/" + attempted.ToString(CultureInfo.InvariantCulture);
        }

        private static CheckRow Automatic(string serviceId, string key, string name, int succeeded, int attempted,
            long milliseconds, string detail)
        {
            return new CheckRow { ServiceId = serviceId, Key = serviceId + "." + key, Name = name,
                State = Classify(succeeded, attempted), Passed = PassedFor(succeeded, attempted),
                Milliseconds = milliseconds, Detail = detail };
        }

        private static CheckRow Manual(string serviceId, string key, string name, string detail)
        {
            return new CheckRow { ServiceId = serviceId, Key = serviceId + "." + key, Name = name,
                State = "Вручную", Passed = null, Detail = detail, Milliseconds = 0 };
        }

        private static void Add(List<CheckRow> rows, CheckRow row, IProgress<CheckRow> progress)
        {
            rows.Add(row);
            if (progress != null) progress.Report(new CheckRow { Key = row.Key, ServiceId = row.ServiceId,
                Name = row.Name, State = row.State, Detail = row.Detail, DiagnosticCode=row.DiagnosticCode, Passed = row.Passed, Milliseconds = row.Milliseconds });
        }
    }
}
