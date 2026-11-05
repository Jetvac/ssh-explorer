using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SSHExplorer.Core;

/// <summary>
/// Windows services and the Event Log in the «Службы» page, shown with the same states as systemd units:
/// running → «Работает», stopped → «Остановлена», automatic → «Автозапуск», manual → «Вручную», disabled → «Заблокирована».
/// </summary>
public static partial class WindowsServices
{
    public static string ListCommand => WindowsHost.PowerShell(
        "$ErrorActionPreference='SilentlyContinue';$id=[Security.Principal.WindowsIdentity]::GetCurrent();" +
        "$a=([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator);" +
        "$s=@(Get-CimInstance Win32_Service|%{[ordered]@{n=$_.Name;d=$_.DisplayName;st=$_.State;m=$_.StartMode;x=$_.ExitCode}});" +
        "[ordered]@{admin=$a;s=$s}|ConvertTo-Json -Depth 3 -Compress");

    public static ServicesSnapshot Parse(string output)
    {
        int start = output.IndexOf('{');
        if (start < 0) return new ServicesSnapshot([], false, false, false, "PowerShell не вернул список служб Windows.");
        try
        {
            using var doc = JsonDocument.Parse(output[start..]); var r = doc.RootElement;
            bool admin = r.TryGetProperty("admin", out var a) && a.ValueKind == JsonValueKind.True;
            var units = new List<ServiceUnit>();
            if (r.TryGetProperty("s", out var list))
                foreach (var s in list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : Enumerable.Empty<JsonElement>())
                {
                    string S(string n) => s.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    var state = S("st"); var mode = S("m");
                    int exit = s.TryGetProperty("x", out var x) && x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var code) ? code : 0;
                    // An automatic service that stopped with an error code is what systemd calls «failed».
                    var active = state switch { "Running" => "active", "Start Pending" => "activating", "Stop Pending" => "deactivating", _ => mode == "Auto" && exit != 0 && exit != 1077 ? "failed" : "inactive" };
                    var sub = state switch { "Running" => "running", "Paused" => "paused", _ => "dead" };
                    var file = mode switch { "Auto" => "enabled", "Manual" => "disabled", "Disabled" => "masked", "Boot" or "System" => "static", _ => mode };
                    units.Add(new ServiceUnit(S("n"), "loaded", active, sub, file, S("d")));
                }
            return new ServicesSnapshot(units.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToArray(), true, admin, false, null);
        }
        catch (JsonException ex) { return new ServicesSnapshot([], false, false, false, "Ответ PowerShell повреждён: " + ex.Message); }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.\-@$ ]{1,256}$")] private static partial Regex NameRegex();
    public static bool IsValidName(string name) => NameRegex().IsMatch(name) && name.Trim().Length > 0;

    public static string ActionCommand(string name, ServiceAction action)
    {
        if (!IsValidName(name)) throw new ArgumentException("Недопустимое имя службы: " + name, nameof(name));
        var q = WindowsHost.Quote(name);
        return WindowsHost.PowerShell(action switch
        {
            ServiceAction.Start => $"Start-Service -Name {q}",
            ServiceAction.Stop => $"Stop-Service -Name {q} -Force",
            ServiceAction.Restart => $"Restart-Service -Name {q} -Force",
            ServiceAction.Enable => $"Set-Service -Name {q} -StartupType Automatic",
            ServiceAction.Disable => $"Set-Service -Name {q} -StartupType Manual",
            _ => throw new NotSupportedException("У служб Windows нет «перечитать настройки»: используйте «Перезапустить»."),
        } + ";'ok'");
    }

    public static string ShowCommand(string name) => WindowsHost.PowerShell(
        $"$s=Get-CimInstance Win32_Service -Filter \"Name='{name.Replace("'", "''").Replace("\"", "")}'\";" +
        "if($s){'Description='+$s.Description;'ExecStart='+$s.PathName;'User='+$s.StartName;'MainPID='+$s.ProcessId;'ExitCode='+$s.ExitCode;'StartMode='+$s.StartMode}");

    public static IReadOnlyDictionary<string, string> ParseShow(string output) => Systemd.ParseShow(output);

    /// <summary>
    /// Events of the service from the System log («Service Control Manager» and the service's own source), oldest
    /// first; with <paramref name="follow"/> new events are printed as they arrive (polled every 2 s).
    /// </summary>
    public static string EventsCommand(string name, string displayName, int lines, bool follow)
    {
        var n = WindowsHost.Quote(name); var d = WindowsHost.Quote(displayName.Length > 0 ? displayName : name);
        int count = Math.Clamp(lines, 10, 2000);
        return WindowsHost.PowerShell(
            "$ErrorActionPreference='SilentlyContinue';" +
            $"function M($e){{$e.Message -like ('*'+{d}+'*') -or $e.ProviderName -eq {n}}};" +
            "function P($e){$e.TimeCreated.ToString('yyyy-MM-dd HH:mm:ss')+' '+$e.LevelDisplayName+' '+$e.ProviderName+': '+(($e.Message -split \"`r?`n\")[0])};" +
            $"$ev=@(Get-WinEvent -LogName System -MaxEvents 3000|?{{M $_}}|Select-Object -First {count.ToString(CultureInfo.InvariantCulture)});[array]::Reverse($ev);$ev|%{{P $_}};" +
            "$last=0;if($ev.Count){$last=$ev[-1].RecordId}else{$f=Get-WinEvent -LogName System -MaxEvents 1;if($f){$last=$f.RecordId}};" +
            (follow ? "while($true){Start-Sleep 2;$new=@(Get-WinEvent -LogName System -MaxEvents 200|?{$_.RecordId -gt $last});[array]::Reverse($new);foreach($e in $new){$last=$e.RecordId;if(M $e){P $e}}}" : ""));
    }

    /// <summary>A text file on a Windows server: the last lines and, with <paramref name="follow"/>, new ones.</summary>
    public static string TailCommand(string sftpPath, int lines, bool follow) => WindowsHost.PowerShell(
        $"Get-Content -LiteralPath {WindowsHost.Quote(WindowsHost.ToDisplay(sftpPath))} -Tail {Math.Clamp(lines, 1, 100000).ToString(CultureInfo.InvariantCulture)} -Encoding UTF8" + (follow ? " -Wait" : ""));
}
