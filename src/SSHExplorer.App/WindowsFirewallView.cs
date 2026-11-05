using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>
/// «Файервол» of a Windows server: inbound rules of Windows Defender Firewall. Every change exports the policy,
/// schedules its import as SYSTEM in two minutes, then checks a new SSH connection before cancelling the restore.
/// Rules made here go to the «SSH Explorer» group; only those can be removed from the app.
/// </summary>
public sealed class WindowsFirewallView : UserControl, IDisposable
{
    readonly WindowsFirewallService firewall;
    readonly ObservableCollection<WindowsFirewallRule> rows = [];
    readonly TextBlock info = Ui.Text("", 12), state = Ui.Text("", 12);
    readonly TextBox search = new() { MinWidth = 220, MinHeight = 30, ToolTip = "Поиск по названию, порту, адресу" };
    readonly Dictionary<string, Button> commands = [];
    readonly CancellationTokenSource lifetime = new();
    WindowsFirewallSnapshot? snapshot; bool loaded, busy;
    internal DataGrid Table { get; } = new() { Name = "WindowsFirewallTable", IsReadOnly = true, AutoGenerateColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single };
    internal Func<WindowsFirewallNewRule?>? AddOverride { get; set; }
    internal Func<string, bool>? ConfirmOverride { get; set; }
    internal string StateText => state.Text;
    internal string InfoText => info.Text;
    internal ObservableCollection<WindowsFirewallRule> Rows => rows;
    WindowsFirewallRule? Selected => Table.SelectedItem as WindowsFirewallRule;

