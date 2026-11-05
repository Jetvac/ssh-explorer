using System.Globalization;
using System.Text.Json;

namespace SSHExplorer.Core;

/// <summary>The latest published release.</summary>
public sealed record ReleaseInfo(string Version, string Url, DateTime? Published);

/// <summary>
/// Asks GitHub which release is the latest and compares it with this build. Nothing is downloaded or installed:
/// the app only says that a newer version exists and links to its page. No personal data is sent.
/// </summary>
public static class UpdateCheck
{
    public const string LatestUrl = "https://api.github.com/repos/Jetvac/ssh-explorer/releases/latest";

    public static ReleaseInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag_name", out var tag) || tag.GetString() is not { Length: > 0 } name) return null;
            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean() || root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
            var url = root.TryGetProperty("html_url", out var u) && u.GetString() is { } s && s.StartsWith("https://github.com/", StringComparison.Ordinal) ? s : "https://github.com/Jetvac/ssh-explorer/releases";
            DateTime? published = root.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String && DateTime.TryParse(p.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
            return new ReleaseInfo(name.TrimStart('v', 'V'), url, published);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return null; }
    }

    /// <summary>Semantic version order: 0.4.0-preview.1 &lt; 0.4.0 &lt; 0.4.1. Build metadata (+commit) is ignored.</summary>
    public static int Compare(string a, string b)
    {
        static (int[] Core, string? Pre) Split(string v)
        {
            v = v.Trim().TrimStart('v', 'V'); var plus = v.IndexOf('+'); if (plus >= 0) v = v[..plus];
            var dash = v.IndexOf('-'); var pre = dash >= 0 ? v[(dash + 1)..] : null; var core = dash >= 0 ? v[..dash] : v;
            return (core.Split('.').Select(x => int.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0).Concat([0, 0, 0]).Take(3).ToArray(), pre);
        }
        var (ca, pa) = Split(a); var (cb, pb) = Split(b);
        for (int i = 0; i < 3; i++) if (ca[i] != cb[i]) return ca[i].CompareTo(cb[i]);
        if (pa is null || pb is null) return pa is null ? (pb is null ? 0 : 1) : -1;
        var xa = pa.Split('.'); var xb = pb.Split('.');
        for (int i = 0; i < Math.Min(xa.Length, xb.Length); i++)
        {
            bool na = int.TryParse(xa[i], out var ia), nb = int.TryParse(xb[i], out var ib);
            int c = na && nb ? ia.CompareTo(ib) : na ? -1 : nb ? 1 : string.CompareOrdinal(xa[i], xb[i]);
            if (c != 0) return c;
        }
        return xa.Length.CompareTo(xb.Length);
    }

    public static bool IsNewer(ReleaseInfo latest, string current) => Compare(latest.Version, current) > 0;

    public static async Task<ReleaseInfo?> LatestAsync(HttpClient http, string userAgentVersion, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
        request.Headers.UserAgent.ParseAdd("SSHExplorer/" + userAgentVersion.Split('+')[0]);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"GitHub ответил {(int)response.StatusCode}.");
        return Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }
}
