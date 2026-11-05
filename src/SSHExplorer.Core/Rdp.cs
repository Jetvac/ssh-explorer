using System.Globalization;
using System.Text;

namespace SSHExplorer.Core;

public enum ConnectionProtocol { Ssh, Rdp }

public enum RdpAudio { ThisComputer, RemoteComputer, Off }

public enum RdpGatewayMode { Never, Always, Detect }

/// <summary>Remote desktop options of a profile. Device redirection is off by default except the clipboard.</summary>
public sealed class RdpOptions
{
    public string Domain { get; set; } = "";
    /// <summary>0 × 0 — the desktop follows the tab size; otherwise a fixed size scaled into the tab.</summary>
    public int Width { get; set; }
    public int Height { get; set; }
    public int ColorDepth { get; set; } = 32;
    public bool Clipboard { get; set; } = true;
    public bool Drives { get; set; }
    public bool Printers { get; set; }
    public bool SmartCards { get; set; }
    public bool Microphone { get; set; }
    public RdpAudio Audio { get; set; } = RdpAudio.ThisComputer;
    /// <summary>Network Level Authentication (CredSSP): the user signs in before a desktop is created.</summary>
    public bool Nla { get; set; } = true;
    /// <summary>The administrative (console) session, as «mstsc /admin».</summary>
    public bool AdminSession { get; set; }
    public RdpGatewayMode GatewayMode { get; set; }
    public string GatewayHost { get; set; } = "";
    public bool GatewaySameCredentials { get; set; } = true;
    public string GatewayUsername { get; set; } = "";
    public bool FitsTab => Width == 0 || Height == 0;
    public RdpOptions Clone() => (RdpOptions)MemberwiseClone();

    public static string? Validate(RdpOptions o)
    {
        if (!o.FitsTab && (o.Width is < 640 or > 8192 || o.Height is < 480 or > 8192)) return "Размер рабочего стола: от 640 × 480 до 8192 × 8192, или «По размеру вкладки».";
        if (o.ColorDepth is not (15 or 16 or 24 or 32)) return "Глубина цвета: 15, 16, 24 или 32 бита.";
        if (o.Domain.Any(c => char.IsControl(c) || c is '\\' or '/' or '@')) return "Домен: имя без \\, / и @ (например, CORP).";
        if (o.GatewayMode != RdpGatewayMode.Never && (string.IsNullOrWhiteSpace(o.GatewayHost) || o.GatewayHost.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or '@')))
            return "Шлюз удалённых рабочих столов: укажите адрес шлюза без схемы и пути.";
        return null;
    }
}

/// <summary>What a server takes from its Home folder instead of its own fields.</summary>
[Flags]
public enum InheritFlags { None = 0, Username = 1, Domain = 2, Password = 4, RdpOptions = 8 }

/// <summary>Values a Home folder («Прод/Web») gives the servers inside it that inherit them. The password is stored
/// with Windows protection under <see cref="Id"/>, like a profile secret.</summary>
public sealed class FolderDefaults
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Path { get; set; } = "";
    public string Username { get; set; } = "";
    public string Domain { get; set; } = "";
    public bool HasPassword { get; set; }
    public RdpOptions? Rdp { get; set; }
    public FolderDefaults Clone() { var c = (FolderDefaults)MemberwiseClone(); c.Rdp = Rdp?.Clone(); return c; }
}

/// <summary>mRemoteNG-style inheritance: a server marked «наследовать» takes the value from the nearest folder that has one.</summary>
public static class ProfileInheritance
{
    public sealed record Resolved(ServerProfile Profile, Guid? PasswordFolder, IReadOnlyList<string> Missing);

    public static Resolved Resolve(ServerProfile profile, IReadOnlyCollection<FolderDefaults> folders)
    {
        if (profile.Inherit == InheritFlags.None) return new(profile, null, []);
        var chain = Chain(profile.Group, folders);
        var copy = Copy(profile); var missing = new List<string>(); Guid? passwordFolder = null;
        if (profile.Inherit.HasFlag(InheritFlags.Username))
        { if (chain.FirstOrDefault(f => f.Username.Length > 0) is { } f) copy.Username = f.Username; else missing.Add("пользователь"); }
        if (profile.Inherit.HasFlag(InheritFlags.Domain))
        { if (chain.FirstOrDefault(f => f.Domain.Length > 0) is { } f) copy.Rdp.Domain = f.Domain; else missing.Add("домен"); }
        if (profile.Inherit.HasFlag(InheritFlags.Password))
        { if (chain.FirstOrDefault(f => f.HasPassword) is { } f) passwordFolder = f.Id; else missing.Add("пароль"); }
        if (profile.Inherit.HasFlag(InheritFlags.RdpOptions))
        { if (chain.FirstOrDefault(f => f.Rdp is not null) is { Rdp: { } rdp }) { var domain = copy.Rdp.Domain; copy.Rdp = rdp.Clone(); copy.Rdp.Domain = domain; } else missing.Add("параметры RDP"); }
        copy.Inherit = InheritFlags.None;
        return new(copy, passwordFolder, missing);
    }

