using System.Text.Json;

namespace SSHExplorer.Monitoring;

/// <summary>
/// «Сервер» page of a Windows server: one read-only PowerShell call (CIM classes, Get-NetIPAddress). Restart and shut
/// down are offered only to an administrator; they use «shutdown.exe» with a short delay so the reply still arrives.
/// </summary>
public static class WindowsInfo
{
    public static string Command => WindowsCollector.PowerShell("""
        $o=Get-CimInstance Win32_OperatingSystem;$cs=Get-CimInstance Win32_ComputerSystem;$cpu=Get-CimInstance Win32_Processor|Select-Object -First 1
        $id=[Security.Principal.WindowsIdentity]::GetCurrent();$admin=([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        $r=[ordered]@{v=1;h=$env:COMPUTERNAME;os=$o.Caption;ver=$o.Version;build=$o.BuildNumber;arch=$o.OSArchitecture;cpu=$cpu.Name;n=$cs.NumberOfLogicalProcessors;mt=[int64]$o.TotalVisibleMemorySize*1KB;mf=[int64]$o.FreePhysicalMemory*1KB;vt=[int64]$o.TotalVirtualMemorySize*1KB;vf=[int64]$o.FreeVirtualMemory*1KB;up=[int64]((Get-Date)-$o.LastBootUpTime).TotalSeconds;boot=$o.LastBootUpTime.ToUniversalTime().ToString('o');user=$id.Name;admin=$admin}
        $r.d=@(Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3'|%{[ordered]@{m=$_.DeviceID;f=$_.FileSystem;s=[int64]$_.Size;a=[int64]$_.FreeSpace}})
        $r.ip=@(Get-NetIPAddress -ErrorAction SilentlyContinue|?{$_.IPAddress -notlike '127.*' -and $_.IPAddress -ne '::1' -and $_.IPAddress -notlike 'fe80*'}|%{[ordered]@{i=$_.InterfaceAlias;f=$(if($_.AddressFamily -eq 2){'IPv4'}else{'IPv6'});a=$_.IPAddress}})
        $r|ConvertTo-Json -Depth 4 -Compress
        """);

    public static ServerInfo Parse(string output, DateTimeOffset collectedAt)
    {
        int start = output.IndexOf('{'); if (start < 0) throw new FormatException("Нет сведений о сервере Windows.");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(output[start..]); }
        catch (JsonException ex) { throw new FormatException("Ответ PowerShell повреждён: " + ex.Message, ex); }
        using var owned = doc; var r = doc.RootElement;
        long? L(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var x) ? x : null;
        string? S(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        IEnumerable<JsonElement> A(string name) => r.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : v.ValueKind == JsonValueKind.Object ? [v] : [] : [];
        bool admin = r.TryGetProperty("admin", out var a) && a.ValueKind == JsonValueKind.True;
        long? total = L(r, "mt"), free = L(r, "mf"), vt = L(r, "vt"), vf = L(r, "vf");
        string why = "Перезагрузка и выключение Windows доступны только администратору (пользователь SSH не в группе «Администраторы»).";
        return new ServerInfo
        {
            Hostname = S(r, "h"), OsName = S(r, "os"), OsVersion = S(r, "ver"), OsId = "windows", Kernel = S(r, "build") is { } b ? "сборка " + b : null, Architecture = S(r, "arch"),
            CpuModel = S(r, "cpu")?.Trim(), CpuCount = (int?)L(r, "n"), MemoryTotalBytes = total, MemoryAvailableBytes = free,
            SwapTotalBytes = vt is { } t1 && total is { } t2 ? Math.Max(0, t1 - t2) : null,
            SwapUsedBytes = vt is { } v1 && vf is { } v2 && total is { } t3 && free is { } f3 ? Math.Max(0, v1 - v2 - (t3 - f3)) : null,
            Uptime = L(r, "up") is { } up ? TimeSpan.FromSeconds(up) : null, BootId = S(r, "boot"), User = S(r, "user"),
            Addresses = A("ip").Select(x => new NetworkAddress(S(x, "i") ?? "", S(x, "f") ?? "", S(x, "a") ?? "")).Where(x => x.Address.Length > 0).ToArray(),
            Storage = A("d").Select(d => { long size = L(d, "s") ?? 0, available = L(d, "a") ?? 0; return new StorageMetric(S(d, "f") ?? "", S(d, "m") ?? "", size, Math.Max(0, size - available), available); }).ToArray(),
            Reboot = admin ? new PowerCapability(PowerAction.Reboot, PowerMethod.WindowsShutdown, "shutdown /r /t 5 /c \"SSH Explorer\"", "shutdown /r от администратора Windows.") : new PowerCapability(PowerAction.Reboot, PowerMethod.None, "", why),
            PowerOff = admin ? new PowerCapability(PowerAction.PowerOff, PowerMethod.WindowsShutdown, "shutdown /s /t 5 /c \"SSH Explorer\"", "shutdown /s от администратора Windows.") : new PowerCapability(PowerAction.PowerOff, PowerMethod.None, "", why),
            Limitations = ["Windows: сведения из CIM (Win32_OperatingSystem и др.); средней нагрузки (load average) в Windows нет."],
            CollectedAt = collectedAt,
        };
    }
}
