using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>One row: a saved tunnel and its live state in this tab.</summary>
public sealed class TunnelRow(TunnelSpec spec, TunnelStatus status) : INotifyPropertyChanged
{
    public TunnelSpec Spec { get; private set; } = spec;
    public TunnelStatus Status { get; private set; } = status;
    public string Name => Spec.Name.Length > 0 ? Spec.Name : Spec.Kind == TunnelKind.Local ? "Локальный" : "Обратный";
    public string KindText => Spec.Kind == TunnelKind.Local ? "Компьютер → сервер (-L)" : "Сервер → компьютер (-R)";
    public string Route => Tunnels.Describe(Spec, Status.BoundPort);
    public string StateText => Status.State switch
    {
        TunnelState.Running => "Открыт" + (Status.Error is { } e ? " · " + e : ""),
        TunnelState.Starting => "Открывается…",
        TunnelState.Failed => "Ошибка: " + Status.Error,
        _ => "Закрыт",
    };
    public string ConnectionsText => Status.State == TunnelState.Running ? Status.Connections.ToString(CultureInfo.CurrentCulture) : "—";
    public string AutoText => Spec.StartOnConnect ? "При подключении" : "Вручную";
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Update(TunnelSpec spec, TunnelStatus status) { Spec = spec; Status = status; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); }
}

/// <summary>
/// «Туннели»: saved port forwards of the server with open/close, live state and the number of accepted connections.
/// Local tunnels listen on 127.0.0.1 by default; listening on all interfaces is allowed only after an explicit warning.
/// </summary>
public sealed class TunnelsView : UserControl, IDisposable
{
    readonly ServerSession session;
    readonly TunnelManager manager;
    readonly ObservableCollection<TunnelRow> rows = [];
    readonly TextBlock state = Ui.Text("", 12);
    readonly Dictionary<string, Button> commands = [];
    readonly CancellationTokenSource lifetime = new();
    bool busy;
    internal DataGrid Table { get; } = new() { Name = "TunnelsTable", IsReadOnly = true, AutoGenerateColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single };
    internal Func<TunnelSpec, TunnelSpec?>? EditOverride { get; set; }
    internal Func<string, bool>? ConfirmOverride { get; set; }
    internal string StateText => state.Text;
    TunnelRow? Selected => Table.SelectedItem as TunnelRow;

