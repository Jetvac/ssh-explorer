using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>The app-wide update check: at most once a day at start, or on request in «О приложении».</summary>
public static class Updates
{
    static readonly System.Net.Http.HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
    /// <summary>Test hook: replaces the GitHub request.</summary>
    internal static Func<CancellationToken, Task<ReleaseInfo?>>? FetchOverride { get; set; }
    public static ReleaseInfo? Latest { get; private set; }
    public static event EventHandler? Found;

    /// <summary>The newer release when there is one; null when this build is current or the check is not due.</summary>
    public static async Task<ReleaseInfo?> CheckAsync(bool force, CancellationToken ct = default)
    {
        var settings = App.Store.Settings;
        if (!force && (!settings.CheckUpdates || settings.LastUpdateCheckUtc is { } last && DateTime.UtcNow - last < TimeSpan.FromHours(20))) return Newer(settings.LatestKnownVersion);
        var latest = FetchOverride is not null ? await FetchOverride(ct) : await UpdateCheck.LatestAsync(http, BuildInfo.DisplayVersion, ct);
        settings.LastUpdateCheckUtc = DateTime.UtcNow;
        if (latest is not null) { Latest = latest; settings.LatestKnownVersion = latest.Version; }
        try { await App.Store.SaveAsync(ct); } catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
        var newer = latest is not null && UpdateCheck.IsNewer(latest, BuildInfo.DisplayVersion) ? latest : null;
        if (newer is not null) Found?.Invoke(null, EventArgs.Empty);
        return newer;
    }

    static ReleaseInfo? Newer(string? known) => known is not null && UpdateCheck.Compare(known, BuildInfo.DisplayVersion) > 0 ? Latest ?? new ReleaseInfo(known, "https://github.com/Jetvac/ssh-explorer/releases/latest", null) : null;

    public static void OpenPage(ReleaseInfo release)
    {
        if (!release.Url.StartsWith("https://github.com/", StringComparison.Ordinal)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(release.Url) { UseShellExecute = true })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception ex) { Ui.Error("Не удалось открыть страницу выпуска: " + ex.Message); }
    }
}
