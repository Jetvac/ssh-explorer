using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Renci.SshNet;

namespace SSHExplorer.Core;

public sealed record WindowsFirewallRule(string Id, string Name, string Group, bool Enabled, string Action, string Profile, string Protocol, string LocalPorts, string RemoteAddresses)
{
    public bool Allows => Action.Equals("Allow", StringComparison.OrdinalIgnoreCase);
    public bool Ours => Group == WindowsFirewall.Group;
    public string ActionText => Allows ? "Разрешить" : "Запретить";
    public string EnabledText => Enabled ? "Включено" : "Выключено";
    public string ProtocolText => Protocol is "" or "Any" ? "Любой" : Protocol;
    public string PortsText => LocalPorts is "" or "Any" ? "Любой" : LocalPorts;
    public string FromText => RemoteAddresses is "" or "Any" ? "Откуда угодно" : RemoteAddresses;
    public string ProfileText => Profile switch { "Any" or "" => "Все", _ => Profile.Replace("Domain", "Домен").Replace("Private", "Частная").Replace("Public", "Общая") };
    public string SearchText => $"{Name} {Group} {Protocol} {LocalPorts} {RemoteAddresses} {ActionText}";
}

public sealed record WindowsFirewallProfile(string Name, bool Enabled, string DefaultInbound);
public sealed record WindowsFirewallSnapshot(IReadOnlyList<WindowsFirewallProfile> Profiles, IReadOnlyList<WindowsFirewallRule> Rules, bool Admin);

/// <summary>A new inbound rule made in the app; it goes to the «SSH Explorer» group so it is easy to find and remove.</summary>
public sealed record WindowsFirewallNewRule(string Name, bool Allow, string Protocol, string Ports, string RemoteAddresses);

/// <summary>Windows Defender Firewall over PowerShell (NetSecurity), inbound rules.</summary>
public static partial class WindowsFirewall
{
    public const string Group = "SSH Explorer";
    public const int RollbackSeconds = 120;

    public static string ReadCommand => WindowsHost.PowerShell(
        "$ErrorActionPreference='SilentlyContinue';$id=[Security.Principal.WindowsIdentity]::GetCurrent();$adm=([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator);" +
        "$pf=@{};Get-NetFirewallPortFilter -All|%{$pf[$_.InstanceID]=$_};$af=@{};Get-NetFirewallAddressFilter -All|%{$af[$_.InstanceID]=$_};" +
        "$p=@(Get-NetFirewallProfile|%{[ordered]@{n=\"$($_.Name)\";e=\"$($_.Enabled)\";i=\"$($_.DefaultInboundAction)\"}});" +
        "$r=@(Get-NetFirewallRule -Direction Inbound|%{$f=$pf[$_.InstanceID];$a=$af[$_.InstanceID];[ordered]@{id=$_.Name;n=$_.DisplayName;g=\"$($_.Group)\";e=\"$($_.Enabled)\";a=\"$($_.Action)\";pr=\"$($_.Profile)\";p=\"$($f.Protocol)\";lp=(@($f.LocalPort) -join ',');ra=(@($a.RemoteAddress) -join ',')}});" +
        "[ordered]@{admin=$adm;profiles=$p;rules=$r}|ConvertTo-Json -Depth 4 -Compress");

