using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>A table row: the rule and its position (ufw checks rules top to bottom, the first match decides).</summary>
public sealed record FirewallRow(int Number, UfwRule Rule)
{
    public string Action => Rule.ActionText;
    public string Direction => Rule.DirectionText;
    public string From => Rule.FromText;
    public string To => Rule.ToText;
    public string Proto => Rule.ProtoText;
    public string Comment => Rule.Comment ?? "";
}

/// <summary>
/// «Файервол»: ufw rules, defaults and on/off, with the access this very connection would have after a change. Every
/// change goes through a safe apply: state saved, restore scheduled, a new SSH connection checked, then confirmed —
/// or restored. «Доступ только через сервер…» closes SSH to everyone except the jump server.
/// </summary>
public sealed class FirewallView : UserControl, IDisposable
{
    readonly FirewallService firewall;
    readonly ObservableCollection<FirewallRow> rows = [];
    readonly TextBlock status = Ui.Text("", 13), access = Ui.Text("", 13), warnings = Ui.Text("", 12), state = Ui.Text("", 12);
    readonly Border accessBadge = new() { CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 8, 0, 0) };
    readonly Dictionary<string, Button> commands = [];
    readonly Border placeholder = new() { Padding = new Thickness(24), Visibility = Visibility.Collapsed };
    readonly Grid content = new();
    readonly CancellationTokenSource lifetime = new();
    FirewallSnapshot? snapshot;
    bool loaded, busy;
    internal DataGrid Table { get; } = new() { Name = "FirewallTable", IsReadOnly = true, AutoGenerateColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single };
    internal Func<string, bool>? ConfirmOverride { get; set; }
    internal Func<FirewallSnapshot, RuleDraft?>? EditOverride { get; set; }
    internal Func<FirewallSnapshot, IReadOnlyList<string>?>? WizardOverride { get; set; }
    internal Func<FirewallSnapshot, (string Name, IReadOnlyList<string> Commands)?>? RuleSetsOverride { get; set; }
    internal string StateText => state.Text;
    internal string AccessText => access.Text;
    internal FirewallSnapshot? Snapshot => snapshot;
    FirewallRow? Selected => Table.SelectedItem as FirewallRow;

    public FirewallView(FirewallService service)
    {
        firewall = service; Name = "FirewallView";
        SetResourceReference(BackgroundProperty, "Paper");
        var title = Ui.Text("Файервол", 18, true);
        var subtitle = Ui.Text("ufw · " + firewall.Session.Profile.Name, 12); subtitle.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); subtitle.TextTrimming = TextTrimming.CharacterEllipsis; subtitle.MaxWidth = 340;
        var heading = new StackPanel { Margin = new Thickness(4, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center }; heading.Children.Add(title); heading.Children.Add(subtitle);
        var bar = new ResponsiveCommandBar { Name = "FirewallCommandBar", VerticalAlignment = VerticalAlignment.Center };
        void Command(string key, string glyph, string label, Func<Task> action, string tooltip, int priority)
        {
            var b = Ui.AsyncCommandButton(glyph, label, () => Run(action), tooltip); b.Name = "Firewall" + key; commands[key] = b;
            bar.Add(key, b, () => Ui.AsyncMenu(label, () => Run(action), glyph: glyph), priority);
        }
        Command("Refresh", Ui.RefreshGlyph, "Обновить", () => RefreshAsync(), "Заново прочитать правила", 100);
        Command("Add", Ui.AddGlyph, "Добавить правило…", AddAsync, "Разрешить или запретить порт, адрес или подсеть", 95);
        Command("Remove", Ui.DeleteGlyph, "Удалить…", RemoveAsync, "Удалить выбранное правило", 80);
        Command("Toggle", IconCatalog.Power, "Включить", ToggleAsync, "Включить или выключить ufw", 85);
        Command("Policy", IconCatalog.Settings, "Политика…", PolicyAsync, "Что делать с подключениями, которые не подошли ни к одному правилу", 40);
        Command("Bastion", IconCatalog.Network, "Доступ только через сервер…", BastionAsync, "Закрыть SSH для всех, кроме промежуточного сервера", 60);
        Command("Sets", IconCatalog.Documents, "Наборы правил…", RuleSetsAsync, "Сохранить правила как набор или применить набор к этому серверу", 50);
        var barRow = new DockPanel(); DockPanel.SetDock(heading, Dock.Left); barRow.Children.Add(heading); var divider = Ui.Line(); DockPanel.SetDock(divider, Dock.Left); barRow.Children.Add(divider); barRow.Children.Add(bar);
        var barHost = new Border { Child = barRow, Padding = new Thickness(10, 4, 10, 4) }; barHost.SetResourceReference(StyleProperty, "CommandBarStyle");

        var info = new StackPanel { Margin = new Thickness(16, 12, 16, 8) };
        status.Name = "FirewallStatus"; status.TextWrapping = TextWrapping.Wrap; info.Children.Add(status);
        access.Name = "FirewallAccess"; access.TextWrapping = TextWrapping.Wrap; accessBadge.Child = access; info.Children.Add(accessBadge);
        warnings.Name = "FirewallWarnings"; warnings.TextWrapping = TextWrapping.Wrap; warnings.Margin = new Thickness(0, 8, 0, 0); warnings.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); info.Children.Add(warnings);

        Table.SetResourceReference(StyleProperty, "ProcessTableStyle"); Table.Margin = new Thickness(16, 0, 16, 0);
        MainWindow.AddColumn(Table, "№", nameof(FirewallRow.Number), 48); MainWindow.AddColumn(Table, "Действие", nameof(FirewallRow.Action), 150);
        MainWindow.AddColumn(Table, "Направление", nameof(FirewallRow.Direction), 130); MainWindow.AddColumn(Table, "Откуда", nameof(FirewallRow.From), 200);
        MainWindow.AddColumn(Table, "Куда", nameof(FirewallRow.To), 180); MainWindow.AddColumn(Table, "Протокол", nameof(FirewallRow.Proto), 100); MainWindow.AddColumn(Table, "Комментарий", nameof(FirewallRow.Comment), 220);
        var last = Table.Columns[^1]; last.MinWidth = 160; last.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
        Table.ItemsSource = rows;
        Table.SelectionChanged += (_, _) => UpdateCommands();
        var order = Ui.Text("Правила проверяются сверху вниз, срабатывает первое подходящее; если ни одно не подошло — действует политика по умолчанию.", 12);
        order.Margin = new Thickness(16, 0, 16, 6); order.TextWrapping = TextWrapping.Wrap; order.SetResourceReference(TextBlock.ForegroundProperty, "Muted");

        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); content.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(order, 1); Grid.SetRow(Table, 2); content.Children.Add(info); content.Children.Add(order); content.Children.Add(Table);
        state.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); state.Margin = new Thickness(16, 6, 16, 6); state.TextTrimming = TextTrimming.CharacterEllipsis; state.Name = "FirewallState";
        var footer = new Border { Child = state }; footer.SetResourceReference(StyleProperty, "StatusBarStyle");
        var root = new DockPanel(); DockPanel.SetDock(barHost, Dock.Top); root.Children.Add(barHost); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var body = new Grid(); body.Children.Add(content); body.Children.Add(placeholder); root.Children.Add(body);
        Content = root;
        UpdateCommands();
        IsVisibleChanged += async (_, e) => { if (e.NewValue is true && !loaded) await Ui.Guard(() => RefreshAsync()); };
    }

    async Task Run(Func<Task> action)
    {
        if (busy) return; busy = true; UpdateCommands();
        try { await action(); }
        catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or ArgumentException or Renci.SshNet.Common.SshException) { state.Text = ErrorMessages.Describe(ex); Ui.Error(ErrorMessages.Describe(ex)); }
        finally { busy = false; UpdateCommands(); }
    }

    public async Task RefreshAsync()
    {
        loaded = true;
        string? problem;
        try { problem = await firewall.ProbeAsync(lifetime.Token); }
        catch (Exception ex) when (ex is not OperationCanceledException) { ShowProblem("Не удалось проверить права: " + ErrorMessages.Describe(ex)); return; }
        if (problem is not null) { ShowProblem(problem); return; }
        var read = await firewall.ReadAsync(lifetime.Token);
        if (read.Problem is not null) { snapshot = null; ShowProblem(read.Problem); return; }
        snapshot = read; placeholder.Visibility = Visibility.Collapsed; content.Visibility = Visibility.Visible;
        var selected = Selected?.Rule.Spec;
        rows.Clear(); int n = 0; foreach (var r in read.Rules) rows.Add(new FirewallRow(++n, r));
        Table.SelectedItem = rows.FirstOrDefault(r => r.Rule.Spec == selected);
        Describe(); UpdateCommands();
    }

    static string PolicyText(string policy) => policy switch { "deny" => "запрещать", "reject" => "отклонять", "allow" => "разрешать", "disabled" => "выключена", "" => "неизвестно", _ => policy };

    void Describe()
    {
        if (snapshot is not { } s) return;
        status.Text = (s.Active ? "ufw включён" : "ufw выключен — правила сохранены, но не действуют") + $" · входящие по умолчанию: {PolicyText(s.DefaultIncoming)} · исходящие: {PolicyText(s.DefaultOutgoing)}" + (s.Logging.Length > 0 ? " · журнал: " + s.Logging : "");
        var verdict = Ufw.CheckAccess(s.Rules, s.Active, s.DefaultIncoming, s.ClientAddress, s.SshPort ?? 22);
        access.Text = $"Ваше SSH-подключение: {s.ClientAddress ?? "адрес неизвестен"} → порт {s.SshPort?.ToString(CultureInfo.InvariantCulture) ?? "?"}" + (firewall.Session.Route is { } route ? $" (через {route})" : "") + ". " + verdict.Reason;
        accessBadge.SetResourceReference(Border.BackgroundProperty, verdict.Allowed == false ? "DangerFill" : "Surface");
        var notes = new List<string>();
        if (s.DockerPresent) notes.Add("Docker на этом сервере открывает опубликованные порты контейнеров в обход ufw: ограничивайте их в параметрах запуска (127.0.0.1:порт).");
        if (s.FirewalldActive) notes.Add("Запущен firewalld: два файервола одновременно дают непредсказуемый результат. Оставьте один.");
        notes.Add($"Каждое изменение проверяется новым SSH-подключением; если оно не пройдёт, прежние правила вернутся сразу, а при потере связи — сами через {FirewallService.RollbackSeconds} с. Внешние файерволы (облако, роутер) приложение не видит.");
        warnings.Text = string.Join("\n", notes);
        state.Text = $"Правил: {s.Rules.Count} · права: {(firewall.Rights == "root" ? "root" : "sudo без пароля")}";
    }

    void ShowProblem(string text)
    {
        var message = Ui.Text(text, 14); message.TextWrapping = TextWrapping.Wrap; message.MaxWidth = 680; message.HorizontalAlignment = HorizontalAlignment.Left; message.Name = "FirewallProblem";
        placeholder.Child = message; placeholder.Visibility = Visibility.Visible; content.Visibility = Visibility.Collapsed;
        state.Text = "Файервол недоступен"; UpdateCommands();
    }

    void UpdateCommands()
    {
        bool ok = snapshot is not null && !busy;
        commands["Refresh"].IsEnabled = !busy;
        commands["Add"].IsEnabled = commands["Policy"].IsEnabled = commands["Bastion"].IsEnabled = commands["Toggle"].IsEnabled = commands["Sets"].IsEnabled = ok;
        commands["Remove"].IsEnabled = ok && Selected is not null;
        var label = snapshot is { Active: true } ? "Выключить" : "Включить";
        if (commands["Toggle"].Content is Panel p) foreach (var t in p.Children.OfType<TextBlock>()) if (t.Text is "Включить" or "Выключить") t.Text = label;
    }

    bool Confirm(string text) => ConfirmOverride?.Invoke(text) ?? Ui.Confirm(text, "Файервол");

    /// <summary>Shows what will run and the predicted access, then applies safely.</summary>
    async Task ApplyAsync(string what, IReadOnlyList<string> commandList)
    {
        if (snapshot is not { } s) return;
        var verdict = Ufw.CheckAfter(s, commandList);
        var danger = verdict.Allowed == false
            ? "\n\n⚠ По расчёту, после изменения новые SSH-подключения с вашего адреса будут заблокированы. Если проверка подключения не пройдёт, изменение откатится."
            : verdict.Allowed is null ? "\n\nНе удаётся точно предсказать, пройдёт ли ваше SSH-подключение. Проверка покажет; при неудаче изменение откатится." : "";
        if (!Confirm($"{what} на {firewall.Session.Profile.Name}?\n\nКоманды:\n{string.Join("\n", commandList)}\n\nПосле изменения: {verdict.Reason}{danger}\n\nЕсли новое SSH-подключение не пройдёт, прежние правила вернутся сразу, а при потере связи — сами через {FirewallService.RollbackSeconds} с.")) return;
        var result = await firewall.ApplyAsync(commandList, text => Dispatcher.Invoke(() => state.Text = text), lifetime.Token);
        await RefreshAsync();
        state.Text = result.Message;
        if (result.Outcome != ApplyOutcome.Confirmed) Ui.Error(result.Message + (result.Output.Length > 0 ? "\n\nОтвет ufw:\n" + result.Output : ""));
    }

    async Task AddAsync()
    {
        if (snapshot is not { } s) return;
        var draft = EditOverride is not null ? EditOverride(s) : Edit(s);
        if (draft is null) return;
        await ApplyAsync("Добавить правило", [Ufw.AddCommand(draft, s.Rules.Count)]);
    }

    RuleDraft? Edit(FirewallSnapshot s)
    {
        var window = new FirewallRuleWindow(s) { Owner = Window.GetWindow(this) };
        return window.ShowDialog() == true ? window.Result : null;
    }

    async Task RemoveAsync() { if (Selected is { } r) await ApplyAsync($"Удалить правило №{r.Number} «{r.Rule.Spec}»", [Ufw.DeleteCommand(r.Rule)]); }

    async Task ToggleAsync()
    {
        if (snapshot is not { } s) return;
        await ApplyAsync(s.Active ? "Выключить ufw (все правила перестанут действовать)" : "Включить ufw", [s.Active ? Ufw.DisableCommand : Ufw.EnableCommand]);
    }

    async Task PolicyAsync()
    {
        if (snapshot is not { } s) return;
        var window = new FirewallPolicyWindow(s) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true || window.Commands.Count == 0) return;
        await ApplyAsync("Изменить политику по умолчанию", window.Commands);
    }

    async Task BastionAsync()
    {
        if (snapshot is not { } s) return;
        IReadOnlyList<string>? planned;
        if (WizardOverride is not null) planned = WizardOverride(s);
        else
        {
            IReadOnlyList<ListeningPort> ports = [];
            try { ports = ListeningPorts.Parse((await firewall.Session.ExecuteLongAsync(firewall.Prefix + "sh -c " + PosixPath.Quote(ListeningPorts.Command), TimeSpan.FromSeconds(30), lifetime.Token)).Output).Ports; }
            catch (Exception ex) when (ex is not OperationCanceledException) { _ = ex; }
            var window = new BastionWizardWindow(s, firewall.Session.Route, firewall.Session.Profile.ProxyJump, ports) { Owner = Window.GetWindow(this) };
            planned = window.ShowDialog() == true ? window.Commands : null;
        }
        if (planned is null || planned.Count == 0) return;
        await ApplyAsync("Закрыть прямой SSH-доступ и оставить его только через промежуточный сервер", planned);
    }

    async Task RuleSetsAsync()
    {
        if (snapshot is not { } s) return;
        (string Name, IReadOnlyList<string> Commands)? chosen;
        if (RuleSetsOverride is not null) chosen = RuleSetsOverride(s);
        else
        {
            var window = new FirewallRuleSetsWindow(s, App.Store.Settings.FirewallRuleSets, () => App.Store.SaveAsync(), firewall.Session.Profile.Name) { Owner = Window.GetWindow(this) };
            chosen = window.ShowDialog() == true && window.Commands.Count > 0 ? (window.AppliedName ?? "", window.Commands) : null;
        }
        if (chosen is not { } c || c.Commands.Count == 0) return;
        await ApplyAsync($"Применить набор правил «{c.Name}»", c.Commands);
    }

    public void Dispose() => lifetime.Cancel();
}

