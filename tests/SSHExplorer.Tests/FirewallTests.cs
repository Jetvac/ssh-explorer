#if CORE_TESTS
using SSHExplorer.Core;

/// <summary>ufw: parsing, building, access check, the jump-only plan and the safe-apply script.</summary>
internal static class FirewallTests
{
    public const string ActiveFixture = """
        #client 203.0.113.10 51234 22
        #status
        Status: active
        Logging: on (low)
        Default: deny (incoming), allow (outgoing), disabled (routed)
        New profiles: skip

        To                         Action      From
        --                         ------      ----
        22/tcp                     ALLOW IN    Anywhere
        #defaults
        DEFAULT_INPUT_POLICY="DROP"
        DEFAULT_OUTPUT_POLICY="ACCEPT"
        #added
        Added user rules (see 'ufw status' for running firewall):
        ufw allow 22/tcp
        ufw allow from 10.0.0.0/8 to any port 5432 proto tcp comment 'office db'
        ufw limit OpenSSH
        ufw deny in on eth1 from 198.51.100.7
        ufw route allow in on wg0 out on eth0
        #neighbours
        docker
        """;

    public static async Task RunAsync(TestSuite t)
    {
        await t.Run("ufw: status, defaults, user rules, this connection's address and neighbours are parsed", () =>
        {
            var s = Ufw.Parse(ActiveFixture);
            TestSuite.True(s.Installed && s.Active && s.DefaultIncoming == "deny" && s.DefaultOutgoing == "allow" && s.DefaultRouted == "disabled" && s.Logging == "on (low)"
                && s.ClientAddress == "203.0.113.10" && s.SshPort == 22 && s.DockerPresent && !s.FirewalldActive && s.Rules.Count == 5 && s.Problem is null, $"{s.Active} {s.DefaultIncoming} {s.Rules.Count} {s.ClientAddress}");
            var db = s.Rules[1];
            TestSuite.True(db is { Action: FirewallAction.Allow, From: "10.0.0.0/8", ToPort: "5432", Proto: "tcp", Comment: "office db" } && db.ToText == "Порт 5432", db.ToString());
            TestSuite.True(s.Rules[2] is { Action: FirewallAction.Limit, App: "OpenSSH" } && s.Rules[3] is { Action: FirewallAction.Deny, Interface: "eth1", From: "198.51.100.7" } && s.Rules[4].Route, "limit/app, interface, route");
        });
        await t.Run("ufw: inactive status takes defaults from /etc/default/ufw; a missing ufw is explained", () =>
        {
            var s = Ufw.Parse("#client 10.1.1.1 4000 2222\n#status\nStatus: inactive\n#defaults\nDEFAULT_INPUT_POLICY=\"DROP\"\nDEFAULT_OUTPUT_POLICY=\"ACCEPT\"\nDEFAULT_FORWARD_POLICY=\"DROP\"\n#added\n(None)\n#neighbours\n");
            TestSuite.True(!s.Active && s.DefaultIncoming == "deny" && s.DefaultOutgoing == "allow" && s.DefaultRouted == "disabled" && s.Rules.Count == 0 && s.SshPort == 2222, s.DefaultIncoming);
            var none = Ufw.Parse("#client 1.1.1.1 1 22\n#noufw\n");
            TestSuite.True(!none.Installed && none.Problem!.Contains("ufw", StringComparison.Ordinal), none.Problem ?? "");
        });
        await t.Run("ufw: rules are built in full syntax with every value quoted; invalid input is refused", () =>
        {
            var draft = new RuleDraft { From = "10.0.0.0/8", Port = "80,443", Proto = "tcp", Comment = "web", Prepend = true };
            TestSuite.Equal("ufw prepend allow in proto tcp from '10.0.0.0/8' to any port '80,443' comment 'web'", Ufw.AddCommand(draft, 3));
            TestSuite.Equal("ufw allow in proto tcp from '10.0.0.0/8' to any port '80,443' comment 'web'", Ufw.AddCommand(draft, 0));
            TestSuite.Equal("ufw limit in on 'eth0' proto tcp from 'any' to any port '22'", Ufw.AddCommand(new RuleDraft { Action = FirewallAction.Limit, Port = "22", Interface = "eth0" }, 0));
            TestSuite.True(Ufw.Validate(new RuleDraft { From = "10.0.0.300", Port = "22" }) is not null && Ufw.Validate(new RuleDraft { Port = "70000" }) is not null
                && Ufw.Validate(new RuleDraft { Port = "6000:6007", Proto = "any" }) is not null && Ufw.Validate(new RuleDraft { Port = "22", Comment = "a'b" }) is not null
                && Ufw.Validate(new RuleDraft { Port = "22", Comment = "$(reboot)" }) is not null && Ufw.Validate(new RuleDraft { Port = "", Proto = "any" }) is not null
                && Ufw.Validate(new RuleDraft { From = "2001:db8::/32", Port = "443" }) is null, "validation");
            TestSuite.Equal("ufw --force delete 'allow' 'from' '10.0.0.0/8' 'to' 'any' 'port' '5432' 'proto' 'tcp'", Ufw.DeleteCommand(Ufw.Parse(ActiveFixture).Rules[1]));
        });
        await t.Run("ufw: the access check follows ufw — first match wins, then the default; unknowns are said so", () =>
        {
            var s = Ufw.Parse(ActiveFixture);
            TestSuite.True(Ufw.CheckAccess(s.Rules, true, "deny", "203.0.113.10", 22).Allowed == true, "allow 22/tcp");
            var deny = Ufw.Simulate(s.Rules, [Ufw.AddCommand(new RuleDraft { Action = FirewallAction.Deny, From = "203.0.113.0/24", Port = "22", Prepend = true }, s.Rules.Count)]);
            TestSuite.True(Ufw.CheckAccess(deny, true, "deny", "203.0.113.10", 22) is { Allowed: false } v && v.Reason.Contains("203.0.113.0/24", StringComparison.Ordinal), "a deny placed first wins");
            TestSuite.True(Ufw.CheckAccess([], true, "deny", "203.0.113.10", 22).Allowed == false && Ufw.CheckAccess([], true, "allow", "203.0.113.10", 22).Allowed == true && Ufw.CheckAccess([], false, "deny", "1.1.1.1", 22).Allowed == true, "defaults and off");
            var iface = Ufw.Parse("#client 1.1.1.1 1 22\n#status\nStatus: active\n#added\nufw allow in on eth0 to any port 22 proto tcp\n").Rules;
            TestSuite.True(Ufw.CheckAccess(iface, true, "deny", "1.1.1.1", 22).Allowed is null, "a rule bound to an interface cannot be judged");
            var v4only = Ufw.Parse("#client 1 1 22\n#status\nStatus: active\n#added\nufw allow from 10.0.0.0/8 to any port 22 proto tcp\n").Rules;
            TestSuite.True(Ufw.CheckAccess(v4only, true, "deny", "2001:db8::5", 22).Allowed == false, "an IPv6 client does not match an IPv4 subnet");
            TestSuite.True(Ufw.CheckAfter(s, [Ufw.DisableCommand]).Allowed == true && Ufw.CheckAfter(Ufw.Parse(ActiveFixture.Replace("ufw allow 22/tcp\n", "").Replace("ufw limit OpenSSH\n", "")), [Ufw.DefaultCommand("incoming", "allow")]).Allowed == true, "predict on/off and defaults");
        });
        await t.Run("ufw: «only through the jump server» opens SSH for it, removes rules open to others, closes the rest", () =>
        {
            var s = Ufw.Parse(ActiveFixture);
            var open = Ufw.OpenSshRules(s.Rules, 22, ["203.0.113.10"]);
            TestSuite.True(open.Select(r => r.Spec).SequenceEqual(["allow 22/tcp", "limit OpenSSH"]), string.Join(" | ", open.Select(r => r.Spec)));
            var plan = Ufw.BastionCommands(s, ["203.0.113.10"], 22, open, [], "bastion-1");
            TestSuite.True(plan[0].StartsWith("ufw prepend allow in proto tcp from '203.0.113.10' to any port '22' comment 'SSH Explorer: SSH через bastion-1'", StringComparison.Ordinal)
                && plan.Count(c => c.StartsWith("ufw --force delete", StringComparison.Ordinal)) == 2 && plan[^1].StartsWith("ufw deny in proto tcp from 'any' to any port '22'", StringComparison.Ordinal), string.Join("\n", plan));
            TestSuite.True(Ufw.CheckAfter(s, plan).Allowed == true && Ufw.CheckAfter(s, plan, "198.51.100.200").Allowed == false, "the jump server gets in, nobody else does");
            var kept = Ufw.BastionCommands(s, ["203.0.113.10"], 22, [], [], "b");
            TestSuite.True(Ufw.CheckAfter(s, kept, "198.51.100.200").Allowed == true, "an allow rule the user keeps still lets others in (the wizard refuses that)");
            var inactive = Ufw.Parse("#client 192.0.2.5 1 22\n#status\nStatus: inactive\n#defaults\nDEFAULT_INPUT_POLICY=\"DROP\"\n#added\n");
            var enable = Ufw.BastionCommands(inactive, ["192.0.2.5"], 22, [], [(80, "tcp"), (443, "tcp"), (22, "tcp")], "b");
            TestSuite.True(enable[^1] == Ufw.EnableCommand && enable.Count(c => c.Contains("было открыт", StringComparison.Ordinal) || c.Contains("был открыт", StringComparison.Ordinal)) == 2 && enable[0].StartsWith("ufw allow in", StringComparison.Ordinal)
                && Ufw.CheckAfter(inactive, enable).Allowed == true && Ufw.CheckAfter(inactive, enable, "8.8.8.8").Allowed == false, string.Join("\n", enable));
            TestSuite.Throws<ArgumentException>(() => Ufw.BastionCommands(s, ["any"], 22, [], [], "b"));
        });
        await t.Run("ufw rule sets: a server's rules become a set; what a set cannot carry is named", () =>
        {
            var s = Ufw.Parse(ActiveFixture);
            var (set, skipped) = FirewallRuleSets.FromSnapshot(s, "  база  ", true);
            TestSuite.True(set.Name == "база" && set.DefaultIncoming == "deny" && set.Rules.Count == 3 && skipped.Count == 2 && skipped.Any(r => r.App == "OpenSSH") && skipped.Any(r => r.Route)
                && set.Rules[1] is { From: "10.0.0.0/8", Port: "5432", Proto: "tcp", Comment: "office db" } && set.Rules[2] is { Action: FirewallAction.Deny, Interface: "eth1", From: "198.51.100.7", Proto: "any" }, string.Join(" | ", set.Rules));
            TestSuite.True(FirewallRuleSets.ValidateName("БАЗА", [set]) is not null && FirewallRuleSets.ValidateName("база", [set], set.Id) is null && FirewallRuleSets.ValidateName(" ", []) is not null, "names");
        });
        await t.Run("ufw rule sets: applying adds only what is missing, in order; «exact copy» also removes the rest", () =>
        {
            var s = Ufw.Parse(ActiveFixture);
            var set = new FirewallRuleSet { Name = "веб", Rules = [new RuleDraft { Port = "22", Comment = "другой комментарий" }, new RuleDraft { Port = "80,443" }, new RuleDraft { From = "203.0.113.10", Port = "9000" }], DefaultIncoming = "reject" };
            var add = FirewallRuleSets.Plan(s, set, false);
            TestSuite.True(add.Present.Count == 1 && add.Added.Count == 2 && add.Removed.Count == 0 && add.Commands.SequenceEqual(["ufw allow in proto tcp from 'any' to any port '80,443'", "ufw allow in proto tcp from '203.0.113.10' to any port '9000'", "ufw default reject incoming"])
                && add.Notes.Any(n => n.Contains("203.0.113.10", StringComparison.Ordinal)), string.Join("
", add.Commands));
            var exact = FirewallRuleSets.Plan(s, set, true);
            TestSuite.True(exact.Removed.Count == 4 && exact.Commands.Take(4).All(c => c.StartsWith("ufw --force delete", StringComparison.Ordinal)) && exact.Commands.Count == 7 && Ufw.CheckAfter(s, exact.Commands).Allowed == true
                && exact.Notes.Any(n => n.Contains("тоже будут удалены", StringComparison.Ordinal)), string.Join("
", exact.Commands));
            var same = FirewallRuleSets.Plan(s, FirewallRuleSets.FromSnapshot(s, "x", true).Set, false);
            TestSuite.True(same.Commands.Count == 0 && same.Present.Count == 3, "a set made from the server needs no change");
            var closing = new FirewallRuleSet { Name = "закрыть", Rules = [new RuleDraft { Action = FirewallAction.Deny, Port = "22" }] };
            TestSuite.True(Ufw.CheckAfter(s, FirewallRuleSets.Plan(s, closing, true).Commands).Allowed == false, "a set that would cut this SSH connection is predicted as such");
        });
        await t.Run("ufw: the safe-apply script saves state, schedules the restore, restores on a failing command; odd commands are refused", () =>
        {
            var script = FirewallScripts.SafeApply("abc123", ["ufw allow in proto tcp from 'any' to any port '80'", Ufw.EnableCommand], 90);
            TestSuite.True(script.Contains("cp -p \"$f\" \"$B/$(basename \"$f\")\"", StringComparison.Ordinal) && script.Contains("systemd-run --quiet --unit='sshx-ufw-rollback-abc123' --on-active=90", StringComparison.Ordinal)
                && script.Contains("sleep 90", StringComparison.Ordinal) && script.Contains("ufw --force enable 2>&1 || { echo '#failed'; sh \"$B/rollback.sh\"; exit 91; }", StringComparison.Ordinal)
                && script.IndexOf("rollback.sh\" >/dev/null", StringComparison.Ordinal) < script.IndexOf("ufw allow in", StringComparison.Ordinal), "order: save, schedule, then change");
            TestSuite.Throws<ArgumentException>(() => FirewallScripts.SafeApply("abc", ["ufw allow 22; reboot"], 90));
            TestSuite.Throws<ArgumentException>(() => FirewallScripts.SafeApply("abc", ["rm -rf /"], 90));
            TestSuite.Throws<ArgumentException>(() => FirewallScripts.SafeApply("a/b", ["ufw allow 22"], 90));
            TestSuite.True(FirewallScripts.Confirm("abc").Contains("rm -rf '/var/tmp/sshx-ufw-abc'", StringComparison.Ordinal) && FirewallScripts.RollbackNow("abc").Contains("sh '/var/tmp/sshx-ufw-abc/rollback.sh'", StringComparison.Ordinal), "confirm and restore");
        });
    }
}
#endif