    public static WindowsFirewallSnapshot Parse(string output)
    {
        int start = output.IndexOf('{'); if (start < 0) throw new FormatException("PowerShell не вернул правила брандмауэра.");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(output[start..]); } catch (JsonException ex) { throw new FormatException("Ответ PowerShell повреждён: " + ex.Message, ex); }
        using (doc)
        {
            var r = doc.RootElement;
            static string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            static IEnumerable<JsonElement> A(JsonElement e, string n) => e.TryGetProperty(n, out var v) ? v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : v.ValueKind == JsonValueKind.Object ? [v] : [] : [];
            var profiles = A(r, "profiles").Select(p => new WindowsFirewallProfile(S(p, "n"), S(p, "e") == "True", S(p, "i"))).ToArray();
            var rules = A(r, "rules").Select(x => new WindowsFirewallRule(S(x, "id"), S(x, "n"), S(x, "g"), S(x, "e") == "True", S(x, "a"), S(x, "pr"), S(x, "p"), S(x, "lp"), S(x, "ra")))
                .OrderByDescending(x => x.Enabled).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            return new WindowsFirewallSnapshot(profiles, rules, r.TryGetProperty("admin", out var adm) && adm.ValueKind == JsonValueKind.True);
        }
    }

    [GeneratedRegex(@"^\d{1,5}(-\d{1,5})?(,\d{1,5}(-\d{1,5})?)*$")] private static partial Regex PortsRegex();

    /// <summary>Null when the rule can be created; otherwise what to fix.</summary>
    public static string? Validate(WindowsFirewallNewRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Length > 120 || rule.Name.Any(char.IsControl)) return "Укажите название правила (до 120 символов).";
        if (rule.Protocol is not ("TCP" or "UDP" or "Any")) return "Протокол: TCP, UDP или любой.";
        var ports = rule.Ports.Replace(" ", "");
        if (rule.Protocol == "Any") { if (ports.Length > 0) return "Порт задаётся только для TCP или UDP."; }
        else if (ports.Length == 0 || !PortsRegex().IsMatch(ports) || ports.Split(',').SelectMany(p => p.Split('-')).Any(p => !int.TryParse(p, out var n) || n is < 1 or > 65535)) return "Порт: число 1–65535, список через запятую или диапазон 8000-8100.";
        foreach (var address in Addresses(rule.RemoteAddresses))
            if (!IsAddress(address)) return $"«{address}» — не IP-адрес и не подсеть (например, 203.0.113.10 или 10.0.0.0/8).";
        return null;
    }

    static IEnumerable<string> Addresses(string text) => text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(a => !a.Equals("Any", StringComparison.OrdinalIgnoreCase));
    static bool IsAddress(string a)
    {
        var parts = a.Split('/');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var ip)) return false;
        return parts.Length == 1 || int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var bits) && bits >= 0 && bits <= (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
    }

    public static string AddScript(WindowsFirewallNewRule rule)
    {
        if (Validate(rule) is { } problem) throw new ArgumentException(problem);
        var addresses = Addresses(rule.RemoteAddresses).ToArray();
        return $"New-NetFirewallRule -DisplayName {WindowsHost.Quote(rule.Name.Trim())} -Group {WindowsHost.Quote(Group)} -Direction Inbound -Action {(rule.Allow ? "Allow" : "Block")} -Protocol {rule.Protocol}"
            + (rule.Protocol == "Any" ? "" : " -LocalPort " + string.Join(",", rule.Ports.Replace(" ", "").Split(',').Select(p => WindowsHost.Quote(p))))
            + (addresses.Length == 0 ? "" : " -RemoteAddress " + string.Join(",", addresses.Select(WindowsHost.Quote))) + " | Out-Null";
    }

    public static string EnableScript(string id, bool enabled) => $"Set-NetFirewallRule -Name {WindowsHost.Quote(id)} -Enabled {(enabled ? "True" : "False")}";
    public static string RemoveScript(string id) => $"Remove-NetFirewallRule -Name {WindowsHost.Quote(id)}";

    static string Check(string id) => id.All(char.IsAsciiLetterOrDigit) && id.Length is > 0 and <= 32 ? id : throw new ArgumentException("id");
    static string Backup(string id) => "(Join-Path $env:ProgramData 'SSHExplorer\\fw-" + Check(id) + ".wfw')";
    static string TaskName(string id) => "'SSHExplorer-firewall-" + Check(id) + "'";

    /// <summary>
    /// Export the policy, schedule its import (as SYSTEM, in <paramref name="seconds"/>), apply the change. A change that
    /// throws restores the export at once. Prints «#applied» or «#failed».
    /// </summary>
    public static string SafeApply(string id, string change, int seconds) => WindowsHost.PowerShell(
        $"$b={Backup(id)};New-Item -ItemType Directory -Force (Split-Path $b)|Out-Null;" +
        "netsh advfirewall export $b|Out-Null;if(-not (Test-Path $b)){'#failed';'Не удалось сохранить правила (нужны права администратора).';exit 1};" +
        "$act=New-ScheduledTaskAction -Execute 'netsh.exe' -Argument ('advfirewall import \"'+$b+'\"');" +
        $"Register-ScheduledTask -TaskName {TaskName(id)} -Action $act -Trigger (New-ScheduledTaskTrigger -Once -At (Get-Date).AddSeconds({seconds.ToString(CultureInfo.InvariantCulture)})) -User 'SYSTEM' -RunLevel Highest -Force|Out-Null;" +
        $"try{{{change};'#applied'}}catch{{netsh advfirewall import $b|Out-Null;Unregister-ScheduledTask -TaskName {TaskName(id)} -Confirm:$false;'#failed';$_.Exception.Message}}");

    public static string Confirm(string id) => WindowsHost.PowerShell($"Unregister-ScheduledTask -TaskName {TaskName(id)} -Confirm:$false;Remove-Item {Backup(id)} -Force;'#confirmed'");
    public static string RollbackNow(string id) => WindowsHost.PowerShell($"$b={Backup(id)};netsh advfirewall import $b|Out-Null;Unregister-ScheduledTask -TaskName {TaskName(id)} -Confirm:$false -ErrorAction SilentlyContinue;Remove-Item $b -Force;'#rolledback'");
}