/// <summary>Editor of one rule with presets, validation, the exact ufw command and the access check for this connection.</summary>
public sealed class FirewallRuleWindow : Window
{
    readonly FirewallSnapshot snapshot;
    static readonly (string Label, string Port, string Proto)[] Presets =
    [
        ("Свой порт", "", "tcp"), ("SSH (22)", "22", "tcp"), ("HTTP (80)", "80", "tcp"), ("HTTPS (443)", "443", "tcp"), ("Веб: HTTP и HTTPS", "80,443", "tcp"),
        ("PostgreSQL (5432)", "5432", "tcp"), ("MySQL / MariaDB (3306)", "3306", "tcp"), ("Redis (6379)", "6379", "tcp"), ("DNS (53/udp)", "53", "udp"), ("Все порты", "", "any"),
    ];
    readonly ComboBox preset = Combo("RulePreset", Presets.Select(p => p.Label)), action = Combo("RuleAction", ["Разрешить", "Запретить (без ответа)", "Отклонить (с ответом)", "Ограничить частоту (защита от перебора)"]),
        direction = Combo("RuleDirection", ["Входящие", "Исходящие"]), proto = Combo("RuleProto", ["tcp", "udp", "tcp и udp"]);
    readonly RadioButton anyFrom = new() { Content = "Любой адрес", Name = "RuleFromAny", IsChecked = true, Margin = new Thickness(0, 2, 16, 2) }, someFrom = new() { Content = "Только с адреса или подсети:", Name = "RuleFromSome", Margin = new Thickness(0, 2, 8, 2) };
    readonly TextBox from = Box("RuleFrom"), port = Box("RulePort"), iface = Box("RuleInterface"), comment = Box("RuleComment");
    readonly CheckBox first = new() { Content = "Поставить первым (проверяется раньше остальных правил)", Name = "RuleFirst", Margin = new Thickness(0, 8, 0, 0) };
    readonly TextBlock preview = Ui.Text("", 12), verdict = Ui.Text("", 12), problem = Ui.Text("", 12);
    readonly Button save;
    public RuleDraft? Result { get; private set; }
    internal string? Problem => problem.Text.Length > 0 ? problem.Text : null;
    internal string Preview => preview.Text;
    internal string Verdict => verdict.Text;

