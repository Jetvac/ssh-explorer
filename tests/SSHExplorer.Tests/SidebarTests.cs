#if CORE_TESTS
using SSHExplorer.Core;

/// <summary>Server panel order and visibility: defaults, saved layouts, new sections, modules and the home section.</summary>
internal static class SidebarTests
{
    public static async Task RunAsync(TestSuite t)
    {
        await t.Run("sidebar: by default every section is in its usual place and the modules are hidden", () =>
        {
            var items = Sidebar.Resolve(new AppSettings());
            TestSuite.True(items.Select(i => i.Key).SequenceEqual(Sidebar.DefaultOrder) && items.Where(i => !i.Visible).Select(i => i.Key).SequenceEqual(["docker", "scripts"]), string.Join(",", items));
        });
        await t.Run("sidebar: a saved order is kept, unknown keys are dropped and missing sections join next to their default neighbour", () =>
        {
            var s = new AppSettings { SidebarOrder = ["files", "terminal", "gone", "server", "terminal"] };
            var keys = Sidebar.Resolve(s).Select(i => i.Key).ToArray();
            TestSuite.True(keys.SequenceEqual(["files", "terminal", "monitor", "tunnels", "firewall", "docker", "scripts", "server"]), string.Join(",", keys));
        });
        await t.Run("sidebar: hiding, showing and moving are stored; modules follow their old on/off flags", () =>
        {
            var s = new AppSettings { DockerModule = true };
            TestSuite.True(Sidebar.IsVisible(s, "docker") && !Sidebar.IsVisible(s, "scripts"), "the 0.3 module flag is honoured");
            var items = Sidebar.SetVisible(Sidebar.Resolve(s), "firewall", false);
            items = Sidebar.SetVisible(items, "scripts", true);
            items = Sidebar.Move(items, "files", -100);
            TestSuite.True(Sidebar.Apply(s, items) && s.SidebarHidden.SequenceEqual(["firewall"]) && s.ScriptsModule && s.DockerModule && s.SidebarOrder[0] == "files", string.Join(",", s.SidebarOrder) + " / " + string.Join(",", s.SidebarHidden));
            TestSuite.True(!Sidebar.Apply(s, Sidebar.Resolve(s)), "applying the same layout reports no change");
            var dragged = Sidebar.MoveTo(Sidebar.Resolve(s), "server", 3);
            TestSuite.True(dragged[3].Key == "server" && dragged.Count == Sidebar.DefaultOrder.Length, string.Join(",", dragged.Select(i => i.Key)));
        });
        await t.Run("sidebar: «Файлы» cannot be hidden; defaults restore order and hide the modules again", () =>
        {
            var s = new AppSettings();
            Sidebar.Apply(s, Sidebar.SetVisible(Sidebar.Resolve(s), "files", false));
            TestSuite.True(Sidebar.IsVisible(s, "files") && !s.SidebarHidden.Contains("files"), "home stays");
            Sidebar.Apply(s, Sidebar.SetVisible(Sidebar.Move(Sidebar.Resolve(s), "files", -3), "docker", true));
            Sidebar.Apply(s, Sidebar.Defaults());
            TestSuite.True(Sidebar.Resolve(s).SequenceEqual(Sidebar.Resolve(new AppSettings())) && !s.DockerModule && !s.ScriptsModule, "defaults");
        });
        await t.Run("sidebar: the layout survives a JSON round trip of the settings", () =>
        {
            var s = new AppSettings(); Sidebar.Apply(s, Sidebar.SetVisible(Sidebar.Move(Sidebar.Resolve(s), "tunnels", -2), "server", false));
            var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(s))!;
            TestSuite.True(Sidebar.Resolve(back).SequenceEqual(Sidebar.Resolve(s)), string.Join(",", Sidebar.Resolve(back)));
        });
    }
}
#endif
