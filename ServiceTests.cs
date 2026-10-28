using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace LocalLink
{
    // Offline only: pure catalogue/aggregation/parsing, plus pre-cancellation.
    // No live socket, file read, engine, filter, route or proxy mutation.
    public static class ServiceTests
    {
        private static int assertions;

        public static int Run()
        {
            assertions = 0;
            List<ServiceEntry> all = ServiceCatalog.All();
            Check(all.Count == 7, "seven explicit services including DCS");
            List<string> ids = new List<string>();
            HashSet<string> unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (ServiceEntry entry in all)
            {
                Check(unique.Add(entry.Id), "unique service id");
                Check(!String.IsNullOrWhiteSpace(entry.Name) && !String.IsNullOrWhiteSpace(entry.Scope), "label and scope present");
                ids.Add(entry.Id);
                Check(Profiles.ValidateDomains(String.Join("\n", entry.Domains)).Length > 0, "curated domains validated");
            }
            string combined = ServiceCatalog.DomainsFor(ids, String.Empty);
            Check(Profiles.ValidateDomains(combined).Length <= 128, "all primary service domains fit bounded inline limit");
            var customMany=new List<string>();for(int i=0;i<32;i++)customMany.Add("custom"+i+".example.org");
            Check(Profiles.ValidateDomains(ServiceCatalog.DomainsFor(ids,String.Join("\n",customMany))).Length==Profiles.ValidateDomains(combined).Length+32,"built-in categories do not consume the user's custom-domain allowance");
            Check(ServiceDiagnostics.ManualRows("dcs").Count==1,"DCS game connection remains an honest manual check");
            string duplicate = ServiceCatalog.DomainsFor(new[] { "discord", "DISCORD" }, "discord.com\nExample.Org");
            Check(Profiles.ValidateDomains(duplicate).Length == ServiceCatalog.All()[0].Domains.Length + 1, "case insensitive service ids and domain deduplication");
            Check(ServiceCatalog.DomainsFor(new string[0], "") == "", "empty selection stays empty");
            Check(ServiceCatalog.DomainsFor(new string[0], "example.org") == "example.org", "custom-only domains supported");
            Reject(delegate { ServiceCatalog.DomainsFor(new[] { "unknown" }, ""); }, "unknown service rejected");
            Reject(delegate { ServiceCatalog.DomainsFor(new[] { "discord" }, "https://evil.example/"); }, "custom URLs cannot become hosts");
            Reject(delegate { ServiceCatalog.DomainsFor(null, "example.org"); }, "null service selection rejected");
            all[0].Domains[0] = "mutated.example";
            all[0].Name = "changed";
            Check(ServiceCatalog.All()[0].Domains[0] == "discord.com" && ServiceCatalog.All()[0].Name == "Discord", "catalogue callers cannot mutate later selections");

            Check(ServiceDiagnostics.Classify(3, 3) == "Доступен", "3/3 available");
            Check(ServiceDiagnostics.Classify(2, 3) == "Нестабильно", "2/3 unstable");
            Check(ServiceDiagnostics.Classify(1, 3) == "Нестабильно", "1/3 unstable");
            Check(ServiceDiagnostics.Classify(0, 3) == "Нет ответа", "0/3 no response");
            Check(ServiceDiagnostics.Classify(0, 0) == "Не проверено" && ServiceDiagnostics.PassedFor(0, 0) == null, "untested is not failure or success");
            Check(ServiceDiagnostics.PassedFor(3, 3) == true && ServiceDiagnostics.PassedFor(2, 3) == false, "partial success cannot pass");
            Reject(delegate { ServiceDiagnostics.Classify(4, 3); }, "impossible counts rejected");
            Reject(delegate { ServiceDiagnostics.Classify(0, -1); }, "negative counts rejected");

            List<CheckRow> blocked = ServiceDiagnostics.AggregateHttps("discord", "web", "Сайт", "discord.com",
                new[] { Sample(true, 403, 10), Sample(true, 403, 20), Sample(true, 403, 30) });
            Check(blocked[0].Passed == true && blocked[1].Passed == null, "403 proves TLS reachability and does not trigger DPI tuning");
            Check(blocked[1].State == "Ответ сервиса" && blocked[1].Detail.Contains("403 x3"), "received error status is not labelled no response");
            Check(blocked[0].Milliseconds == 20, "mean whole-request duration");
            List<CheckRow> intermittent = ServiceDiagnostics.AggregateHttps("discord", "web", "Сайт", "discord.com",
                new[] { Sample(true, 200, 9), Sample(false, 0, 30), Sample(true, 200, 12) });
            Check(intermittent[0].State == "Нестабильно" && intermittent[1].State == "Нестабильно", "intermittent transport stays unstable");
            List<CheckRow> redirect = ServiceDiagnostics.AggregateHttps("youtube", "web", "Сайт", "www.youtube.com",
                new[] { Sample(true, 302, 1), Sample(true, 301, 1), Sample(true, 200, 1) });
            Check(redirect[1].Passed == true && redirect[1].Detail.Contains("Перенаправления не выполнялись"), "HTTP redirect observation never claims destination loaded");
            List<CheckRow> empty = ServiceDiagnostics.AggregateHttps("steam", "web", "Сайт", "store.steampowered.com", new ProbeResult[0]);
            Check(empty[0].Passed == null && empty[1].Passed == null, "empty samples not green");
            Check(ServiceDiagnostics.TelegramProbeAddresses().Length == 3, "three fixed public Telegram endpoints, not a scan");
            string[] endpoints = ServiceDiagnostics.TelegramProbeAddresses();
            endpoints[0] = "127.0.0.1";
            Check(ServiceDiagnostics.TelegramProbeAddresses()[0] == "149.154.175.50", "probe endpoint catalogue cannot be mutated");
            CheckRow native = ServiceDiagnostics.AggregateNativeTcp(1, 3, 3, 25);
            Check(native.State == "TCP открыт" && native.Passed == true && native.Key == "telegram.dc1", "native TCP has a distinct limited result");
            Check(native.Detail.Contains("без MTProto") && native.Detail.Contains("не проверка подлинности"), "TCP success never promises protocol or authentication");
            Check(ServiceDiagnostics.AggregateNativeTcp(2, 0, 0, 0).Passed == null, "untested native TCP stays unknown");
            Check(ServiceDiagnostics.AggregateNativeTcp(3, 1, 3, 10).State == "Нестабильно", "native intermittent TCP remains unstable");
            Reject(delegate { ServiceDiagnostics.AggregateNativeTcp(4, 3, 3, 1); }, "native DC scope bounded");
            Reject(delegate { ServiceDiagnostics.AggregateHttps("x", "x", "x", "example.org", new ProbeResult[] { null }); }, "null sample rejected");
            foreach (string id in ids)
            {
                foreach (CheckRow row in ServiceDiagnostics.ManualRows(id))
                {
                    Check(row.Passed == null && row.State == "Вручную", "manual capability remains unverified: " + row.Key);
                    Check(unique.Add(row.Key), "manual keys unique");
                }
            }

            Check(Hello("{\"op\":10,\"d\":{\"heartbeat_interval\":45000}}"), "documented Discord Hello accepted");
            foreach (string bad in new[] { "", "not json", "[]", "{}", "{\"op\":0,\"d\":{\"heartbeat_interval\":45000}}",
                "{\"op\":\"10\",\"d\":{\"heartbeat_interval\":45000}}", "{\"op\":10,\"d\":null}",
                "{\"op\":10,\"d\":{\"heartbeat_interval\":0}}", "{\"op\":10,\"d\":{\"heartbeat_interval\":-1}}",
                "{\"op\":10,\"d\":{\"heartbeat_interval\":\"45000\"}}", "{\"op\":10,\"d\":{\"heartbeat_interval\":true}}" })
                Check(!Hello(bad), "non-Hello JSON rejected");
            Check(!ServiceDiagnostics.IsGatewayHello(new byte[] { 0xff, 0xfe }, 2), "invalid UTF8 rejected");
            Check(!ServiceDiagnostics.IsGatewayHello(null, 1), "null payload rejected");
            Check(!ServiceDiagnostics.IsGatewayHello(new byte[8193], 8193), "payload over 8 KiB rejected");
            Check(!ServiceDiagnostics.IsGatewayHello(new byte[1], 2), "invalid payload length rejected");
            bool cancelled = false;
            try { ServiceDiagnostics.RunAsync(ids, null, new CancellationToken(true)).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "pre-cancelled suite makes no network requests");
            Check(ServiceDiagnostics.RunAsync(new string[0], null, CancellationToken.None).GetAwaiter().GetResult().Count == 0, "empty suite makes no requests");
            Console.WriteLine("Service tests passed: " + assertions + " assertions (offline; no network or system changes).");
            return 0;
        }

        private static bool Hello(string json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            return ServiceDiagnostics.IsGatewayHello(bytes, bytes.Length);
        }

        private static ProbeResult Sample(bool tls, int status, long milliseconds)
        {
            return new ProbeResult { TlsReached = tls, StatusCode = status, HttpOk = status >= 200 && status < 400, Milliseconds = milliseconds };
        }

        private static void Check(bool condition, string name)
        {
            assertions++;
            if (!condition) throw new Exception("FAIL service test: " + name);
        }

        private static void Reject(Action action, string name)
        {
            assertions++;
            try { action(); }
            catch (ArgumentException) { return; }
            throw new Exception("FAIL service test: " + name);
        }
    }
}