    static ComboBox Combo(string name, IEnumerable<string> items) => new() { Name = name, ItemsSource = items.ToArray(), SelectedIndex = 0, MinHeight = 32 };
    static TextBox Box(string name) => new() { Name = name, MinHeight = 32 };

    public FirewallRuleWindow(FirewallSnapshot snapshot)
    {
        this.snapshot = snapshot;
        Title = "Правило файервола"; Width = 640; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "DialogWindowStyle");
        var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(Ui.DialogHeading("Новое правило"));
        body.Children.Add(Two(Labeled("Готовый вариант", preset), Labeled("Действие", action)));
        var fromRow = new WrapPanel(); fromRow.Children.Add(anyFrom); fromRow.Children.Add(someFrom); from.Width = 220; fromRow.Children.Add(from);
        var fromPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) }; fromPanel.Children.Add(Ui.Text("Откуда", 12)); fromPanel.Children.Add(fromRow); body.Children.Add(fromPanel);
        from.ToolTip = "1.2.3.4, 10.0.0.0/8 или 2001:db8::/32";
        body.Children.Add(Two(Labeled("Порт (пусто — все; 80,443; 6000:6007)", port), Labeled("Протокол", proto)));
        var advanced = new StackPanel(); advanced.Children.Add(Two(Labeled("Направление", direction), Labeled("Интерфейс (необязательно, например eth0)", iface)));
        body.Children.Add(new Expander { Header = "Направление и интерфейс", Content = advanced, Margin = new Thickness(0, 0, 0, 6) });
        body.Children.Add(Labeled("Комментарий (необязательно)", comment));
        body.Children.Add(first);
        preview.Name = "RulePreview"; preview.FontFamily = new FontFamily("Cascadia Mono, Consolas"); preview.TextWrapping = TextWrapping.Wrap; preview.Margin = new Thickness(0, 12, 0, 0); body.Children.Add(preview);
        verdict.Name = "RuleVerdict"; verdict.TextWrapping = TextWrapping.Wrap; verdict.Margin = new Thickness(0, 6, 0, 0); body.Children.Add(verdict);
        problem.Name = "RuleProblem"; problem.TextWrapping = TextWrapping.Wrap; problem.Margin = new Thickness(0, 6, 0, 0); problem.SetResourceReference(TextBlock.ForegroundProperty, "DangerText"); body.Children.Add(problem);
        var cancel = Ui.Button("Отмена", () => DialogResult = false); cancel.IsCancel = true;
        save = Ui.Button("Применить…", () => { if (save!.IsEnabled) { Result = Draft(); DialogResult = true; } }); save.Name = "RuleSave"; save.IsDefault = true; save.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        Content = Ui.DialogSurface(body, cancel, save);
        preset.SelectionChanged += (_, _) => { var p = Presets[Math.Max(0, preset.SelectedIndex)]; if (preset.SelectedIndex > 0) { port.Text = p.Port; proto.SelectedIndex = p.Proto switch { "udp" => 1, "any" => 2, _ => 0 }; } Update(); };
        foreach (var c in new[] { action, direction, proto }) c.SelectionChanged += (_, _) => Update();
        foreach (var b in new[] { from, port, iface, comment }) b.TextChanged += (_, _) => Update();
        from.GotFocus += (_, _) => someFrom.IsChecked = true;
        anyFrom.Checked += (_, _) => Update(); someFrom.Checked += (_, _) => Update(); first.Click += (_, _) => Update();
        Loaded += (_, _) => port.Focus();
        Update();
    }

    static Grid Two(FrameworkElement a, FrameworkElement b)
    {
        var g = new Grid(); g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) }); g.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(b, 2); g.Children.Add(a); g.Children.Add(b); return g;
    }

    static FrameworkElement Labeled(string label, Control input)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) }; panel.Children.Add(Ui.Text(label, 12)); input.Margin = new Thickness(0, 4, 0, 0); panel.Children.Add(input);
        System.Windows.Automation.AutomationProperties.SetName(input, label); return panel;
    }

    RuleDraft Draft() => new()
    {
        Action = (FirewallAction)Math.Max(0, action.SelectedIndex), Direction = direction.SelectedIndex == 1 ? FirewallDirection.Out : FirewallDirection.In,
        From = someFrom.IsChecked == true ? from.Text.Trim() : "any", Port = port.Text.Trim(), Proto = proto.SelectedIndex switch { 1 => "udp", 2 => "any", _ => "tcp" },
        Interface = iface.Text.Trim(), Comment = comment.Text.Trim(), Prepend = first.IsChecked == true,
    };

    void Update()
    {
        var d = Draft();
        var error = Ufw.Validate(d);
        problem.Text = error ?? ""; problem.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        save.IsEnabled = error is null;
        if (error is not null) { preview.Text = ""; verdict.Text = ""; return; }
        var command = Ufw.AddCommand(d, snapshot.Rules.Count);
        preview.Text = command;
        var after = Ufw.CheckAfter(snapshot, [command]);
        verdict.Text = (after.Allowed == false ? "⚠ " : "") + "Ваше SSH-подключение после этого: " + after.Reason;
        verdict.SetResourceReference(TextBlock.ForegroundProperty, after.Allowed == false ? "DangerText" : "Muted");
    }
}