/// <summary>Reads and changes the Windows firewall with the same safety net as ufw: restore scheduled, checked by a new connection.</summary>
public sealed class WindowsFirewallService(ServerSession session)
{
    public ServerSession Session => session;
    internal Func<string, CancellationToken, Task<CommandResult>>? ExecuteOverride { get; set; }
    internal Func<CancellationToken, Task<bool>>? VerifyOverride { get; set; }
    Task<CommandResult> Exec(string command, CancellationToken ct) => ExecuteOverride?.Invoke(command, ct) ?? session.ExecuteLongAsync(command, TimeSpan.FromSeconds(90), ct);

    public async Task<WindowsFirewallSnapshot> ReadAsync(CancellationToken ct = default) => WindowsFirewall.Parse((await Exec(WindowsFirewall.ReadCommand, ct).ConfigureAwait(false)).Output);

    public async Task<ApplyResult> ApplyAsync(string change, Action<string>? progress = null, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N")[..16];
        progress?.Invoke("Сохранение текущих правил и задание автоматического возврата…");
        var run = await Exec(WindowsFirewall.SafeApply(id, change, WindowsFirewall.RollbackSeconds), ct).ConfigureAwait(false);
        var output = (run.Output + "\n" + run.Error).Trim();
        if (!run.Output.Contains("#applied", StringComparison.Ordinal))
            return new ApplyResult(ApplyOutcome.Failed, run.Output.Contains("#failed", StringComparison.Ordinal) ? "Windows отклонил изменение; прежние правила восстановлены." : $"Изменение не применено (код {run.ExitStatus}).", Clean(output));
        progress?.Invoke("Изменение применено. Проверка нового SSH-подключения…");
        bool reachable;
        try { reachable = await (VerifyOverride?.Invoke(ct) ?? VerifyAsync(ct)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { reachable = false; }
        if (reachable)
        {
            try { await Exec(WindowsFirewall.Confirm(id), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { return new ApplyResult(ApplyOutcome.Confirmed, $"Новое подключение работает, но отменить возврат не удалось ({ErrorMessages.Describe(ex)}): через {WindowsFirewall.RollbackSeconds} с правила вернутся к прежним.", Clean(output)); }
            return new ApplyResult(ApplyOutcome.Confirmed, "Применено и подтверждено: новое SSH-подключение проходит.", Clean(output));
        }
        progress?.Invoke("Новое подключение не прошло. Откат…");
        try
        {
            await Exec(WindowsFirewall.RollbackNow(id), CancellationToken.None).ConfigureAwait(false);
            return new ApplyResult(ApplyOutcome.RolledBack, "Новое SSH-подключение после изменения не прошло, поэтому прежние правила восстановлены.", Clean(output));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return new ApplyResult(ApplyOutcome.RolledBack, $"Новое подключение не прошло, а связь с сервером потеряна. Прежние правила вернёт запланированная задача не позже чем через {WindowsFirewall.RollbackSeconds} с.", Clean(output)); }
    }

    static string Clean(string output) => string.Join("\n", output.Split('\n').Where(l => !l.StartsWith('#') && !l.StartsWith('<')).Select(l => l.Trim()).Where(l => l.Length > 0));

    async Task<bool> VerifyAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(25));
        SshClient? client = null;
        try
        {
            client = await session.CreateSshClientAsync(timeout.Token).ConfigureAwait(false);
            using var cmd = client.CreateCommand(WindowsHost.DetectCommand); cmd.CommandTimeout = TimeSpan.FromSeconds(10);
            await cmd.ExecuteAsync(timeout.Token).ConfigureAwait(false);
            return cmd.ExitStatus == 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { return false; }
        finally { if (client is not null) session.ReleaseClient(client); }
    }
}
