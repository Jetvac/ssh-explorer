using System.ComponentModel;
using System.Globalization;

namespace SSHExplorer.Core;

/// <summary>Stored by name; Agent is appended so existing numeric indexes keep their meaning.</summary>
public enum AuthenticationKind { Password, PrivateKey, KeyboardInteractive, Agent }
public sealed class ServerProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";
    public AuthenticationKind Authentication { get; set; }
    public string PrivateKeyPath { get; set; } = "";
    public string InitialPath { get; set; } = "~";
    public int TimeoutSeconds { get; set; } = 20;
    public bool SaveSecret { get; set; }
    public List<string> Bookmarks { get; set; } = [];
    /// <summary>OpenSSH-style jump host chain: "[user@]host[:port]" or a profile name, comma separated (empty — direct).</summary>
    public string ProxyJump { get; set; } = "";
    /// <summary>Optional group on the Home list (empty — ungrouped).</summary>
    public string Group { get; set; } = "";
    public string Endpoint => $"{(Host.Contains(':') ? $"[{Host}]" : Host)}:{Port}";
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Username))
            throw new ArgumentException("Укажите название, адрес сервера и пользователя.");
        if (Host.Any(char.IsWhiteSpace) || Host.IndexOfAny(['/', '\\', '\0']) >= 0)
            throw new ArgumentException("Адрес должен быть DNS-именем, IPv4 или IPv6 без схемы и пути.");
        if (Port is < 1 or > 65535) throw new ArgumentException("Порт должен быть от 1 до 65535.");
        if (TimeoutSeconds is < 3 or > 120) throw new ArgumentException("Время ожидания должно быть от 3 до 120 секунд.");
        if (Authentication == AuthenticationKind.PrivateKey && !File.Exists(PrivateKeyPath))
            throw new FileNotFoundException("Приватный ключ не найден.", PrivateKeyPath);
    }
}
public sealed class ColumnSetting
{
    public string Key { get; set; } = "";
    public double Width { get; set; } = 140;
    public int Order { get; set; }
    public bool Visible { get; set; } = true;
}
public sealed class AppSettings
{
    public string ExternalEditorPath { get; set; } = "";
    public string Theme { get; set; } = "System";
    public string ViewMode { get; set; } = "Details";
    public double TreeWidth { get; set; } = 244;
    public int PollIntervalSeconds { get; set; } = 2;
    public int TerminalFontSize { get; set; } = 14;
    public bool ShowHidden { get; set; }
    public bool ShowExtensions { get; set; } = true;
    public bool DetailsPane { get; set; }
    public bool PreviewPane { get; set; }
    public long PreviewMaxBytes { get; set; } = 20 * 1024 * 1024;
    public long PreviewMaxPixels { get; set; } = 40_000_000;
    public int PreviewParallelism { get; set; } = 2;
    public long PreviewCacheBytes { get; set; } = 128 * 1024 * 1024;
    public List<ColumnSetting> Columns { get; set; } = [];
    public MonitorPreferences Monitor { get; set; } = new();
    /// <summary>File table order (header and "Сортировка" menu share it).</summary>
    public string FileSortKey { get; set; } = "Name";
    public bool FileSortDescending { get; set; }
    /// <summary>Header-click order of the other tables, one entry per table.</summary>
    public List<TableSortSetting> TableSorts { get; set; } = [];
    public bool ReduceMotion { get; set; }
}
public sealed class TableSortSetting
{
    public string Table { get; set; } = "";
    public string Column { get; set; } = "";
    public bool Descending { get; set; }
}
public sealed record InteractivePrompt(string Instruction, string Prompt, bool Echo);
public sealed record SessionCredentials(string Secret, Func<InteractivePrompt, Task<string?>>? Prompt = null,
    Func<InteractivePrompt, CancellationToken, Task<string?>>? CancellablePrompt = null)
{
    public override string ToString() => "SessionCredentials { Secret = [redacted] }";
}
public enum TrustDecision { Reject, Session, Permanent }
public sealed record HostKeyPrompt(string Host, int Port, string Algorithm, string Fingerprint, string? PreviousFingerprint, bool Changed);
public enum SessionState { Connecting, AwaitingTrust, Connected, Disconnected, Closing, Closed }
public sealed record CommandResult(string Output, string Error, int ExitStatus);
public sealed class HostKeyChangedException(HostKeyPrompt prompt) : Exception($"Ключ сервера {prompt.Host}:{prompt.Port} изменён. Соединение заблокировано. Ранее: {prompt.PreviousFingerprint}; сейчас: {prompt.Fingerprint}.")
{ public HostKeyPrompt Prompt { get; } = prompt; }
public sealed class RemoteEntry
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsDirectory { get; init; }
    public bool IsSymbolicLink { get; init; }
    public bool IsRegularFile { get; init; }
    public long Length { get; init; }
    public DateTime LastWriteTimeUtc { get; init; }
    public DateTime LastAccessTimeUtc { get; init; }
    public int UserId { get; init; }
    public int GroupId { get; init; }
    public short Permissions { get; init; }
    public string? LinkTarget { get; init; }
    public string TypeName => IsSymbolicLink ? "Символическая ссылка" : IsDirectory ? "Папка" : IsRegularFile ? (System.IO.Path.GetExtension(Name) is { Length: > 0 } ext ? $"Файл {ext.TrimStart('.').ToUpperInvariant()}" : "Файл") : "Специальный объект";
    public string SizeText => IsRegularFile && !IsSymbolicLink ? Length < 0 ? "Недоступно" : FormatBytes(Length) : "—";
    public string PermissionsText => Convert.ToString(Permissions & 0xfff, 8).PadLeft(4, '0');
    public static string FormatBytes(long value)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double size = value; int i = 0;
        while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
        return $"{size.ToString(i == 0 ? "N0" : "N1", CultureInfo.CurrentCulture)} {units[i]}";
    }
}