    public TunnelsView(ServerSession session, TunnelManager manager)
    {
        this.session = session; this.manager = manager; Name = "TunnelsView";
        SetResourceReference(BackgroundProperty, "Paper");
        var title = Ui.Text("Туннели", 18, true);
        var subtitle = Ui.Text("Переадресация портов · " + session.Profile.Name, 12); subtitle.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); subtitle.TextTrimming = TextTrimming.CharacterEllipsis; subtitle.MaxWidth = 340;
        var heading = new StackPanel { Margin = new Thickness(4, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center }; heading.Children.Add(title); heading.Children.Add(subtitle);
        var bar = new ResponsiveCommandBar { Name = "TunnelsCommandBar", VerticalAlignment = VerticalAlignment.Center };
        void Command(string key, string glyph, string label, Func<Task> action, string tooltip, int priority)
        {
            var b = Ui.AsyncCommandButton(glyph, label, () => Run(action), tooltip); b.Name = "Tunnels" + key; commands[key] = b;
            bar.Add(key, b, () => Ui.AsyncMenu(label, () => Run(action), glyph: glyph), priority);
        }
        Command("Open", IconCatalog.Network, "Открыть", OpenAsync, "Открыть выбранный туннель", 100);
        Command("Close", Ui.CloseGlyph, "Закрыть", CloseAsync, "Закрыть выбранный туннель; открытые через него соединения прервутся", 90);
        Command("Add", Ui.AddGlyph, "Добавить…", AddAsync, "Новый туннель", 80);
        Command("Edit", IconCatalog.Settings, "Изменить…", EditAsync, "Изменить выбранный туннель", 70);
        Command("Remove", Ui.DeleteGlyph, "Удалить…", RemoveAsync, "Удалить туннель из списка сервера", 10);
        var barRow = new DockPanel(); DockPanel.SetDock(heading, Dock.Left); barRow.Children.Add(heading); var divider = Ui.Line(); DockPanel.SetDock(divider, Dock.Left); barRow.Children.Add(divider); barRow.Children.Add(bar);
        var barHost = new Border { Child = barRow, Padding = new Thickness(10, 4, 10, 4) }; barHost.SetResourceReference(StyleProperty, "CommandBarStyle");

        Table.SetResourceReference(StyleProperty, "ProcessTableStyle"); Table.Margin = new Thickness(16, 12, 16, 0);
        MainWindow.AddColumn(Table, "Имя", nameof(TunnelRow.Name), 160); MainWindow.AddColumn(Table, "Направление", nameof(TunnelRow.KindText), 190);
        MainWindow.AddColumn(Table, "Маршрут", nameof(TunnelRow.Route), 360); MainWindow.AddColumn(Table, "Состояние", nameof(TunnelRow.StateText), 200);
        MainWindow.AddColumn(Table, "Соединений", nameof(TunnelRow.ConnectionsText), 100); MainWindow.AddColumn(Table, "Запуск", nameof(TunnelRow.AutoText), 140);
        Table.Columns[2].Width = new DataGridLength(1, DataGridLengthUnitType.Star); Table.Columns[2].MinWidth = 220;
        Table.ItemsSource = rows;
        Table.SelectionChanged += (_, _) => UpdateCommands();
        Table.MouseDoubleClick += async (_, _) => { if (Selected is { } r) await Ui.Guard(() => Run(manager.IsRunning(r.Spec.Id) ? (Func<Task>)CloseAsync : OpenAsync)); };

        var note = Ui.Text("Локальный туннель: порт на этом компьютере ведёт к адресу, видимому с сервера (как ssh -L). Обратный: порт на сервере ведёт к адресу, видимому с компьютера (ssh -R). Каждый туннель использует своё SSH-подключение.", 12);
        note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(16, 10, 16, 8); note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        state.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); state.Margin = new Thickness(16, 6, 16, 6); state.TextTrimming = TextTrimming.CharacterEllipsis; state.Name = "TunnelsState";
        var footer = new Border { Child = state }; footer.SetResourceReference(StyleProperty, "StatusBarStyle");
        var root = new DockPanel(); DockPanel.SetDock(barHost, Dock.Top); root.Children.Add(barHost); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); DockPanel.SetDock(note, Dock.Bottom); root.Children.Add(note); root.Children.Add(Table);
        Content = root;
        manager.Changed += OnChanged;
        Reload();
    }

    void OnChanged(Guid id) => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
    {
        foreach (var row in rows) if (row.Spec.Id == id) row.Update(row.Spec, manager.Status(id));
        UpdateCommands(); UpdateState();
    });

    ServerProfile? Profile => App.Store.Profiles.FirstOrDefault(p => p.Id == session.Profile.Id);
    List<TunnelSpec> Specs => Profile?.Tunnels ?? session.Profile.Tunnels;
    bool Connected => session.State == SessionState.Connected;

    internal void Reload()
    {
        var selected = Selected?.Spec.Id;
        rows.Clear(); foreach (var s in Specs) rows.Add(new TunnelRow(s, manager.Status(s.Id)));
        Table.SelectedItem = rows.FirstOrDefault(r => r.Spec.Id == selected) ?? rows.FirstOrDefault();
        UpdateCommands(); UpdateState();
    }

    void UpdateState()
    {
        int open = rows.Count(r => r.Status.State == TunnelState.Running);
        state.Text = rows.Count == 0 ? "Туннелей пока нет — «Добавить…» создаёт первый." : $"Туннелей: {rows.Count}, открыто: {open}" + (Connected ? "" : " · сервер не подключён: открыть нельзя");
    }

    void UpdateCommands()
    {
        var r = Selected; var status = r is null ? TunnelStatus.Stopped : manager.Status(r.Spec.Id);
        commands["Open"].IsEnabled = !busy && r is not null && Connected && status.State is TunnelState.Stopped or TunnelState.Failed;
        commands["Close"].IsEnabled = !busy && r is not null && status.State is TunnelState.Running;
        commands["Add"].IsEnabled = !busy;
        commands["Edit"].IsEnabled = commands["Remove"].IsEnabled = !busy && r is not null && status.State is not (TunnelState.Running or TunnelState.Starting);
    }

    async Task Run(Func<Task> action)
    {
        if (busy) return; busy = true; UpdateCommands();
        try { await action(); }
        catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or ArgumentException or Renci.SshNet.Common.SshException) { state.Text = ErrorMessages.Describe(ex); Ui.Error(ErrorMessages.Describe(ex)); }
        finally { busy = false; UpdateCommands(); }
    }

    async Task SaveAsync() { if (Profile is not null) await App.Store.SaveAsync(); }

    async Task OpenAsync()
    {
        if (Selected is not { } r) return;
        await manager.StartAsync(r.Spec, lifetime.Token);
        state.Text = "Открыт: " + Tunnels.Describe(r.Spec, manager.Status(r.Spec.Id).BoundPort);
    }

    async Task CloseAsync() { if (Selected is { } r) await manager.StopAsync(r.Spec.Id); }

    TunnelSpec? Edit(TunnelSpec spec)
    {
        if (EditOverride is not null) return EditOverride(spec);
        var window = new TunnelEditorWindow(spec) { Owner = Window.GetWindow(this) };
        return window.ShowDialog() == true ? window.Result : null;
    }

    async Task AddAsync()
    {
        if (Edit(new TunnelSpec()) is not { } created) return;
        Specs.Add(created); if (!ReferenceEquals(Specs, session.Profile.Tunnels)) session.Profile.Tunnels.Add(created.Clone());
        await SaveAsync(); Reload(); Table.SelectedItem = rows.FirstOrDefault(x => x.Spec.Id == created.Id);
    }

    async Task EditAsync()
    {
        if (Selected is not { } r) return;
        if (Edit(r.Spec.Clone()) is not { } changed) return;
        Replace(Specs, changed); if (!ReferenceEquals(Specs, session.Profile.Tunnels)) Replace(session.Profile.Tunnels, changed);
        await SaveAsync(); Reload();
    }

    static void Replace(List<TunnelSpec> list, TunnelSpec changed) { int i = list.FindIndex(x => x.Id == changed.Id); if (i >= 0) list[i] = changed; else list.Add(changed); }

    async Task RemoveAsync()
    {
        if (Selected is not { } r) return;
        if (!(ConfirmOverride?.Invoke(r.Name) ?? Ui.Confirm($"Удалить туннель «{r.Name}» ({Tunnels.Describe(r.Spec)}) из списка сервера {session.Profile.Name}?"))) return;
        Specs.RemoveAll(x => x.Id == r.Spec.Id); session.Profile.Tunnels.RemoveAll(x => x.Id == r.Spec.Id);
        await SaveAsync(); Reload();
    }

    public void Dispose() { lifetime.Cancel(); manager.Changed -= OnChanged; }
}

