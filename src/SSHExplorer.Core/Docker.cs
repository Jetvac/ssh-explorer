using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SSHExplorer.Core;

/// <summary>A container as listed by <c>docker ps -a</c>.</summary>
public sealed record DockerContainer(string Id, string Name, string Image, string State, string Status, string Ports, string Created, string Command)
{
    public bool IsRunning => State is "running" or "restarting";
    public string ShortId => Id.Length > 12 ? Id[..12] : Id;
    public string SearchText => $"{Name} {Image} {State} {Status} {Ports} {ShortId}";
}

/// <summary>One environment variable of a template. Secret values are stored encrypted (DPAPI) and shown masked.</summary>
public sealed class DockerEnv
{
    public string Key { get; set; } = "";
    /// <summary>Plain value; empty for a secret, whose value lives in <see cref="Protected"/>.</summary>
    public string Value { get; set; } = "";
    public bool Secret { get; set; }
    /// <summary>Base64 DPAPI (CurrentUser) of the secret value; never written as plain text.</summary>
    public string Protected { get; set; } = "";
}

/// <summary>
/// Saved run parameters for one container on one server: what «Пересоздать с параметрами» uses.
/// Stored in the profile; secret env values are encrypted.
/// </summary>
public sealed class DockerTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ContainerName { get; set; } = "";
    public string Image { get; set; } = "";
    public string RestartPolicy { get; set; } = "unless-stopped";
    public List<string> Ports { get; set; } = [];
    public List<string> Volumes { get; set; } = [];
    public List<DockerEnv> Env { get; set; } = [];
    public string Network { get; set; } = "";
    /// <summary>Command after the image (overrides CMD), shell-like words; empty — the image default.</summary>
    public string Command { get; set; } = "";
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public DockerTemplate Clone() => new()
    {
        Id = Id, ContainerName = ContainerName, Image = Image, RestartPolicy = RestartPolicy, Ports = [.. Ports], Volumes = [.. Volumes], Network = Network, Command = Command, UpdatedUtc = UpdatedUtc,
        Env = Env.Select(e => new DockerEnv { Key = e.Key, Value = e.Value, Secret = e.Secret, Protected = e.Protected }).ToList(),
    };
}

public static partial class DockerCommands
{
    public static readonly string[] RestartPolicies = ["no", "always", "unless-stopped", "on-failure"];

    /// <summary>Finds a usable docker CLI: direct, then non-interactive sudo. Prints one marker line.</summary>
    public const string Probe =
        "if ! command -v docker >/dev/null 2>&1; then echo '#docker missing'; " +
        "elif docker version --format '{{.Server.Version}}' >/dev/null 2>&1; then echo \"#docker direct $(docker version --format '{{.Server.Version}}')\"; " +
        "elif sudo -n docker version --format '{{.Server.Version}}' >/dev/null 2>&1; then echo \"#docker sudo $(sudo -n docker version --format '{{.Server.Version}}')\"; " +
        "else echo '#docker denied'; docker version --format '{{.Server.Version}}' 2>&1 | tail -n 1; fi";