/// <summary>Default policies for incoming and outgoing connections.</summary>
public sealed class FirewallPolicyWindow : Window
{
    readonly ComboBox incoming = new() { Name = "PolicyIncoming", ItemsSource = new[] { "Запрещать (рекомендуется)", "Отклонять", "Разрешать" }, MinHeight = 32 };
    readonly ComboBox outgoing = new() { Name = "PolicyOutgoing", ItemsSource = new[] { "Запрещать", "Отклонять", "Разрешать (обычно)" }, MinHeight = 32 };
    static readonly string[] Values = ["deny", "reject", "allow"];
    public IReadOnlyList<string> Commands { get; private set; } = [];

    public FirewallPolicyWindow(FirewallSnapshot s)
    {
        Title = "Политика по умолчанию"; Width = 520; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "DialogWindowStyle");
        var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(Ui.DialogHeading("Если ни одно правило не подошло"));
        foreach (var (label, box) in new[] { ("Входящие подключения", incoming), ("Исходящие подключения", outgoing) })
        { var t = Ui.Text(label, 12); t.Margin = new Thickness(0, 8, 0, 4); body.Children.Add(t); body.Children.Add(box); }
        incoming.SelectedIndex = Math.Max(0, Array.IndexOf(Values, s.DefaultIncoming)); outgoing.SelectedIndex = Math.Max(0, Array.IndexOf(Values, s.DefaultOutgoing is "" ? "allow" : s.DefaultOutgoing));
        var note = Ui.Text("Запрет исходящих блокирует обновления, DNS и подключения сервера к другим сервисам, пока для них нет разрешающих правил.", 12); note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(0, 10, 0, 0); note.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); body.Children.Add(note);
        var cancel = Ui.Button("Отмена", () => DialogResult = false); cancel.IsCancel = true;
        var ok = Ui.Button("Применить…", () =>
        {
            var list = new List<string>();
            if (Values[incoming.SelectedIndex] != s.DefaultIncoming) list.Add(Ufw.DefaultCommand("incoming", Values[incoming.SelectedIndex]));
            if (Values[outgoing.SelectedIndex] != s.DefaultOutgoing) list.Add(Ufw.DefaultCommand("outgoing", Values[outgoing.SelectedIndex]));
            Commands = list; DialogResult = true;
        });
        ok.IsDefault = true; ok.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        Content = Ui.DialogSurface(body, cancel, ok);
    }
}
