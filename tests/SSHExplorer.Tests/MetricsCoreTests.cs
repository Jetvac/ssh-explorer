#if CORE_TESTS
using SSHExplorer.Core;

/// <summary>0.6: what a profile records (with folders), custom metric rules, imported commands, the scheduler without a network, Windows ACLs.</summary>
internal static class MetricsCoreTests
{
    public static async Task RunAsync(TestSuite t)
    {
        await t.Run("metrics profile: background recording and custom metrics come from the server and its folders; RDP never records", () =>
        {
            var folders = new List<FolderDefaults>
            {
                new() { Path = "Прод", RecordHistory = true, CustomMetrics = [new CustomMetricDefinition { Name = "nginx", Command = "echo 1" }, new CustomMetricDefinition { Name = "queue", Command = "echo 2" }] },
            };
            var server = new ServerProfile { Name = "web", Host = "w", Username = "u", Group = "Прод/Web", CustomMetrics = [new CustomMetricDefinition { Name = "nginx", Command = "echo own" }] };
            var custom = MetricsProfile.CustomMetrics(server, folders);
            TestSuite.True(MetricsProfile.RecordsInBackground(server, folders) && custom.Count == 2 && custom.Single(m => m.Name == "nginx").Command == "echo own", string.Join(",", custom.Select(m => m.Name + ":" + m.Command)));
            TestSuite.True(!MetricsProfile.RecordsInBackground(new ServerProfile { Group = "Другое" }, folders) && !MetricsProfile.RecordsInBackground(new ServerProfile { Group = "Прод", RecordHistory = true, Protocol = ConnectionProtocol.Rdp }, folders)
                && MetricsProfile.RecordsInBackground(new ServerProfile { RecordHistory = true }, []), "other folders, RDP and the profile's own flag");
        });

        await t.Run("custom metric definitions: name, command, interval and unit are checked", () =>
        {
            TestSuite.True(new CustomMetricDefinition { Name = "ok_1-a", Command = "echo 1" }.Validate() is null, "valid");
            foreach (var bad in new[] { new CustomMetricDefinition { Name = "", Command = "x" }, new CustomMetricDefinition { Name = "имя", Command = "x" }, new CustomMetricDefinition { Name = "a", Command = " " }, new CustomMetricDefinition { Name = "a", Command = "x", IntervalSeconds = 10 }, new CustomMetricDefinition { Name = "a", Command = "x", Unit = new string('u', 13) } })
                TestSuite.True(bad.Validate() is not null, bad.Name + " " + bad.IntervalSeconds);
        });

        await t.Run("exchange: imported custom metric commands are off until the user confirms them", async () =>
        {
            var profile = new ServerProfile { Name = "app", Host = "app.example.net", Username = "deploy", CustomMetrics = [new CustomMetricDefinition { Name = "danger", Command = "rm -rf /tmp/x; echo 1", Approved = true }] };
            var doc = await ProfileExchange.BuildAsync([profile], new ExportOptions(), "0.6.0", _ => Task.FromResult<string?>(null), new AppSettings(), [], s => s);
            var back = ProfileExchange.Parse(ProfileExchange.Serialize(doc));
            TestSuite.True(back.Servers[0].Profile.CustomMetrics is [{ Name: "danger", Approved: false }] && profile.CustomMetrics[0].Approved, "approval is dropped on import, the source keeps it");
        });

        await t.Run("scheduler: a background server without a saved password is not connected and says why; it is not retried at once", async () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "sshx-sched-" + Guid.NewGuid().ToString("N"));
            var store = new AppStore(dir);
            var noPassword = new ServerProfile { Name = "no-pass", Host = "192.0.2.10", Username = "u", Authentication = AuthenticationKind.Password, RecordHistory = true };
            var agent = new ServerProfile { Name = "agent", Host = "192.0.2.11", Username = "u", Authentication = AuthenticationKind.Agent, RecordHistory = true };
            var idle = new ServerProfile { Name = "idle", Host = "192.0.2.12", Username = "u", Authentication = AuthenticationKind.Agent };
            store.Profiles.AddRange([noPassword, agent, idle]);
            int connects = 0, samples = 0;
            await using var scheduler = new MetricsScheduler(store, (_, _) => { samples++; return Task.CompletedTask; })
            {
                ConnectOverride = (_, _) => { connects++; throw new IOException("сеть недоступна"); },
            };
            await scheduler.TickAsync(CancellationToken.None);
            await scheduler.TickAsync(CancellationToken.None);
            TestSuite.True(scheduler.StatusOf(noPassword.Id) is { Problem: { } p } && p.Contains("Пароль не сохранён", StringComparison.Ordinal), scheduler.StatusOf(noPassword.Id)?.Problem ?? "none");
            TestSuite.True(scheduler.StatusOf(agent.Id) is { Problem: { } a, Background: true } && a.Contains("сеть недоступна", StringComparison.Ordinal) && connects == 1 && samples == 0, $"{scheduler.StatusOf(agent.Id)?.Problem} connects={connects}");
            TestSuite.True(scheduler.StatusOf(idle.Id) is null, "a server without recording has no state");
            scheduler.Wake(agent.Id); await scheduler.TickAsync(CancellationToken.None);
            TestSuite.True(connects == 2, "«Обновить» retries at once");
            try { Directory.Delete(dir, true); } catch (IOException) { }
        });

        await t.Run("windows acl: owner, attributes and rules (one rule or many) are read from Get-Acl", () =>
        {
            var many = WindowsHost.ParseAcl("{\"o\":\"BUILTIN\\\\Administrators\",\"at\":\"Directory\",\"r\":[{\"i\":\"NT AUTHORITY\\\\SYSTEM\",\"t\":\"Allow\",\"r\":\"FullControl\",\"h\":true},{\"i\":\"BUILTIN\\\\Users\",\"t\":\"Deny\",\"r\":\"Write, Synchronize\",\"h\":false}]}");
            var one = WindowsHost.ParseAcl("ignored prefix {\"o\":\"me\",\"at\":\"Archive\",\"r\":{\"i\":\"me\",\"t\":\"Allow\",\"r\":\"Modify, Synchronize\",\"h\":false}}");
            TestSuite.True(many is { Owner: "BUILTIN\\Administrators", Attributes: "Directory" } && many.Rules.Count == 2 && many.Rules[0] is { Allow: true, Inherited: true, Identity: "NT AUTHORITY\\SYSTEM" } && !many.Rules[1].Allow
                && one.Rules.Single().Rights == "Modify, Synchronize" && WindowsHost.DescribeRights(one.Rules[0].Rights) == "Изменение" && WindowsHost.DescribeRights("FullControl") == "Полный доступ", many + " / " + one);
            TestSuite.True(WindowsHost.AclCommand("/C:/Users/it's").StartsWith("powershell ", StringComparison.Ordinal), "one PowerShell command");
        });
    }
}
#endif