    /// <summary>The folder itself first, then its parents: «Прод/Web» → «Прод».</summary>
    public static IReadOnlyList<FolderDefaults> Chain(string group, IReadOnlyCollection<FolderDefaults> folders)
    {
        var result = new List<FolderDefaults>(); var path = Normalize(group);
        while (path.Length > 0)
        {
            if (folders.FirstOrDefault(f => string.Equals(Normalize(f.Path), path, StringComparison.CurrentCultureIgnoreCase)) is { } f) result.Add(f);
            int slash = path.LastIndexOf('/'); path = slash > 0 ? path[..slash] : "";
        }
        return result;
    }

    public static string Normalize(string? group) => string.Join("/", (group ?? "").Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

    static ServerProfile Copy(ServerProfile p) => new()
    {
        Id = p.Id, Name = p.Name, Host = p.Host, Port = p.Port, Username = p.Username, Authentication = p.Authentication, PrivateKeyPath = p.PrivateKeyPath,
        InitialPath = p.InitialPath, TimeoutSeconds = p.TimeoutSeconds, SaveSecret = p.SaveSecret, Bookmarks = p.Bookmarks, ProxyJump = p.ProxyJump, Group = p.Group,
        DockerTemplates = p.DockerTemplates, Scripts = p.Scripts, Tunnels = p.Tunnels, Protocol = p.Protocol, Rdp = p.Rdp.Clone(), Inherit = p.Inherit,
    };
}

/// <summary>Reads and writes Remote Desktop Connection files (<c>.rdp</c>). A password is never written to a file.</summary>
public static class RdpFile
{
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim('﻿', ' ', '\t', '\0');
            // "name:type:value" — the value may itself contain ':' (IPv6, ports).
            int first = line.IndexOf(':'); if (first <= 0) continue;
            int second = line.IndexOf(':', first + 1); if (second < 0) continue;
            values[line[..first].Trim()] = line[(second + 1)..];
        }
        return values;
    }

    /// <summary>Text from a file: UTF-16 with BOM (what mstsc writes), UTF-8 otherwise.</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        // UTF-16 without BOM: every second byte of ASCII text is zero.
        if (bytes.Length >= 4 && bytes[1] == 0 && bytes[3] == 0) return Encoding.Unicode.GetString(bytes);
        return Encoding.UTF8.GetString(bytes).TrimStart('﻿');
    }

    /// <summary>Splits «host», «host:port», «[v6]:port».</summary>
    public static (string Host, int? Port) SplitAddress(string address)
    {
        address = address.Trim();
        if (address.StartsWith('['))
        {
            int close = address.IndexOf(']');
            if (close > 0) return (address[1..close], address.Length > close + 2 && address[close + 1] == ':' && int.TryParse(address[(close + 2)..], NumberStyles.None, CultureInfo.InvariantCulture, out var p6) ? p6 : null);
        }
        if (address.Count(c => c == ':') == 1)
        {
            var parts = address.Split(':');
            return (parts[0], int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : null);
        }
        return (address, null);
    }

    public static string Write(ServerProfile profile, string? address = null, int? port = null)
    {
        var o = profile.Rdp; var b = new StringBuilder();
        void S(string name, string value) => b.Append(name).Append(":s:").Append(value.Replace("\r", "").Replace("\n", "")).Append("\r\n");
        void I(string name, int value) => b.Append(name).Append(":i:").Append(value.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        var host = address ?? (profile.Host.Contains(':') ? "[" + profile.Host + "]" : profile.Host);
        var p = port ?? profile.Port;
        S("full address", p == 3389 ? host : host + ":" + p.ToString(CultureInfo.InvariantCulture));
        if (profile.Username.Length > 0) S("username", o.Domain.Length > 0 && !profile.Username.Contains('\\') && !profile.Username.Contains('@') ? o.Domain + "\\" + profile.Username : profile.Username);
        I("screen mode id", 1);
        if (!o.FitsTab) { I("desktopwidth", o.Width); I("desktopheight", o.Height); }
        else I("dynamic resolution", 1);
        I("session bpp", o.ColorDepth);
        I("redirectclipboard", o.Clipboard ? 1 : 0);
        I("redirectprinters", o.Printers ? 1 : 0);
        I("redirectsmartcards", o.SmartCards ? 1 : 0);
        if (o.Drives) S("drivestoredirect", "*");
        I("audiomode", (int)o.Audio);
        I("audiocapturemode", o.Microphone ? 1 : 0);
        I("enablecredsspsupport", o.Nla ? 1 : 0);
        I("authentication level", 2);
        I("administrative session", o.AdminSession ? 1 : 0);
        I("prompt for credentials", 0);
        if (o.GatewayMode != RdpGatewayMode.Never)
        {
            S("gatewayhostname", o.GatewayHost);
            I("gatewayusagemethod", o.GatewayMode == RdpGatewayMode.Always ? 1 : 2);
            I("gatewayprofileusagemethod", 1);
            I("promptcredentialonce", o.GatewaySameCredentials ? 1 : 0);
        }
        return b.ToString();
    }

    /// <summary>A profile candidate from a <c>.rdp</c> file (the name is the file name without extension).</summary>
    public static ImportCandidate ToCandidate(string name, IReadOnlyDictionary<string, string> v, string localUser, IReadOnlyCollection<ServerProfile> existing)
    {
        var notes = new List<string>();
        var (host, port) = SplitAddress(v.GetValueOrDefault("full address") ?? v.GetValueOrDefault("alternate full address") ?? "");
        if (v.TryGetValue("server port", out var sp) && int.TryParse(sp, out var serverPort) && port is null) port = serverPort;
        var user = v.GetValueOrDefault("username") ?? ""; var domain = v.GetValueOrDefault("domain") ?? "";
        SplitUser(ref user, ref domain);
        var o = new RdpOptions
        {
            Domain = domain,
            Clipboard = Flag(v, "redirectclipboard", true), Printers = Flag(v, "redirectprinters", false), SmartCards = Flag(v, "redirectsmartcards", false),
            Drives = (v.GetValueOrDefault("drivestoredirect") ?? "").Length > 0 || Flag(v, "redirectdrives", false), Microphone = Flag(v, "audiocapturemode", false),
            Audio = Int(v, "audiomode") switch { 1 => RdpAudio.RemoteComputer, 2 => RdpAudio.Off, _ => RdpAudio.ThisComputer },
            Nla = Flag(v, "enablecredsspsupport", true), AdminSession = Flag(v, "administrative session", false) || Flag(v, "connect to console", false),
            ColorDepth = Int(v, "session bpp") is { } bpp && bpp is 15 or 16 or 24 or 32 ? bpp : 32,
        };
        if (Int(v, "screen mode id") == 1 && Int(v, "desktopwidth") is >= 640 and <= 8192 and var w && Int(v, "desktopheight") is >= 480 and <= 8192 and var h && !Flag(v, "dynamic resolution", false) && !Flag(v, "smart sizing", false)) { o.Width = w; o.Height = h; }
        if (v.GetValueOrDefault("gatewayhostname") is { Length: > 0 } gw && Int(v, "gatewayusagemethod") is { } usage && usage is 1 or 2 or 4)
        { o.GatewayHost = gw; o.GatewayMode = usage == 1 ? RdpGatewayMode.Always : RdpGatewayMode.Detect; o.GatewaySameCredentials = Flag(v, "promptcredentialonce", true); }
        if (v.ContainsKey("password 51")) notes.Add("Пароль из файла .rdp не переносится.");
        if ((v.GetValueOrDefault("remoteapplicationprogram") ?? "").Length > 0) notes.Add("RemoteApp не поддерживается: откроется весь рабочий стол.");
        return Candidate(name, host, port, user, o, null, localUser, existing, notes);
    }

    /// <summary>
    /// The servers «Подключение к удалённому рабочему столу» remembers (HKCU\…\Terminal Server Client: «Default» MRU
    /// entries and «Servers» with the last user name). Only addresses and user names exist there — no passwords.
    /// </summary>
    public static IReadOnlyList<ImportCandidate> FromHistory(IEnumerable<string> recent, IReadOnlyDictionary<string, string?> serverUsers, string localUser, IReadOnlyCollection<ServerProfile> existing)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var result = new List<ImportCandidate>();
        foreach (var address in recent.Concat(serverUsers.Keys))
        {
            if (string.IsNullOrWhiteSpace(address) || !seen.Add(address.Trim())) continue;
            var (host, port) = SplitAddress(address);
            if (host.Length == 0) continue;
            var user = serverUsers.FirstOrDefault(p => string.Equals(p.Key, address.Trim(), StringComparison.OrdinalIgnoreCase) || string.Equals(p.Key, host, StringComparison.OrdinalIgnoreCase)).Value ?? "";
            var domain = ""; SplitUser(ref user, ref domain);
            result.Add(Candidate(host, host, port, user, new RdpOptions { Domain = domain }, null, localUser, existing, []));
        }
        return result;
    }

    /// <summary>«CORP\user» and «user@corp.local» keep the user and the domain apart.</summary>
    public static void SplitUser(ref string user, ref string domain)
    {
        user = user.Trim(); domain = domain.Trim();
        int slash = user.IndexOf('\\');
        if (slash > 0 && domain.Length == 0) { domain = user[..slash]; user = user[(slash + 1)..]; }
    }

    internal static ImportCandidate Candidate(string name, string host, int? port, string user, RdpOptions options, string? group, string localUser, IReadOnlyCollection<ServerProfile> existing, List<string> notes, InheritFlags inherit = InheritFlags.None)
    {
        host = host.Trim().Trim('[', ']');
        var profile = new ServerProfile { Name = name, Host = host, Port = port is >= 1 and <= 65535 ? port.Value : 3389, Username = user, Protocol = ConnectionProtocol.Rdp, Rdp = options, Group = group ?? "", Inherit = inherit, Authentication = AuthenticationKind.Password };
        if (string.IsNullOrWhiteSpace(user) && !inherit.HasFlag(InheritFlags.Username)) { profile.Username = localUser; notes.Add($"Пользователь не указан: используется имя Windows «{localUser}»."); }
        var duplicate = existing.FirstOrDefault(p => p.IsRdp && string.Equals(p.Host, profile.Host, StringComparison.OrdinalIgnoreCase) && p.Port == profile.Port && string.Equals(p.Username, profile.Username, StringComparison.OrdinalIgnoreCase))?.Name;
        try { profile.Validate(); }
        catch (ArgumentException ex) { notes.Add("Требует исправления: " + ex.Message); }
        return new ImportCandidate(profile, notes, duplicate);
    }

    static bool Flag(IReadOnlyDictionary<string, string> v, string key, bool fallback) => v.TryGetValue(key, out var s) && int.TryParse(s.Trim(), out var i) ? i != 0 : fallback;
    static int? Int(IReadOnlyDictionary<string, string> v, string key) => v.TryGetValue(key, out var s) && int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
}