    /// <summary>The CLI prefix to use, or an explanation why Docker cannot be used by this account.</summary>
    public static (string? Cli, string Version, string? Problem) ParseProbe(string output)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var marker = lines.FirstOrDefault(l => l.StartsWith("#docker ", StringComparison.Ordinal)) ?? "#docker missing";
        var parts = marker.Split(' ', 3);
        return parts[1] switch
        {
            "direct" => ("docker", parts.Length > 2 ? parts[2] : "", null),
            "sudo" => ("sudo -n docker", parts.Length > 2 ? parts[2] : "", null),
            "denied" => (null, "", "Нет доступа к Docker: " + (lines.Skip(1).LastOrDefault() is { Length: > 0 } reason && reason.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
                ? "у учётной записи нет прав на /var/run/docker.sock. Добавьте её в группу docker (sudo usermod -aG docker <пользователь>, затем переподключитесь) или разрешите sudo без пароля для docker."
                : (lines.Skip(1).LastOrDefault() ?? "служба Docker не отвечает."))),
            _ => (null, "", "Docker не установлен на этом сервере (команда docker не найдена)."),
        };
    }

    public static string List(string cli) => cli + " ps -a --no-trunc --format '{{json .}}'";
    public static string Action(string cli, string verb, string id) => $"{cli} {verb} {PosixPath.Quote(id)}";
    public static string Remove(string cli, string id, bool force) => $"{cli} rm {(force ? "-f " : "")}{PosixPath.Quote(id)}";
    public static string Logs(string cli, string id, int tail) => $"{cli} logs --timestamps --tail {Math.Clamp(tail, 1, 10000)} {PosixPath.Quote(id)} 2>&1";
    public static string Inspect(string cli, string id) => $"{cli} inspect {PosixPath.Quote(id)}";
    public static string InspectImage(string cli, string image) => $"{cli} image inspect {PosixPath.Quote(image)} 2>/dev/null || true";

    public static IReadOnlyList<DockerContainer> ParseList(string output)
    {
        var result = new List<DockerContainer>();
        foreach (var line in output.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            using var json = JsonDocument.Parse(line); var r = json.RootElement;
            string S(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            result.Add(new DockerContainer(S("ID"), S("Names").TrimStart('/'), S("Image"), S("State").ToLowerInvariant(), S("Status"), S("Ports"), S("CreatedAt"), S("Command").Trim('"')));
        }
        return result.OrderByDescending(c => c.IsRunning).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Builds a template from <c>docker inspect</c>. Variables that equal the image's own environment (PATH and the like)
    /// are left out, so the template holds what was set for this container.
    /// </summary>
    public static DockerTemplate FromInspect(string inspectJson, string? imageInspectJson)
    {
        using var doc = JsonDocument.Parse(inspectJson);
        var c = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement[0] : doc.RootElement;
        var config = c.GetProperty("Config"); var host = c.GetProperty("HostConfig");
        var imageEnv = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(imageInspectJson))
        {
            using var image = JsonDocument.Parse(imageInspectJson);
            var root = image.RootElement.ValueKind == JsonValueKind.Array && image.RootElement.GetArrayLength() > 0 ? image.RootElement[0] : image.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Config", out var ic) && ic.TryGetProperty("Env", out var ie) && ie.ValueKind == JsonValueKind.Array)
                foreach (var e in ie.EnumerateArray()) imageEnv.Add(e.GetString() ?? "");
        }
        var template = new DockerTemplate { ContainerName = c.GetProperty("Name").GetString()!.TrimStart('/'), Image = config.GetProperty("Image").GetString() ?? "" };
        if (config.TryGetProperty("Env", out var env) && env.ValueKind == JsonValueKind.Array)
            foreach (var item in env.EnumerateArray().Select(e => e.GetString() ?? "").Where(e => !imageEnv.Contains(e)))
            {
                int eq = item.IndexOf('='); if (eq <= 0) continue;
                template.Env.Add(new DockerEnv { Key = item[..eq], Value = item[(eq + 1)..] });
            }
        if (host.TryGetProperty("PortBindings", out var bindings) && bindings.ValueKind == JsonValueKind.Object)
            foreach (var binding in bindings.EnumerateObject())
            {
                var container = binding.Name.EndsWith("/tcp", StringComparison.Ordinal) ? binding.Name[..^4] : binding.Name;
                if (binding.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var b in binding.Value.EnumerateArray())
                {
                    var hostIp = b.TryGetProperty("HostIp", out var ip) ? ip.GetString() ?? "" : ""; var hostPort = b.TryGetProperty("HostPort", out var hp) ? hp.GetString() ?? "" : "";
                    template.Ports.Add((hostIp.Length > 0 ? (hostIp.Contains(':') ? $"[{hostIp}]" : hostIp) + ":" : "") + (hostPort.Length > 0 ? hostPort + ":" : "") + container);
                }
            }
        if (host.TryGetProperty("Binds", out var binds) && binds.ValueKind == JsonValueKind.Array)
            foreach (var b in binds.EnumerateArray()) if (b.GetString() is { Length: > 0 } v) template.Volumes.Add(v);
        if (host.TryGetProperty("RestartPolicy", out var restart) && restart.TryGetProperty("Name", out var rn) && rn.GetString() is { Length: > 0 } policy)
            template.RestartPolicy = RestartPolicies.Contains(policy) ? policy : "no";
        else template.RestartPolicy = "no";
        if (host.TryGetProperty("NetworkMode", out var net) && net.GetString() is { Length: > 0 } mode && mode is not ("default" or "bridge")) template.Network = mode;
        if (config.TryGetProperty("Cmd", out var cmd) && cmd.ValueKind == JsonValueKind.Array && !(imageInspectJson is not null && SameCmd(cmd, imageInspectJson)))
            template.Command = string.Join(' ', cmd.EnumerateArray().Select(a => QuoteWord(a.GetString() ?? "")));
        return template;
    }

    static bool SameCmd(JsonElement cmd, string imageInspectJson)
    {
        using var image = JsonDocument.Parse(imageInspectJson);
        var root = image.RootElement.ValueKind == JsonValueKind.Array && image.RootElement.GetArrayLength() > 0 ? image.RootElement[0] : image.RootElement;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Config", out var ic) && ic.TryGetProperty("Cmd", out var icmd) && icmd.ValueKind == JsonValueKind.Array
            && cmd.EnumerateArray().Select(e => e.GetString()).SequenceEqual(icmd.EnumerateArray().Select(e => e.GetString()));
    }

    /// <summary>Every problem that would stop a run, as user-facing messages (empty — valid).</summary>
    public static IReadOnlyList<string> Validate(DockerTemplate t, Func<DockerEnv, string> value)
    {
        var errors = new List<string>();
        if (!NameRegex().IsMatch(t.ContainerName)) errors.Add("Имя контейнера: латиница, цифры, «_», «.», «-»; начинается с буквы или цифры.");
        if (string.IsNullOrWhiteSpace(t.Image) || t.Image.Any(char.IsWhiteSpace)) errors.Add("Укажите образ без пробелов, например nginx:1.27.");
        if (!RestartPolicies.Contains(t.RestartPolicy)) errors.Add("Неизвестная политика перезапуска.");
        foreach (var p in t.Ports) if (!PortRegex().IsMatch(p)) errors.Add($"Порт «{p}»: ожидается [адрес:]порт_хоста:порт_контейнера[/udp].");
        foreach (var v in t.Volumes) if (v.Length == 0 || v.Contains('\n') || !v.Contains(':')) errors.Add($"Том «{v}»: ожидается источник:путь_в_контейнере[:ro].");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in t.Env)
        {
            if (!EnvKeyRegex().IsMatch(e.Key)) errors.Add($"Переменная «{e.Key}»: имя из латиницы, цифр и «_», не с цифры.");
            else if (!keys.Add(e.Key)) errors.Add($"Переменная «{e.Key}» задана дважды.");
            var v = value(e); if (v.Contains('\n') || v.Contains('\r') || v.Contains('\0')) errors.Add($"Значение «{e.Key}» содержит перевод строки: Docker env-file их не поддерживает.");
        }
        if (t.Network.Length > 0 && !NameRegex().IsMatch(t.Network) && t.Network is not "host" and not "none") errors.Add("Сеть: имя сети Docker.");
        try { _ = SplitWords(t.Command); } catch (FormatException ex) { errors.Add("Команда: " + ex.Message); }
        return errors;
    }

    /// <summary>
    /// <c>docker run</c> with every argument quoted separately. Environment comes from <paramref name="envFile"/> only,
    /// so values never appear in the server's process list.
    /// </summary>
    public static string Run(string cli, DockerTemplate t, string? envFile)
    {
        var args = new List<string> { "run", "-d", "--name", t.ContainerName, "--restart", t.RestartPolicy };
        foreach (var p in t.Ports) { args.Add("-p"); args.Add(p); }
        foreach (var v in t.Volumes) { args.Add("-v"); args.Add(v); }
        if (t.Network.Length > 0) { args.Add("--network"); args.Add(t.Network); }
        if (envFile is not null) { args.Add("--env-file"); args.Add(envFile); }
        args.Add(t.Image); args.AddRange(SplitWords(t.Command));
        return cli + " " + string.Join(' ', args.Select(PosixPath.Quote));
    }

    /// <summary>docker env-file: one KEY=VALUE per line, the value taken literally (no quotes processing).</summary>
    public static string EnvFile(DockerTemplate t, Func<DockerEnv, string> value) =>
        string.Concat(t.Env.Select(e => e.Key + "=" + value(e) + "\n"));

    /// <summary>Shell-like words: spaces separate, single and double quotes group, backslash escapes outside single quotes.</summary>
    public static IReadOnlyList<string> SplitWords(string text)
    {
        var words = new List<string>(); var current = new StringBuilder(); bool any = false; char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote == '\'') { if (c == '\'') quote = '\0'; else current.Append(c); continue; }
            if (c == '\\' && i + 1 < text.Length && quote != '\'') { current.Append(text[++i]); any = true; continue; }
            if (quote == '"') { if (c == '"') quote = '\0'; else current.Append(c); continue; }
            if (c is '\'' or '"') { quote = c; any = true; continue; }
            if (char.IsWhiteSpace(c)) { if (any) { words.Add(current.ToString()); current.Clear(); any = false; } continue; }
            current.Append(c); any = true;
        }
        if (quote != '\0') throw new FormatException("незакрытая кавычка.");
        if (any) words.Add(current.ToString());
        return words;
    }

    static string QuoteWord(string word) => word.Length > 0 && word.All(ch => char.IsLetterOrDigit(ch) || "-_./:=@,+%".Contains(ch)) ? word : "'" + word.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$", RegexOptions.CultureInvariant)] private static partial Regex NameRegex();
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)] private static partial Regex EnvKeyRegex();
    [GeneratedRegex(@"^(?:(?:\d{1,3}(?:\.\d{1,3}){3}|\[[0-9a-fA-F:]+\]):)?(?:\d{1,5}(?:-\d{1,5})?:)?\d{1,5}(?:-\d{1,5})?(?:/(?:tcp|udp|sctp))?$", RegexOptions.CultureInvariant)] private static partial Regex PortRegex();
}

/// <summary>Encrypts secret env values for storage (Windows DPAPI, CurrentUser). Replaceable for tests.</summary>
public static class DockerSecrets
{
    public static Func<string, string> Protect { get; set; } = plain =>
        OperatingSystem.IsWindows() ? Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, System.Security.Cryptography.DataProtectionScope.CurrentUser)) : throw new PlatformNotSupportedException();
    public static Func<string, string> Unprotect { get; set; } = stored =>
        OperatingSystem.IsWindows() ? Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(stored), null, System.Security.Cryptography.DataProtectionScope.CurrentUser)) : throw new PlatformNotSupportedException();

    /// <summary>The value to send: plain, or decrypted secret.</summary>
    public static string ValueOf(DockerEnv e) => e.Secret ? (e.Protected.Length == 0 ? "" : Unprotect(e.Protected)) : e.Value;
    public static void SetSecret(DockerEnv e, string plain) { e.Secret = true; e.Value = ""; e.Protected = plain.Length == 0 ? "" : Protect(plain); }
}
