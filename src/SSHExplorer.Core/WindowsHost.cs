using System.Text;

namespace SSHExplorer.Core;

/// <summary>
/// Windows servers reached over SSH (OpenSSH Server). Their default shell is cmd.exe or PowerShell; every command the
/// application sends is one PowerShell invocation with an encoded script, which both shells run the same way. Nothing is
/// installed on the server.
/// </summary>
public static class WindowsHost
{
    /// <summary>Asked only after the POSIX probe failed: «ver» exists in cmd.exe on every Windows.</summary>
    public const string DetectCommand = "cmd /c ver";

    public static bool LooksLikeWindows(CommandResult result) => result.ExitStatus == 0 && result.Output.Contains("Windows", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A PowerShell script as a single command line. Output is UTF-8 and errors stop the script; the command line stays
    /// under cmd.exe's 8191-character limit for scripts up to about 2.5 KB.
    /// </summary>
    public static string PowerShell(string script)
    {
        var full = "$ProgressPreference='SilentlyContinue';$ErrorActionPreference='Stop';[Console]::OutputEncoding=[Text.Encoding]::UTF8;" + script;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        if (encoded.Length > 8000) throw new ArgumentException("Сценарий PowerShell слишком длинный для командной строки Windows.", nameof(script));
        return "powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded;
    }

    /// <summary>
    /// PowerShell started with -EncodedCommand reports errors on stderr as CLIXML; only the error texts are kept.
    /// </summary>
    public static string CleanError(string error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "";
        if (!error.TrimStart().StartsWith("#< CLIXML", StringComparison.Ordinal)) return (char)10 + error.Trim();
        var texts = System.Text.RegularExpressions.Regex.Matches(error, "<S S=\"Error\">(.*?)</S>").Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Replace("_x000D__x000A_", " ").Trim()).Where(t => t.Length > 0);
        var joined = string.Join(" ", texts);
        return joined.Length == 0 ? "" : (char)10 + joined;
    }

    /// <summary>An SFTP path of Win32-OpenSSH: «/C:/…».</summary>
    public static bool IsWindowsPath(string path) => path.Length >= 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == ':';

    /// <summary>A PowerShell single-quoted literal.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>Win32-OpenSSH SFTP paths look like «/C:/Users/admin»; people read «C:\Users\admin».</summary>
    public static string ToDisplay(string sftpPath)
    {
        if (sftpPath is "/" or "") return "Этот компьютер";
        var p = sftpPath.StartsWith('/') && sftpPath.Length >= 3 && sftpPath[2] == ':' ? sftpPath[1..] : sftpPath;
        p = p.Replace('/', '\\');
        return p.Length == 2 && p[1] == ':' ? p + "\\" : p;
    }

    /// <summary>«C:\Users», «c:/users», «/C:/Users» → «/C:/Users»; anything else is returned unchanged.</summary>
    public static string FromDisplay(string text)
    {
        var t = text.Trim().Trim('"');
        if (t is "Этот компьютер") return "/";
        if (t.Length >= 2 && char.IsAsciiLetter(t[0]) && t[1] == ':')
        {
            var rest = t[2..].Replace('\\', '/').TrimEnd('/');
            return "/" + char.ToUpperInvariant(t[0]) + ":" + (rest.Length == 0 ? "/" : rest.StartsWith('/') ? rest : "/" + rest);
        }
        return t.Replace('\\', '/');
    }
}
