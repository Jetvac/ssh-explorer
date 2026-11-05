namespace SSHExplorer.Core;

/// <summary>One row of a server tab's left panel: its section key and whether it is shown.</summary>
public sealed record SidebarItem(string Key, bool Visible);

/// <summary>
/// Order and visibility of the server panel sections, shared by all tabs. Modules (Docker, «Скрипты») are ordinary
/// rows hidden by default: hiding a module also turns it off (scheduled scripts do not run). «Файлы» is the home
/// section and the place a hidden section falls back to, so it cannot be hidden. Unknown saved keys are dropped;
/// sections added in a later version appear next to their default neighbour.
/// </summary>
public static class Sidebar
{
    public static readonly string[] DefaultOrder = ["server", "terminal", "monitor", "tunnels", "firewall", "security", "docker", "scripts", "files"];
    public static readonly string[] Modules = ["docker", "scripts"];
    public const string Home = "files";

    public static bool IsModule(string key) => Array.IndexOf(Modules, key) >= 0;
    public static bool CanHide(string key) => key != Home;

    public static IReadOnlyList<SidebarItem> Resolve(AppSettings settings)
    {
        var order = new List<string>();
        foreach (var key in settings.SidebarOrder ?? []) if (Array.IndexOf(DefaultOrder, key) >= 0 && !order.Contains(key)) order.Add(key);
        for (int i = 0; i < DefaultOrder.Length; i++)
        {
            var key = DefaultOrder[i]; if (order.Contains(key)) continue;
            // After the nearest default predecessor that is already placed; otherwise first.
            int at = 0;
            for (int j = i - 1; j >= 0; j--) { int k = order.IndexOf(DefaultOrder[j]); if (k >= 0) { at = k + 1; break; } }
            order.Insert(at, key);
        }
        var hidden = settings.SidebarHidden ?? [];
        return order.Select(key => new SidebarItem(key, IsVisible(settings, hidden, key))).ToArray();
    }

    static bool IsVisible(AppSettings settings, List<string> hidden, string key) => key switch
    {
        Home => true,
        "docker" => settings.DockerModule,
        "scripts" => settings.ScriptsModule,
        _ => !hidden.Contains(key),
    };

    public static bool IsVisible(AppSettings settings, string key) => IsVisible(settings, settings.SidebarHidden ?? [], key);

    /// <summary>Stores the order and visibility; returns true when anything changed.</summary>
    public static bool Apply(AppSettings settings, IReadOnlyList<SidebarItem> items)
    {
        var before = Resolve(settings);
        settings.SidebarOrder = items.Select(i => i.Key).ToList();
        settings.SidebarHidden = items.Where(i => !i.Visible && CanHide(i.Key) && !IsModule(i.Key)).Select(i => i.Key).ToList();
        settings.DockerModule = items.Any(i => i.Key == "docker" && i.Visible);
        settings.ScriptsModule = items.Any(i => i.Key == "scripts" && i.Visible);
        return !before.SequenceEqual(Resolve(settings));
    }

    public static IReadOnlyList<SidebarItem> Move(IReadOnlyList<SidebarItem> items, string key, int delta)
    {
        var list = items.ToList(); int from = list.FindIndex(i => i.Key == key); if (from < 0) return items;
        int to = Math.Clamp(from + delta, 0, list.Count - 1); if (to == from) return items;
        var item = list[from]; list.RemoveAt(from); list.Insert(to, item); return list;
    }

    public static IReadOnlyList<SidebarItem> MoveTo(IReadOnlyList<SidebarItem> items, string key, int index)
    {
        var list = items.ToList(); int from = list.FindIndex(i => i.Key == key); if (from < 0) return items;
        var item = list[from]; list.RemoveAt(from); list.Insert(Math.Clamp(index, 0, list.Count), item); return list;
    }

    public static IReadOnlyList<SidebarItem> SetVisible(IReadOnlyList<SidebarItem> items, string key, bool visible) =>
        items.Select(i => i.Key == key ? i with { Visible = visible || !CanHide(key) } : i).ToArray();

    /// <summary>The default layout: every section in its usual place, modules hidden.</summary>
    public static IReadOnlyList<SidebarItem> Defaults() => DefaultOrder.Select(k => new SidebarItem(k, !IsModule(k))).ToArray();
}