/// <summary>Editor of one tunnel: direction, listening address and port, target. A non-loopback listener needs a confirmation tick.</summary>
public sealed class TunnelEditorWindow : Window
{
    readonly TunnelSpec spec;
    readonly TextBox name = Box("TunnelName"), bindAddress = Box("TunnelBindAddress"), bindPort = Box("TunnelBindPort"), targetHost = Box("TunnelTargetHost"), targetPort = Box("TunnelTargetPort");
    readonly RadioButton local = new() { Content = "Компьютер → сервер (ssh -L): порт открывается на этом компьютере", Name = "TunnelLocal", Margin = new Thickness(0, 2, 0, 2) };
    readonly RadioButton remote = new() { Content = "Сервер → компьютер (ssh -R): порт открывается на сервере", Name = "TunnelRemote", Margin = new Thickness(0, 2, 0, 2) };
    readonly CheckBox exposed = new() { Content = "Я понимаю, что порт будет доступен другим компьютерам в сети", Name = "TunnelExposed", Margin = new Thickness(0, 6, 0, 0) };
    readonly CheckBox auto = new() { Content = "Открывать при подключении к серверу", Name = "TunnelAuto", Margin = new Thickness(0, 10, 0, 0) };
    readonly TextBlock bindLabel = Ui.Text("", 12), targetLabel = Ui.Text("", 12), preview = Ui.Text("", 12), problem = Ui.Text("", 12);
    readonly Button save;
    public TunnelSpec? Result { get; private set; }
    internal string? Problem => problem.Text.Length > 0 ? problem.Text : null;
    internal string Preview => preview.Text;

    static TextBox Box(string name) => new() { Name = name, MinHeight = 32 };