    public WindowsFirewallView(WindowsFirewallService service)
    {
        firewall = service; Name = "WindowsFirewallView"; SetResourceReference(BackgroundProperty, "Paper");
        var title = Ui.Text("Брандмауэр Windows", 18, true);
        var subtitle = Ui.Text("входящие правила · " + firewall.Session.Profile.Name, 12); subtitle.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); subtitle.TextTrimming = TextTrimming.CharacterEllipsis; subtitle.MaxWidth = 340;
        var heading = new StackPanel { Margin = new Thickness(4, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center }; heading.Children.Add(title); heading.Children.Add(subtitle);
        var bar = new ResponsiveCommandBar { Name = "WindowsFirewallCommandBar", VerticalAlignment = VerticalAlignment.Center };
        void Command(string key, string glyph, string label, Func<Task> action, string tooltip, int priority)
        {
            var b = Ui.AsyncCommandButton(glyph, label, () => Run(action), tooltip); b.Name = "WinFw" + key; commands[key] = b;
            bar.Add(key, b, () => Ui.AsyncMenu(label, () => Run(action), glyph: glyph), priority);
        }
        Command("Refresh", Ui.RefreshGlyph, "Обновить", () => RefreshAsync(), "Заново прочитать правила", 100);
        Command("Add", Ui.AddGlyph, "Добавить правило…", AddAsync, "Разрешить или запретить входящие подключения к порту", 95);
        Command("Toggle", IconCatalog.Power, "Выключить", ToggleAsync, "Выключить или включить выбранное правило", 80);
        Command("Remove", Ui.DeleteGlyph, "Удалить…", RemoveAsync, "Удалить правило, созданное в SSH Explorer", 60);
        AutomationProperties.SetName(search, "Поиск правил"); search.Margin = new Thickness(0, 0, 12, 0); search.VerticalAlignment = VerticalAlignment.Center;
        search.TextChanged += (_, _) => ApplyFilter();
        var top = new DockPanel { LastChildFill = true }; DockPanel.SetDock(heading, Dock.Left); top.Children.Add(heading); DockPanel.SetDock(search, Dock.Right); top.Children.Add(search); top.Children.Add(bar);
        var barHost = new Border { Child = top, Padding = new Thickness(10, 4, 10, 4) }; barHost.SetResourceReference(StyleProperty, "CommandBarStyle");
        info.Name = "WindowsFirewallInfo"; info.TextWrapping = TextWrapping.Wrap; info.Margin = new Thickness(14, 8, 14, 4); info.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        Table.SetResourceReference(StyleProperty, "ProcessTableStyle"); Table.Margin = new Thickness(10, 4, 10, 0);
        MainWindow.AddColumn(Table, "Правило", nameof(WindowsFirewallRule.Name), 280); MainWindow.AddColumn(Table, "Состояние", nameof(WindowsFirewallRule.EnabledText), 100);
        MainWindow.AddColumn(Table, "Действие", nameof(WindowsFirewallRule.ActionText), 100); MainWindow.AddColumn(Table, "Протокол", nameof(WindowsFirewallRule.ProtocolText), 90);
        MainWindow.AddColumn(Table, "Порт", nameof(WindowsFirewallRule.PortsText), 110); MainWindow.AddColumn(Table, "Откуда", nameof(WindowsFirewallRule.FromText), 170); MainWindow.AddColumn(Table, "Профиль", nameof(WindowsFirewallRule.ProfileText), 120);
        Table.Columns[^1].Width = new DataGridLength(1, DataGridLengthUnitType.Star);
        Table.ItemsSource = rows; Table.SelectionChanged += (_, _) => UpdateCommands();
        state.Name = "WindowsFirewallState"; state.Margin = new Thickness(0); state.TextWrapping = TextWrapping.Wrap;
        var footer = new Border { Child = state }; footer.SetResourceReference(StyleProperty, "StatusBarStyle");
        var root = new DockPanel(); DockPanel.SetDock(barHost, Dock.Top); root.Children.Add(barHost); DockPanel.SetDock(info, Dock.Top); root.Children.Add(info); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); root.Children.Add(Table);
        Content = root;
        Loaded += async (_, _) => { if (loaded) return; loaded = true; await Ui.Guard(() => Run(() => RefreshAsync())); };
        UpdateCommands();
    }

    async Task Run(Func<Task> action)
    {
        if (busy) return; busy = true; UpdateCommands();
        try { await action(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or FormatException or ArgumentException or Renci.SshNet.Common.SshException) { state.Text = ErrorMessages.Describe(ex); }
        finally { busy = false; UpdateCommands(); }
    }

    internal async Task RefreshAsync(string? select = null)
    {
        state.Text = "Чтение правил…";
        snapshot = await firewall.ReadAsync(lifetime.Token);
        var profiles = string.Join(" · ", snapshot.Profiles.Select(p => $"{p.Name}: {(p.Enabled ? "включён" : "выключен")}, входящие по умолчанию — {(p.DefaultInbound.Equals("Allow", StringComparison.OrdinalIgnoreCase) ? "разрешены" : "запрещены")}"));
        info.Text = (snapshot.Admin ? "" : "Пользователь SSH не администратор: правила можно только смотреть. ") + profiles + ". Изменения проверяются новым SSH-подключением и при сбое откатываются; если связь потеряна — правила вернутся сами через " + WindowsFirewall.RollbackSeconds + " с.";
        ApplyFilter(select);
        state.Text = $"Правил: {snapshot.Rules.Count}, включено: {snapshot.Rules.Count(r => r.Enabled)} · созданных в SSH Explorer: {snapshot.Rules.Count(r => r.Ours)}";
    }

    void ApplyFilter(string? select = null)
    {
        if (snapshot is null) return;
        var keep = select ?? Selected?.Id; var text = search.Text.Trim();
        rows.Clear();
        foreach (var r in snapshot.Rules.Where(r => text.Length == 0 || r.SearchText.Contains(text, StringComparison.CurrentCultureIgnoreCase))) rows.Add(r);
        Table.SelectedItem = rows.FirstOrDefault(r => r.Id == keep);
    }

    void UpdateCommands()
    {
        bool admin = snapshot?.Admin == true, idle = !busy;
        commands["Refresh"].IsEnabled = idle;
        commands["Add"].IsEnabled = idle && admin;
        commands["Toggle"].IsEnabled = idle && admin && Selected is not null;
        commands["Remove"].IsEnabled = idle && admin && Selected is { Ours: true };
        if (commands["Toggle"].Content is Panel panel) foreach (var t in panel.Children.OfType<TextBlock>()) if (t.Text is "Выключить" or "Включить") t.Text = Selected?.Enabled == false ? "Включить" : "Выключить";
    }

    bool Confirm(string text) => ConfirmOverride?.Invoke(text) ?? Ui.Confirm(text);

    async Task Apply(string change, string what, string? select = null)
    {
        var result = await firewall.ApplyAsync(change, p => Dispatcher.Invoke(() => state.Text = p), lifetime.Token);
        await RefreshAsync(select);
        state.Text = what + ": " + result.Message + (result.Outcome == ApplyOutcome.Failed && result.Output.Length > 0 ? " " + result.Output : "");
    }

    async Task AddAsync()
    {
        var rule = AddOverride is not null ? AddOverride() : WindowsFirewallRuleWindow.Ask(Window.GetWindow(this));
        if (rule is null) return;
        if (!Confirm($"{(rule.Allow ? "Разрешить" : "Запретить")} входящие {rule.Protocol} {rule.Ports} {(string.IsNullOrWhiteSpace(rule.RemoteAddresses) ? "откуда угодно" : "с " + rule.RemoteAddresses)} на {firewall.Session.Profile.Name}?\n\nПеред изменением правила сохраняются; если новое SSH-подключение не пройдёт, они вернутся.")) return;
        await Apply(WindowsFirewall.AddScript(rule), "«" + rule.Name + "»");
    }

    async Task ToggleAsync()
    {
        if (Selected is not { } r) return;
        if (!Confirm($"{(r.Enabled ? "Выключить" : "Включить")} правило «{r.Name}»?")) return;
        await Apply(WindowsFirewall.EnableScript(r.Id, !r.Enabled), "«" + r.Name + "»", r.Id);
    }

    async Task RemoveAsync()
    {
        if (Selected is not { Ours: true } r) return;
        if (!Confirm($"Удалить правило «{r.Name}»?")) return;
        await Apply(WindowsFirewall.RemoveScript(r.Id), "«" + r.Name + "» удалено");
    }

    public void Dispose() { lifetime.Cancel(); lifetime.Dispose(); }
}

