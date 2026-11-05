#if CORE_TESTS
using SSHExplorer.Core;

/// <summary>0.3 C1–C4: archives, content search, systemd parsing and commands, log following.</summary>
internal static class GroupCTests
{
    public static async Task RunAsync(TestSuite t)
    {
        await t.Run("archives: formats are detected by name, base names and suggestions keep the stem", () =>
        {
            TestSuite.True(Archives.Detect("a.TGZ") == ArchiveFormat.TarGz && Archives.Detect("a.tar.xz") == ArchiveFormat.TarXz && Archives.Detect("a.tbz2") == ArchiveFormat.TarBz2
                && Archives.Detect("a.tar") == ArchiveFormat.Tar && Archives.Detect("a.zip") == ArchiveFormat.Zip && Archives.Detect("a.gz") is null && Archives.Detect("a.txt") is null, "detect");
            TestSuite.Equal("site", Archives.BaseName("site.tar.gz")); TestSuite.Equal("v1.2", Archives.BaseName("v1.2.zip")); TestSuite.Equal(".tar", Archives.BaseName(".tar"));
            TestSuite.Equal("logs.tar.gz", Archives.SuggestName("/var", ["logs"], ArchiveFormat.TarGz));
            TestSuite.Equal("www.zip", Archives.SuggestName("/srv/www", ["a", "b"], ArchiveFormat.Zip));
            TestSuite.True(Archives.ValidateName("a/b") is not null && Archives.ValidateName(" ") is not null && Archives.ValidateName("..") is not null && Archives.ValidateName("ok.zip") is null, "names");
        });
        await t.Run("archives: members go in as quoted ./names, so a leading dash or a quote is never an option or shell text", () =>
        {
            var command = Archives.CreateCommand("/srv/my site", ["-rf", "it's"], "out.tar.gz", ArchiveFormat.TarGz, ".sshexplorer-archive-x.part");
            TestSuite.True(command.Contains("tar -czf '.sshexplorer-archive-x.part' './-rf' './it'\"'\"'s'", StringComparison.Ordinal) && command.StartsWith("cd '/srv/my site' || exit 2;", StringComparison.Ordinal)
                && command.Contains("exit 17", StringComparison.Ordinal), command);
            TestSuite.True(Archives.CreateCommand("/p", ["a"], "a.zip", ArchiveFormat.Zip, "t.part").Contains("zip -r -q -y 't.part' './a'", StringComparison.Ordinal), "zip keeps links as links");
            TestSuite.Throws<ArgumentException>(() => Archives.CreateCommand("/p", ["a/b"], "x.tar", ArchiveFormat.Tar, "t"));
            TestSuite.Throws<ArgumentException>(() => Archives.CreateCommand("/p", [], "x.tar", ArchiveFormat.Tar, "t"));
        });
        await t.Run("archives: tools per format, unsafe entries, listing cap, extraction never replaces in either mode", () =>
        {
            var tools = Archives.ParseTools("tar gzip unzip \n");
            TestSuite.True(Archives.Missing(ArchiveFormat.TarGz, true, tools) is null && Archives.Missing(ArchiveFormat.TarXz, true, tools) == "xz" && Archives.Missing(ArchiveFormat.Zip, true, tools) == "zip" && Archives.Missing(ArchiveFormat.Zip, false, tools) is null, "tools");
            var listing = Archives.ParseListing("a/\na/b.txt\n../evil\n/etc/passwd\nok/../../x\n");
            TestSuite.True(listing.Entries.Count == 5 && listing.Unsafe.SequenceEqual(["../evil", "/etc/passwd", "ok/../../x"]) && !listing.Truncated, string.Join(",", listing.Unsafe));
            var many = Archives.ParseListing(string.Join("\n", Enumerable.Range(0, Archives.ListLimit + 1).Select(i => "f" + i)));
            TestSuite.True(many.Truncated && many.Entries.Count == Archives.ListLimit, "cap");
            var fresh = Archives.ExtractCommand("/p/a.tar.gz", ArchiveFormat.TarGz, "/p/a", ExtractMode.NewFolder);
            TestSuite.True(fresh.Contains("exit 17", StringComparison.Ordinal) && fresh.Contains("--no-same-owner", StringComparison.Ordinal) && !fresh.Contains("--skip-old-files", StringComparison.Ordinal), fresh);
            TestSuite.True(Archives.ExtractCommand("/p/a.tar", ArchiveFormat.Tar, "/p", ExtractMode.HereKeepExisting).Contains("--skip-old-files", StringComparison.Ordinal)
                && Archives.ExtractCommand("/p/a.zip", ArchiveFormat.Zip, "/p", ExtractMode.HereKeepExisting).Contains("unzip -q -n", StringComparison.Ordinal), "keep existing");
        });
        await t.Run("content search: grep gets -e with the quoted pattern, fixed or extended, masks and skipped folders", () =>
        {
            var command = ContentSearch.Command("/etc", new ContentSearchOptions { Pattern = "-x 'y'", Include = "*.conf, *.yml", IgnoreCase = false });
            TestSuite.True(command.Contains("-e '-x '\"'\"'y'\"'\"''", StringComparison.Ordinal) && command.Contains(" -F", StringComparison.Ordinal) && !command.Contains(" -i", StringComparison.Ordinal)
                && command.Contains("--include='*.conf' --include='*.yml'", StringComparison.Ordinal) && command.Contains("--exclude-dir='.git'", StringComparison.Ordinal) && command.Contains("-- .", StringComparison.Ordinal), command);
            var regex = ContentSearch.Command("/", new ContentSearchOptions { Pattern = "a+", Regex = true, WholeWord = true, SkipCommonFolders = false });
            TestSuite.True(regex.Contains(" -E", StringComparison.Ordinal) && regex.Contains(" -w", StringComparison.Ordinal) && !regex.Contains("--exclude-dir", StringComparison.Ordinal), regex);
            TestSuite.True(ContentSearch.Validate(new ContentSearchOptions()) is not null && ContentSearch.Validate(new ContentSearchOptions { Pattern = "a\nb" }) is not null && ContentSearch.Validate(new ContentSearchOptions { Pattern = "a", Include = "x/*.c" }) is not null, "validate");
        });
        await t.Run("content search: NUL-separated output keeps colons in names; long lines are shortened; a grep error is reported", () =>
        {
            var result = ContentSearch.Parse("./dir/a:b.txt\u000012:key: value\n./c.conf\u00003:\tx\n" + "./long\u00001:" + new string('z', 500) + "\nnoise\n", "");
            TestSuite.True(result.Matches.Count == 3 && result.Matches[0] is { Path: "dir/a:b.txt", Line: 12, Text: "key: value" } && result.Matches[1].Text == " x" && result.Matches[2].Text.Length == ContentSearch.LineLimit + 1 && result.Error is null, string.Join(" | ", result.Matches));
            var bad = ContentSearch.Parse("", "grep: Unmatched ( or \\(\n");
            TestSuite.True(bad.Error == "grep: Unmatched ( or \\(", bad.Error ?? "null");
            TestSuite.True(ContentSearch.Parse("", "grep: ./x: Permission denied\n").Error is null, "permission noise is not an error");
        });
        await t.Run("systemd: units and unit files are merged; templates skipped; failed first; Russian states", () =>
        {
            var output = "#uid 1000\n#sudo\n#units\nnginx.service loaded active running A high performance web server\ncron.service loaded failed failed Regular background program processing daemon\n" +
                         "ssh.service loaded inactive dead OpenBSD Secure Shell server\n#files\nnginx.service enabled enabled\ncron.service enabled enabled\nssh.service disabled enabled\ngetty@.service enabled enabled\nunused.service disabled enabled\n";
            var s = Systemd.Parse(output);
            TestSuite.True(s.Available && !s.IsRoot && s.Sudo && s.Units.Select(u => u.Name).SequenceEqual(["cron.service", "nginx.service", "ssh.service", "unused.service"]), string.Join(",", s.Units.Select(u => u.Name)));
            var nginx = s.Units.Single(u => u.Name == "nginx.service");
            TestSuite.True(nginx is { StateText: "Работает", StartupText: "Автозапуск", Description: "A high performance web server" } && s.Units[0].StateText == "Сбой" && s.Units.Single(u => u.Name == "unused.service") is { StateText: "Остановлена", StartupText: "Вручную" }, nginx.ToString());
            TestSuite.True(!Systemd.Parse("#none\n").Available, "no systemd");
        });
        await t.Run("systemd: actions are quoted, elevated only when allowed, and never accept a malformed unit name", () =>
        {
            TestSuite.Equal("LC_ALL=C sudo -n systemctl --no-ask-password restart -- 'nginx.service' 2>&1", Systemd.ActionCommand("nginx.service", ServiceAction.Restart, false, true));
            TestSuite.Equal("LC_ALL=C systemctl --no-ask-password stop -- 'a@b.service' 2>&1", Systemd.ActionCommand("a@b.service", ServiceAction.Stop, true, true));
            TestSuite.Throws<ArgumentException>(() => Systemd.ActionCommand("x; reboot", ServiceAction.Start, true, false));
            TestSuite.Throws<ArgumentException>(() => Systemd.ActionCommand("nginx", ServiceAction.Start, true, false));
            TestSuite.True(Systemd.MayDropConnection("ssh.service", ServiceAction.Stop) && !Systemd.MayDropConnection("ssh.service", ServiceAction.Reload) && !Systemd.MayDropConnection("nginx.service", ServiceAction.Stop), "ssh warning");
            TestSuite.True(Systemd.JournalCommand("nginx.service", 200, true, false, false).Contains("journalctl --no-pager -o short-iso -n 200 -f -u 'nginx.service'", StringComparison.Ordinal), "journal");
            TestSuite.True(Systemd.ParseShow("MainPID=42\nActiveState=active\nbroken\n") is { Count: 2 } map && map["MainPID"] == "42", "show");
        });
        await t.Run("logs: text and /regex/ filters, a bad expression is explained, the buffer drops the oldest lines", () =>
        {
            TestSuite.True(LogFollow.Filter("ERROR", out _)!("an error here") && !LogFollow.Filter("ERROR", out _)!("fine"), "text filter is case-insensitive");
            var regex = LogFollow.Filter("/^\\d{4}-.*fail/", out var e1); TestSuite.True(e1 is null && regex!("2026-11-05 x failed") && !regex!("x 2026 fail"), "regex");
            TestSuite.True(LogFollow.Filter("/(/", out var e2) is null && e2 is not null && LogFollow.Filter("  ", out _) is null, "bad regex / empty");
            var buffer = new LogBuffer(3); foreach (var l in new[] { "1", "2", "3", "4", "5" }) buffer.Add(l);
            TestSuite.True(buffer.Lines.SequenceEqual(["3", "4", "5"]) && buffer.Dropped == 2 && buffer.Matching(l => l != "4").SequenceEqual(["3", "5"]), "buffer");
            var wrap = LogFollow.Wrap("tail -n 5 -F -- 'x' 2>&1");
            TestSuite.True(wrap.StartsWith("exec 3<&0; tail -n 5 -F -- 'x' 2>&1 & p=$!;", StringComparison.Ordinal) && wrap.EndsWith("exit $rc", StringComparison.Ordinal), wrap);
        });
    }
}
#endif