    public TunnelEditorWindow(TunnelSpec spec)
    {
        this.spec = spec.Clone();
        Title = "Туннель"; Width = 600; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "DialogWindowStyle");
        var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(Ui.DialogHeading(spec.Name.Length > 0 ? "Туннель «" + spec.Name + "»" : "Новый туннель"));
        body.Children.Add(Labeled("Имя (необязательно)", name));
        body.Children.Add(local); body.Children.Add(remote);
        var bind = new Grid { Margin = new Thickness(0, 10, 0, 0) }; bind.ColumnDefinitions.Add(new ColumnDefinition()); bind.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) }); bind.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        var bindHost = Field(bindLabel, bindAddress); var bindP = Field(Ui.Text("Порт", 12), bindPort); Grid.SetColumn(bindP, 2); bind.Children.Add(bindHost); bind.Children.Add(bindP); body.Children.Add(bind);
        var target = new Grid(); target.ColumnDefinitions.Add(new ColumnDefinition()); target.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) }); target.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        var targetH = Field(targetLabel, targetHost); var targetP = Field(Ui.Text("Порт", 12), targetPort); Grid.SetColumn(targetP, 2); target.Children.Add(targetH); target.Children.Add(targetP); body.Children.Add(target);
        body.Children.Add(exposed); body.Children.Add(auto);
        preview.Name = "TunnelPreview"; preview.TextWrapping = TextWrapping.Wrap; preview.Margin = new Thickness(0, 12, 0, 0); preview.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); body.Children.Add(preview);
        problem.Name = "TunnelProblem"; problem.TextWrapping = TextWrapping.Wrap; problem.Margin = new Thickness(0, 6, 0, 0); problem.SetResourceReference(TextBlock.ForegroundProperty, "DangerText"); body.Children.Add(problem);
        var cancel = Ui.Button("Отмена", () => DialogResult = false); cancel.IsCancel = true;
        save = Ui.Button("Сохранить", Accept); save.Name = "TunnelSave"; save.IsDefault = true; save.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        Content = Ui.DialogSurface(body, cancel, save);
        name.Text = spec.Name; bindAddress.Text = spec.BindAddress; bindPort.Text = spec.BindPort == 0 && spec.Kind == TunnelKind.Remote ? "" : spec.BindPort.ToString(CultureInfo.InvariantCulture); targetHost.Text = spec.TargetHost; targetPort.Text = spec.TargetPort == 0 ? "" : spec.TargetPort.ToString(CultureInfo.InvariantCulture);
        (spec.Kind == TunnelKind.Local ? local : remote).IsChecked = true; auto.IsChecked = spec.StartOnConnect; exposed.IsChecked = Tunnels.IsExposed(spec);
        foreach (var b in new[] { name, bindAddress, bindPort, targetHost, targetPort }) b.TextChanged += (_, _) => Update();
        local.Checked += (_, _) => Update(); remote.Checked += (_, _) => Update(); exposed.Click += (_, _) => Update();
        Loaded += (_, _) => (bindPort.Text.Length == 0 ? bindPort : name).Focus();
        Update();
    }

    static FrameworkElement Labeled(string label, Control input) => Field(Ui.Text(label, 12), input);
    static FrameworkElement Field(TextBlock label, Control input)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) }; panel.Children.Add(label); input.Margin = new Thickness(0, 4, 0, 0); panel.Children.Add(input);
        System.Windows.Automation.AutomationProperties.SetName(input, label.Text.Length > 0 ? label.Text : input.Name); return panel;
    }

    TunnelSpec Current()
    {
        var s = spec.Clone();
        s.Name = name.Text.Trim(); s.Kind = remote.IsChecked == true ? TunnelKind.Remote : TunnelKind.Local;
        s.BindAddress = bindAddress.Text.Trim(); s.TargetHost = targetHost.Text.Trim(); s.StartOnConnect = auto.IsChecked == true;
        s.BindPort = int.TryParse(bindPort.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var bp) ? bp : -1;
        s.TargetPort = int.TryParse(targetPort.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var tp) ? tp : -1;
        if (bindPort.Text.Trim().Length == 0 && s.Kind == TunnelKind.Local) s.BindPort = 0;
        return s;
    }

    void Update()
    {
        bool isLocal = local.IsChecked == true;
        bindLabel.Text = isLocal ? "Слушать на этом компьютере (адрес)" : "Слушать на сервере (адрес)";
        targetLabel.Text = isLocal ? "Куда вести — адрес, как его видит сервер" : "Куда вести — адрес, как его видит этот компьютер";
        var s = Current();
        bool exposedNow = Tunnels.IsExposed(s); exposed.Visibility = exposedNow ? Visibility.Visible : Visibility.Collapsed;
        var text = Tunnels.Validate(s) ?? (exposedNow && exposed.IsChecked != true ? "Адрес " + s.BindAddress + " не локальный: порт откроется для других компьютеров. Подтвердите это флажком или укажите 127.0.0.1." : null);
        preview.Text = Tunnels.Validate(s) is null ? Tunnels.Describe(s) + (isLocal && s.BindPort == 0 ? " (порт 0 — свободный порт будет выбран при открытии)" : "") : "";
        problem.Text = text ?? ""; problem.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        save.IsEnabled = text is null;
    }

    void Accept() { var s = Current(); if (save.IsEnabled) { Result = s; DialogResult = true; } }
}
