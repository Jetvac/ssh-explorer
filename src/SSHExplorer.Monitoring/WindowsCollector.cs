using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SSHExplorer.Monitoring;

/// <summary>
/// Task manager data of a Windows server over SSH: one PowerShell call reads the performance counters (already
/// rates and percentages, so one sample is enough) and prints JSON. Nothing is installed or changed on the server.
/// </summary>
public static class WindowsCollector
{
    /// <summary>The same encoded-command form as <c>SSHExplorer.Core.WindowsHost.PowerShell</c> (no shared project).</summary>
    public static string PowerShell(string script, bool stopOnError = false)
    {
        var full = "$ProgressPreference='SilentlyContinue';$ErrorActionPreference='" + (stopOnError ? "Stop" : "SilentlyContinue") + "';[Console]::OutputEncoding=[Text.Encoding]::UTF8;" + script;
        return "powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
    }

    const string System = """
        $o=Get-CimInstance Win32_OperatingSystem;$cs=Get-CimInstance Win32_ComputerSystem
        $r=[ordered]@{v=1;cpu=(Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'").PercentProcessorTime;n=$cs.NumberOfLogicalProcessors;mt=[int64]$o.TotalVisibleMemorySize*1KB;mf=[int64]$o.FreePhysicalMemory*1KB;vt=[int64]$o.TotalVirtualMemorySize*1KB;vf=[int64]$o.FreeVirtualMemory*1KB;up=[int64]((Get-Date)-$o.LastBootUpTime).TotalSeconds}
        $r.d=@(Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3'|%{[ordered]@{m=$_.DeviceID;f=$_.FileSystem;s=[int64]$_.Size;a=[int64]$_.FreeSpace}})
        $r.net=@(Get-CimInstance Win32_PerfFormattedData_Tcpip_NetworkInterface|%{[ordered]@{n=$_.Name;rx=[int64]$_.BytesReceivedPersec;tx=[int64]$_.BytesSentPersec}})
        $r.io=@(Get-CimInstance Win32_PerfFormattedData_PerfDisk_PhysicalDisk|?{$_.Name -ne '_Total'}|%{[ordered]@{n=$_.Name;r=[int64]$_.DiskReadBytesPersec;w=[int64]$_.DiskWriteBytesPersec}})
        """;
    const string Processes = """
        $pc=@{};Get-CimInstance Win32_PerfFormattedData_PerfProc_Process|%{$pc[[int]$_.IDProcess]=$_.PercentProcessorTime}
        $ps=$null;try{$ps=Get-Process -IncludeUserName -ErrorAction Stop}catch{};if(-not $ps){$ps=Get-Process}
        $r.p=@($ps|Select-Object -First 2048|%{[ordered]@{i=$_.Id;n=$_.ProcessName;u=$_.UserName;m=[int64]$_.WorkingSet64;c=$pc[$_.Id];s=$(try{[int64]$_.StartTime.ToFileTimeUtc()}catch{0})}})
        """;

    public static string Command(bool processes) => PowerShell(System + (processes ? "\n" + Processes : "") + "\n$r|ConvertTo-Json -Depth 4 -Compress");

    public static MonitorSnapshot Parse(string output, DateTimeOffset timestamp)
    {
        int start = output.IndexOf('{'); if (start < 0) throw new FormatException("Нет данных мониторинга Windows.");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(output[start..]); }
        catch (JsonException ex) { throw new FormatException("Ответ PowerShell повреждён: " + ex.Message, ex); }
        using var owned = doc;
        var r = doc.RootElement;
        long? L(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var x) ? x : null;
        double? D(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        string S(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        IEnumerable<JsonElement> A(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : v.ValueKind == JsonValueKind.Object ? [v] : [];
        int cpus = (int)Math.Max(1, L(r, "n") ?? 1);
        long? total = L(r, "mt"), free = L(r, "mf"), vTotal = L(r, "vt"), vFree = L(r, "vf");
        long? used = total is { } t && free is { } f ? Math.Max(0, t - f) : null;
        long? swapTotal = vTotal is { } vt && total is { } t2 ? Math.Max(0, vt - t2) : null;
        long? swapUsed = vTotal is { } vt2 && vFree is { } vf && used is { } u ? Math.Max(0, vt2 - vf - u) : null;
        var system = new SystemMetric(Clamp(D(r, "cpu")), cpus, total, free, used, swapTotal, swapUsed, L(r, "up") is { } up ? TimeSpan.FromSeconds(up) : null, null, null, null, null, false);
        var unmeasured = new MetricStatus(MetricState.Unavailable, "Windows: чтение и запись по процессам не измеряются.");
        var processes = A("p").Select(p => new ProcessMetric(
            new ProcessIdentity((int)(L(p, "i") ?? 0), (ulong)Math.Max(0, L(p, "s") ?? 0), 0), S(p, "n"), S(p, "u"), "", Clamp(D(p, "c") / cpus), L(p, "m"), null, null, null)
            { ReadStatus = unmeasured, WriteStatus = unmeasured }).Where(p => p.Pid > 0).ToArray();
        var networks = A("net").Select(n => new NetworkMetric(S(n, "n"), 0, 0, D(n, "rx"), D(n, "tx"))).ToArray();
        var disks = A("io").Select(d => new DiskMetric(S(d, "n"), 0, 0, D(d, "r"), D(d, "w"))).ToArray();
        var storage = A("d").Select(d => { long size = L(d, "s") ?? 0, available = L(d, "a") ?? 0; return new StorageMetric(S(d, "f"), S(d, "m"), size, Math.Max(0, size - available), available); }).ToArray();
        return new MonitorSnapshot(timestamp, system, processes, networks, disks, storage, ["Windows: данные счётчиков производительности; процессы — Get-Process."]);
    }

    static double? Clamp(double? value) => value is { } v ? Math.Clamp(v, 0, 100) : null;

    /// <summary>Stops a process only if it is still the same one (PID and start time); 74 — it changed.</summary>
    public static string StopCommand(ProcessIdentity identity, bool force) => PowerShell(
        "$p=Get-Process -Id " + identity.Pid.ToString(CultureInfo.InvariantCulture) + " -ErrorAction SilentlyContinue;" +
        "if(-not $p){exit 74};$s=0;try{$s=[int64]$p.StartTime.ToFileTimeUtc()}catch{};if($s -ne " + identity.StartTimeTicks.ToString(CultureInfo.InvariantCulture) + "){exit 74};" +
        "try{Stop-Process -Id " + identity.Pid.ToString(CultureInfo.InvariantCulture) + (force ? " -Force" : "") + " -ErrorAction Stop}catch{exit 1}");

    /// <summary>System (4), Idle (0) and the session manager must never be stopped from here.</summary>
    public static string? ProtectedReason(ProcessIdentity identity) => identity.Pid is 0 or 4 ? "Системные процессы Windows (Idle, System) защищены." : null;
}