/// <summary>A new inbound rule: name, allow or block, protocol, ports, from where.</summary>
public static class WindowsFirewallRuleWindow
{
    public static WindowsFirewallNewRule? Ask(Window? owner)
    {
        var w = Dialogs.Window("Новое правило брандмауэра", 520, owner); var p = new StackPanel { Margin = new Thickness(24) };
        p.Children.Add(Ui.DialogHeading("Входящие подключения"));
        TextBox Field(string label, string value) { var l = Ui.Text(label, 12); l.Margin = new Thickness(0, 10, 0, 5); l.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); p.Children.Add(l); var t = new TextBox { Text = value, MinHeight = 34 }; AutomationProperties.SetName(t, label); p.Children.Add(t); return t; }
        var name = Field("Название", "");
        var action = new ComboBox { ItemsSource = new[] { "Разрешить", "Запретить" }, SelectedIndex = 0, MinHeight = 34, Margin = new Thickness(0, 10, 0, 0) }; AutomationProperties.SetName(action, "Действие"); p.Children.Add(action);
        var protocol = new ComboBox { ItemsSource = new[] { "TCP", "UDP", "Любой" }, SelectedIndex = 0, MinHeight = 34, Margin = new Thickness(0, 10, 0, 0) }; AutomationProperties.SetName(protocol, "Протокол"); p.Children.Add(protocol);
        var ports = Field("Порт (8080, список 80,443 или диапазон 8000-8100)", "");
        var from = Field("Откуда (пусто — откуда угодно; адреса или подсети через запятую)", "");
        var error = Ui.Text("", 12); error.SetResourceReference(FrameworkElement.StyleProperty, "ValidationMessageStyle"); error.TextWrapping = TextWrapping.Wrap; p.Children.Add(error);
        WindowsFirewallNewRule? result = null;
        var ok = Ui.Button("Добавить", () =>
        {
            var rule = new WindowsFirewallNewRule(name.Text.Trim(), action.SelectedIndex == 0, protocol.SelectedIndex switch { 0 => "TCP", 1 => "UDP", _ => "Any" }, ports.Text.Trim(), from.Text.Trim());
            if (WindowsFirewall.Validate(rule) is { } problem) { error.Text = problem; return; }
            result = rule; w.DialogResult = true;
        });
        ok.IsDefault = true; ok.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButtonStyle");
        var cancel = Ui.Button("Отмена", () => w.DialogResult = false); cancel.IsCancel = true;
        w.Content = Ui.DialogSurface(p, cancel, ok); w.Loaded += (_, _) => name.Focus();
        return ModalRunner.Show(w, owner, CancellationToken.None) ? result : null;
    }
}