/// <summary>
/// The server's TLS public key, as the remote desktop control reports it before sign-in, is confirmed once and then
/// checked like an SSH host key. The record lives in hostkeys.json with the algorithm «rdp-tls».
/// </summary>
public static class RdpTrust
{
    public const string Algorithm = "rdp-tls";
    public enum Verdict { Trusted, Unknown, Changed }

    public static (Verdict Verdict, HostKeyPrompt Prompt) Check(HostTrustStore store, string host, int port, byte[] publicKey, ISet<string>? sessionTrusted = null)
    {
        host = HostTrustStore.NormalizeHost(host);
        var fingerprint = HostTrustStore.Fingerprint(publicKey); var key = Convert.ToBase64String(publicKey);
        var prior = store.Records.FirstOrDefault(r => r.Host == host && r.Port == port && r.Algorithm == Algorithm);
        if (prior?.PublicKey == key || sessionTrusted?.Contains(host + ":" + port + ":" + key) == true) return (Verdict.Trusted, new HostKeyPrompt(host, port, "TLS (RDP)", fingerprint, null, false));
        return prior is null ? (Verdict.Unknown, new HostKeyPrompt(host, port, "TLS (RDP)", fingerprint, null, false))
            : (Verdict.Changed, new HostKeyPrompt(host, port, "TLS (RDP)", fingerprint, prior.Fingerprint, true));
    }

    public static string SessionKey(string host, int port, byte[] publicKey) => HostTrustStore.NormalizeHost(host) + ":" + port + ":" + Convert.ToBase64String(publicKey);

    /// <summary>Human text for a disconnect reason code when the control gives none.</summary>
    public static string DescribeDisconnect(int reason) => reason switch
    {
        0 or 1 or 2 or 3 => "Сеанс завершён.",
        260 or 520 => "Сервер не найден: проверьте адрес.",
        264 or 516 => "Сервер не отвечает: проверьте адрес, порт и что удалённый рабочий стол включён.",
        2055 or 2567 or 2823 => "Неверное имя пользователя или пароль.",
        3335 or 3591 => "Учётная запись заблокирована или срок её действия истёк.",
        6919 or 7431 => "Сертификат сервера не принят.",
        _ => $"Соединение прервано (код {reason}).",
    };
}
