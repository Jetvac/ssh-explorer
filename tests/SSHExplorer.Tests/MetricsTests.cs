using System.Globalization;
using System.Text;
using SSHExplorer.Monitoring;

/// <summary>0.6 metric history: the store, roll-ups, retention, events, processes, custom metrics, alerts and exports.</summary>
internal static class MetricsTests
{
    static string TempDir() { var d = Path.Combine(Path.GetTempPath(), "sshx-metrics-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }
    static readonly DateTimeOffset Today = new(DateTime.UtcNow.Date, TimeSpan.Zero);

    public static async Task RunAsync(TestSuite t)
    {
        await t.Run("metrics store: samples come back with their time, value and unit; non-numbers are gaps, not zeros", () =>
        {
            var store = new MetricsStore(TempDir()); var id = Guid.NewGuid();
            var t0 = Today.AddHours(1);
            store.Append(id, t0, [new(MetricSeries.Cpu, 10, "%"), new(MetricSeries.FileSystem("/"), 55.5, "%")]);
            store.Append(id, t0.AddMinutes(1), [new(MetricSeries.Cpu, double.NaN, "%"), new(MetricSeries.FileSystem("/"), 56, "%")]);
            store.Append(id, t0.AddMinutes(2), [new(MetricSeries.Cpu, 30, "%")]);
            var data = store.Read(id, [MetricSeries.Cpu, MetricSeries.FileSystem("/"), "missing"], t0, t0.AddMinutes(5), MetricResolution.Raw);
            var cpu = data[MetricSeries.Cpu];
            TestSuite.True(cpu.Count == 2 && cpu[0].Time == t0 && cpu[0].Average == 10 && cpu[1].Time == t0.AddMinutes(2) && cpu[1].Average == 30, string.Join(",", cpu));
            TestSuite.True(data[MetricSeries.FileSystem("/")].Count == 2 && data["missing"].Count == 0 && store.SeriesOf(id)[MetricSeries.Cpu] == "%", "fs and unknown series");
        });

        await t.Run("metrics store: a record torn by a crash is dropped and later samples still read", () =>
        {
            var root = TempDir(); var store = new MetricsStore(root); var id = Guid.NewGuid(); var t0 = Today.AddHours(2);
            store.Append(id, t0, [new(MetricSeries.Cpu, 1)]);
            var file = Directory.GetFiles(Path.Combine(root, id.ToString("N"), "raw")).Single();
            using (var s = new FileStream(file, FileMode.Append)) s.Write([1, 2, 3, 4, 5]);
            store.Append(id, t0.AddMinutes(1), [new(MetricSeries.Cpu, 2)]);
            var cpu = store.Read(id, [MetricSeries.Cpu], t0, t0.AddHours(1), MetricResolution.Raw)[MetricSeries.Cpu];
            TestSuite.True(cpu.Select(p => p.Average).SequenceEqual([1d, 2d]) && new FileInfo(file).Length == 32, string.Join(",", cpu) + " length " + new FileInfo(file).Length);
        });

        await t.Run("metrics store: finished days roll up to 5-minute and hourly points (average, min, max); old samples expire", () =>
        {
            var store = new MetricsStore(TempDir()); var id = Guid.NewGuid();
            var day = Today.AddDays(-10);
            for (int m = 0; m < 120; m++) store.Append(id, day.AddMinutes(m), [new(MetricSeries.Cpu, m % 5 == 0 ? 10 : 20)]);
            var recent = Today.AddHours(-1);
            store.Append(id, recent, [new(MetricSeries.Cpu, 77)]);
            store.Compact(DateTimeOffset.UtcNow, new MetricsRetention(7, 30, 365));
            var dir = Path.Combine(store.Root, id.ToString("N"));
            TestSuite.True(!File.Exists(Path.Combine(dir, "raw", day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".bin")) && File.Exists(Path.Combine(dir, "m5", day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".bin")) && File.Exists(Path.Combine(dir, "h1", day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".bin")), "raw expired, roll-ups written");
            var five = store.Read(id, [MetricSeries.Cpu], day, day.AddHours(2), MetricResolution.FiveMinutes)[MetricSeries.Cpu];
            TestSuite.True(five.Count == 24 && five.All(p => p.Count == 5 && Math.Abs(p.Average - 18) < 1e-9 && p.Min == 10 && p.Max == 20), $"{five.Count} {five.FirstOrDefault()}");
            var hours = store.Read(id, [MetricSeries.Cpu], day, day.AddHours(2), MetricResolution.Hour)[MetricSeries.Cpu];
            TestSuite.True(hours.Count == 2 && hours.All(p => p.Count == 60 && Math.Abs(p.Average - 18) < 1e-9), string.Join(",", hours));
            var fallback = store.Read(id, [MetricSeries.Cpu], day, day.AddHours(2), MetricResolution.Raw)[MetricSeries.Cpu];
            TestSuite.True(fallback.Count == 24 && fallback[0].Count == 5, "a day whose samples are gone is shown from its 5-minute points");
            var live = store.Read(id, [MetricSeries.Cpu], recent.AddMinutes(-10), recent.AddMinutes(10), MetricResolution.FiveMinutes)[MetricSeries.Cpu];
            TestSuite.True(live.Count == 1 && live[0].Average == 77, "today is rolled up on the fly");
            store.Compact(DateTimeOffset.UtcNow, new MetricsRetention(7, 30, 365));
            TestSuite.True(store.Read(id, [MetricSeries.Cpu], day, day.AddHours(2), MetricResolution.FiveMinutes)[MetricSeries.Cpu].Count == 24, "compaction is repeatable");
            store.Compact(DateTimeOffset.UtcNow, new MetricsRetention(1, 5, 9));
            TestSuite.True(store.Read(id, [MetricSeries.Cpu], day, day.AddHours(2), MetricResolution.Hour)[MetricSeries.Cpu].Count == 0, "every level expires by its own retention");
        });

        await t.Run("metrics store: events, the busiest processes near a moment, latest values after a restart, deletion", () =>
        {
            var root = TempDir(); var store = new MetricsStore(root); var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
            store.AddEvent(id, new MetricEvent(now.AddMinutes(-30), "mark", "обновил nginx"));
            store.AddEvent(id, new MetricEvent(now.AddMinutes(-5), "reboot", "Сервер перезагружен"));
            File.AppendAllText(Directory.GetFiles(Path.Combine(root, id.ToString("N"), "events")).Single(), "{\"t\":broken\n");
            var events = store.Events(id, now.AddHours(-1), now);
            TestSuite.True(events.Count == 2 && events[0].Text == "обновил nginx" && events[1].Kind == "reboot" && store.Events(id, now.AddMinutes(-10), now).Count == 1, string.Join(",", events));
            store.AppendProcesses(id, now.AddMinutes(-2), [new ProcessTopEntry("postgres", 42, "postgres", 87.5, 1_000_000), new ProcessTopEntry("nginx", 7, "www-data", 3, null)]);
            var top = store.ProcessesAt(id, now.AddMinutes(-1.5), TimeSpan.FromMinutes(2));
            TestSuite.True(top is { Processes.Count: 2 } && top.Processes[0] is { Name: "postgres", Pid: 42, Cpu: 87.5, Memory: 1_000_000 } && top.Processes[1].Memory is null && store.ProcessesAt(id, now.AddHours(-1), TimeSpan.FromMinutes(2)) is null, "processes");
            store.Append(id, now.AddMinutes(-1), [new(MetricSeries.Memory, 40, "%")]);
            var reopened = new MetricsStore(root).Latest(id, now);
            TestSuite.True(reopened.TryGetValue(MetricSeries.Memory, out var mem) && mem.Value == 40, "latest values are read back from today's file");
            TestSuite.True(store.SizeOf(id) > 0 && store.Servers().Contains(id), "size and list");
            store.DeleteServer(id);
            TestSuite.True(!Directory.Exists(Path.Combine(root, id.ToString("N"))) && store.SizeOf(id) == 0, "history deleted");
        });

        await t.Run("metric series: a monitor sample becomes system values and disk fullness; loopback traffic is not counted", () =>
        {
            var system = new SystemMetric(25, 4, 8_000, 2_000, 6_000, 1_000, 250, TimeSpan.FromHours(3), 0.5, 0.4, 0.3, 100, false);
            var snapshot = new MonitorSnapshot(DateTimeOffset.UtcNow, system,
                [new ProcessMetric(new ProcessIdentity(10, 1, 0), "a", "root", "S", 50, 100, null, null, null), new ProcessMetric(new ProcessIdentity(11, 1, 0), "b", "root", "S", 1, 9_000, null, null, null), new ProcessMetric(new ProcessIdentity(12, 1, 0), "c", "root", "S", null, null, null, null, null)],
                [new NetworkMetric("lo", 0, 0, 1e9, 1e9), new NetworkMetric("eth0", 0, 0, 100, 50), new NetworkMetric("eth1", 0, 0, 10, null)],
                [new DiskMetric("sda", 0, 0, 5, 7)], [new StorageMetric("ext4", "/", 100, 81, 19), new StorageMetric("tmpfs", "/empty", 0, 0, 0)], []);
            var values = MetricSeries.FromSnapshot(snapshot).ToDictionary(v => v.Name, v => v.Value);
            TestSuite.True(values[MetricSeries.Cpu] == 25 && values[MetricSeries.Memory] == 75 && values[MetricSeries.Swap] == 25 && values[MetricSeries.Load1] == 0.5 && values[MetricSeries.NetReceive] == 110 && values[MetricSeries.NetSend] == 50
                && values[MetricSeries.DiskRead] == 5 && values[MetricSeries.FileSystem("/")] == 81 && !values.ContainsKey(MetricSeries.FileSystem("/empty")), string.Join(",", values));
            var top = MetricSeries.TopProcesses(snapshot, 1);
            TestSuite.True(top.Select(p => p.Name).SequenceEqual(["a", "b"]), "the top by CPU and the top by memory");
            TestSuite.True(MetricSeries.Format(1536, MetricSeries.BytesPerSecond, CultureInfo.InvariantCulture) == "1.5 КБ/с" && MetricSeries.Format(42.25, MetricSeries.Percent, CultureInfo.InvariantCulture) == "42 %" && MetricSeries.Format(3.5, "мс", CultureInfo.InvariantCulture) == "3.5 мс", "formatting");
        });

        await t.Run("custom metrics: a number or name=value lines; anything else is an error with its text, never a zero", () =>
        {
            var single = CustomMetricParser.Parse("42\n", "queue", "");
            var many = CustomMetricParser.Parse("active=3\r\nwaiting=2,5\n", "nginx", "");
            var percent = CustomMetricParser.Parse(" 12% ", "x", "%");
            TestSuite.True(single.Error is null && single.Values.Single() is { Name: "custom:queue", Value: 42 } && many.Values.Select(v => v.Name + "=" + v.Value.ToString(CultureInfo.InvariantCulture)).SequenceEqual(["custom:nginx.active=3", "custom:nginx.waiting=2.5"]) && percent.Values.Single().Value == 12, "valid output");
            foreach (var (text, part) in new[] { ("", "ничего"), ("oops", "Строка 1"), ("a=b", "не число"), ("bad key=1", "только латиница"), (string.Join("\n", Enumerable.Range(0, 21).Select(i => $"k{i}=1")), "Больше 20") })
            {
                var r = CustomMetricParser.Parse(text, "x", "");
                TestSuite.True(r.Error is { } e && e.Contains(part, StringComparison.Ordinal) && r.Values.Count == 0, $"«{text}» → {r.Error}");
            }
            var http = CustomMetricTemplates.All.Single(x => x.Key == "service");
            TestSuite.True(http.Command(false, "it's") == "systemctl is-active --quiet 'it'\\''s' && echo 1 || echo 0" && http.Command(true, "it's").Contains("-Name 'it''s'", StringComparison.Ordinal), "template parameters are quoted for the shell");
            TestSuite.True(CustomMetricTemplates.SafeName("Мой nginx!") == "nginx" && CustomMetricTemplates.SafeName("___") == "metric", CustomMetricTemplates.SafeName("Мой nginx!"));
        });

        await t.Run("history alerts: a value held beyond the threshold for the whole window; gaps and short history do not count", () =>
        {
            var now = DateTimeOffset.UtcNow; var minute = TimeSpan.FromMinutes(1);
            MetricPoint P(int ago, double v) => new(now - TimeSpan.FromMinutes(ago), v, v, v, 1);
            var high = Enumerable.Range(0, 17).Reverse().Select(i => P(i, 95)).ToArray();
            TestSuite.True(MetricAlerts.Sustained(high, now, true, 90, 15, minute), "16 minutes above 90");
            TestSuite.True(!MetricAlerts.Sustained(high.Select((p, i) => i == 8 ? p with { Average = 80 } : p).ToArray(), now, true, 90, 15, minute), "one value below");
            TestSuite.True(!MetricAlerts.Sustained(high.Where((_, i) => i is < 5 or > 10).ToArray(), now, true, 90, 15, minute), "a hole");
            TestSuite.True(!MetricAlerts.Sustained(high.TakeLast(5).ToArray(), now, true, 90, 15, minute), "history too short");
            TestSuite.True(!MetricAlerts.Sustained(high.Select(p => p with { Time = p.Time - TimeSpan.FromMinutes(10) }).ToArray(), now, true, 90, 15, minute), "stale");
            TestSuite.True(MetricAlerts.Sustained(high.Select(p => p with { Average = 5 }).ToArray(), now, false, 10, 15, minute), "below");
            var growing = Enumerable.Range(0, 48).Select(h => new MetricPoint(now - TimeSpan.FromHours(47 - h), 50 + h / 24d, 0, 0, 12)).ToArray();
            TestSuite.True(MetricAlerts.DaysUntilFull(growing) is { } days && Math.Abs(days - 48) < 1.5, $"1 % a day from 52 % → about 48 days: {MetricAlerts.DaysUntilFull(growing)}");
            TestSuite.True(MetricAlerts.DaysUntilFull(growing.Select(p => p with { Average = 60 }).ToArray()) is null && MetricAlerts.DaysUntilFull(growing.TakeLast(10).ToArray()) is null, "flat or short history has no forecast");
            TestSuite.True(MetricAlerts.Matches("fs:*", "fs:/data") && !MetricAlerts.Matches("fs:*", "cpu") && MetricAlerts.Matches("cpu", "cpu"), "patterns");
        });

        await t.Run("export: CSV with «;» and local numbers, min/max for roll-ups; Prometheus text with escaped labels and no stale values", () =>
        {
            var t0 = new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);
            var csv = MetricsExport.Csv(new Dictionary<string, IReadOnlyList<MetricPoint>> { [MetricSeries.Cpu] = [new(t0, 12.5, 12.5, 12.5, 1), new(t0.AddMinutes(1), 13, 13, 13, 1)], [MetricSeries.FileSystem("/")] = [new(t0, 81, 81, 81, 1)] }, TimeZoneInfo.Utc, CultureInfo.GetCultureInfo("ru-RU"));
            var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            TestSuite.True(lines.Length == 3 && lines[0] == "Время;ЦП, %;Диск /, %" && lines[1] == "2026-11-06 10:00:00;12,5;81" && lines[2] == "2026-11-06 10:01:00;13;", string.Join(" | ", lines));
            var rolled = MetricsExport.Csv(new Dictionary<string, IReadOnlyList<MetricPoint>> { [MetricSeries.Cpu] = [new(t0, 10, 5, 20, 5)] }, TimeZoneInfo.Utc, CultureInfo.InvariantCulture);
            TestSuite.True(rolled.StartsWith("Время;ЦП, %;ЦП, мин;ЦП, макс\r\n2026-11-06 10:00:00;10;5;20", StringComparison.Ordinal), rolled);
            var now = DateTimeOffset.UtcNow;
            var text = MetricsExport.Prometheus([("web \"1\"", new Dictionary<string, (DateTimeOffset, double)> { [MetricSeries.Cpu] = (now, 12.5), [MetricSeries.FileSystem("/data")] = (now, 80), ["custom:nginx.active"] = (now, 3), [MetricSeries.Memory] = (now.AddHours(-1), 50) })], now, TimeSpan.FromMinutes(5));
            TestSuite.True(text.Contains("# TYPE sshx_cpu_percent gauge\nsshx_cpu_percent{server=\"web \\\"1\\\"\"} 12.5\n", StringComparison.Ordinal) && text.Contains("sshx_filesystem_used_percent{server=\"web \\\"1\\\"\",mount=\"/data\"} 80", StringComparison.Ordinal)
                && text.Contains("sshx_custom{server=\"web \\\"1\\\"\",metric=\"nginx.active\"} 3", StringComparison.Ordinal) && !text.Contains("sshx_memory_percent", StringComparison.Ordinal), text);
        });

        await t.Run("prometheus endpoint: GET /metrics on 127.0.0.1 answers the text; other paths get 404", async () =>
        {
            using var endpoint = new PrometheusEndpoint(0, () => "sshx_cpu_percent{server=\"a\"} 1\n");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var ok = await http.GetAsync($"http://127.0.0.1:{endpoint.Port}/metrics");
            var body = await ok.Content.ReadAsStringAsync();
            var missing = await http.GetAsync($"http://127.0.0.1:{endpoint.Port}/");
            TestSuite.True(ok.IsSuccessStatusCode && body.Contains("sshx_cpu_percent", StringComparison.Ordinal) && ok.Content.Headers.ContentType?.MediaType == "text/plain" && (int)missing.StatusCode == 404, $"{ok.StatusCode} {body} {missing.StatusCode}");
        });

        await t.Run("metrics sampler: a Windows sample, its processes, a reboot and custom metrics are stored; a failing command is reported", async () =>
        {
            var store = new MetricsStore(TempDir()); var id = Guid.NewGuid();
            long uptime = 5000; var clock = DateTimeOffset.UtcNow;
            string Collector() => "{\"v\":1,\"cpu\":12.5,\"n\":4,\"mt\":8000000000,\"mf\":2000000000,\"vt\":10000000000,\"vf\":3000000000,\"up\":" + uptime.ToString(CultureInfo.InvariantCulture)
                + ",\"d\":[{\"m\":\"C:\",\"f\":\"NTFS\",\"s\":100000000000,\"a\":25000000000}],\"net\":[{\"n\":\"Ethernet\",\"rx\":1000,\"tx\":500}],\"io\":[{\"n\":\"0 C:\",\"r\":10,\"w\":20}],\"p\":[{\"i\":100,\"n\":\"svchost\",\"u\":\"SYSTEM\",\"m\":1000000,\"c\":40,\"s\":1}]}";
            var commands = new List<string>();
            Task<ExecResult> Exec(string command, TimeSpan timeout, CancellationToken ct)
            {
                commands.Add(command);
                if (command == WindowsCollector.Command(true) || command == WindowsCollector.Command(false)) return Task.FromResult(new ExecResult(Collector(), "", 0));
                if (command == WindowsCollector.PowerShell("ok")) return Task.FromResult(new ExecResult("a=1\nb=2\n", "", 0));
                return Task.FromResult(new ExecResult("", "Get-Thing : not found\n", 1));
            }
            var sampler = new MetricsSampler(id, store, Exec, windows: true, clock: () => clock);
            CustomMetricSpec[] custom = [new(Guid.NewGuid(), "good", "ok", "шт", 60), new(Guid.NewGuid(), "bad", "fail", "", 60)];
            var first = await sampler.SampleAsync(custom, CancellationToken.None);
            var values = first.Values.ToDictionary(v => v.Name, v => v.Value);
            TestSuite.True(values[MetricSeries.Cpu] == 12.5 && values[MetricSeries.Memory] == 75 && values[MetricSeries.Swap] == 50 && values[MetricSeries.FileSystem("C:")] == 75 && values["custom:good.a"] == 1 && values["custom:good.b"] == 2, string.Join(",", values));
            TestSuite.True(first.CustomErrors.Count == 1 && first.CustomErrors[custom[1].Id].StartsWith("Код завершения 1: Get-Thing : not found", StringComparison.Ordinal) && store.SeriesOf(id)["custom:good.a"] == "шт", string.Join(",", first.CustomErrors.Values));
            TestSuite.True(store.ProcessesAt(id, clock, TimeSpan.FromMinutes(1)) is { Processes: [{ Name: "svchost", Cpu: 10 }] }, "processes (CPU per logical processor share)");
            int before = commands.Count;
            clock = clock.AddSeconds(30); await sampler.SampleAsync(custom, CancellationToken.None);
            TestSuite.True(commands.Count == before + 1, "custom metrics wait for their interval");
            uptime = 30; clock = clock.AddMinutes(1);
            var third = await sampler.SampleAsync(custom, CancellationToken.None);
            TestSuite.True(third.Events.Any(e => e.Kind == "reboot") && store.Events(id, clock.AddHours(-1), clock.AddMinutes(1)).Any(e => e.Kind == "reboot"), "a smaller uptime is a reboot");
            var check = await sampler.RunCustomAsync(new CustomMetricSpec(Guid.NewGuid(), "x", "ok", "", 60), CancellationToken.None);
            TestSuite.True(check.Error is null && check.Values.Count == 2, "a single test run");
        });
    }
}
