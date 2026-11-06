using System.Globalization;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SSHExplorer.Core;
using SSHExplorer.Monitoring;

namespace SSHExplorer.App;

/// <summary>
/// "Сервер": facts about the connected server (read-only exec, no agent) and session actions:
/// refresh, reconnect, terminal and the guarded power actions.
/// </summary>
public sealed class ServerPanelView : UserControl
{
    readonly ServerSession session;
    readonly Func<Task>? reconnect;
    readonly Action? openTerminal;
    readonly Func<(int Transfers, int Documents)> pendingWork;
    readonly CardGridPanel cards = new() { Name = "ServerCards" };
    readonly TextBlock subtitle = Ui.Text("", 12);
    readonly TextBlock state = Ui.Text("", 12);
    readonly Border banner = new() { CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 12), BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed };
    readonly TextBlock bannerText = Ui.Text("");
    readonly StackPanel bannerActions = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
    readonly ProgressBar bannerProgress = new() { Height = 3, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    readonly Button refresh, reconnectButton, terminalButton, powerButton;
    readonly DataGrid storageTable = new() { Name = "ServerStorageTable", IsReadOnly = true, AutoGenerateColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column, MaxHeight = 220 };
    CancellationTokenSource? loading, powerWait;
    PowerOperation? operation;
    public ServerInfo? Info { get; private set; }
    internal PowerOperation? Operation => operation;
    internal FrameworkElement Banner => banner;
    internal Func<ServerInfo, PowerCapability, bool>? ConfirmOverride { get; set; }
    internal Func<CancellationToken, Task<bool>>? ServiceProbeOverride { get; set; }

    readonly Button updatesButton;
    /// <summary>Types a command into this server's terminal without running it (set by the tab).</summary>
    internal Func<string, Task>? TypeInTerminal { get; set; }
    internal Func<PackagesWindow, bool?>? UpdatesWindowOverride { get; set; }
    async Task ShowUpdatesAsync()
    {
        var window = new PackagesWindow(session.Profile.Name, async ct => (await session.ExecuteLongAsync(Packages.ReadCommand, TimeSpan.FromSeconds(90), ct)).Output, TypeInTerminal) { Owner = Window.GetWindow(this) };
        if (UpdatesWindowOverride is not null) UpdatesWindowOverride(window); else window.ShowDialog();
        await Task.CompletedTask;
    }
    public ServerPanelView(ServerSession session, Func<Task>? reconnect, Action? openTerminal, Func<(int Transfers, int Documents)> pendingWork)
    {
        this.session = session; this.reconnect = reconnect; this.openTerminal = openTerminal; this.pendingWork = pendingWork;
        SetResourceReference(BackgroundProperty, "Paper");
        refresh = Ui.AsyncCommandButton(Ui.RefreshGlyph, "Обновить", () => LoadAsync(), "Обновить сведения о сервере");
        reconnectButton = Ui.AsyncCommandButton(IconCatalog.Reconnect, "Переподключиться", () => reconnect?.Invoke() ?? Task.CompletedTask, "Закрыть это подключение и подключиться заново с проверкой ключа сервера");
        reconnectButton.IsEnabled = reconnect is not null;
        terminalButton = Ui.CommandButton(IconCatalog.Terminal, "Терминал", () => openTerminal?.Invoke(), "Открыть терминал этого сервера");
        terminalButton.IsEnabled = openTerminal is not null && session.ShellAvailable;
        powerButton = Ui.FlyoutButton(IconCatalog.Power, "Питание", BuildPowerMenu, "Перезагрузка и выключение сервера");
        powerButton.Name = "ServerPowerButton";
        updatesButton = Ui.AsyncCommandButton(IconCatalog.Restart, "Обновления…", ShowUpdatesAsync, "Обновления пакетов: сколько, какие из них — безопасности, нужна ли перезагрузка");
        updatesButton.Name = "ServerUpdatesButton"; updatesButton.IsEnabled = session.ShellAvailable;

        var title = Ui.Text("Сервер", 18, true);
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
        subtitle.Text = $"{session.Profile.Name} · {session.Profile.Username}@{session.Profile.Endpoint}";
        var heading = new StackPanel { Margin = new Thickness(4, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center }; heading.Children.Add(title); heading.Children.Add(subtitle);
        var commands = Ui.Row(heading, Ui.Line(), refresh, reconnectButton, terminalButton, updatesButton, Ui.Line(), powerButton);
        var bar = new Border { Child = new ScrollViewer { Content = commands, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false }, Padding = new Thickness(10, 4, 10, 4) };
        bar.SetResourceReference(StyleProperty, "CommandBarStyle");

        var bannerStack = new StackPanel(); bannerText.TextWrapping = TextWrapping.Wrap; bannerStack.Children.Add(bannerText); bannerStack.Children.Add(bannerProgress); bannerStack.Children.Add(bannerActions);
        banner.Child = bannerStack; AutomationProperties.SetLiveSetting(bannerText, AutomationLiveSetting.Assertive); AutomationProperties.SetName(banner, "Состояние операции питания");
        var body = new StackPanel { Margin = new Thickness(20, 16, 20, 16) }; body.Children.Add(banner); body.Children.Add(cards);
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        state.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); state.Margin = new Thickness(16, 6, 16, 6); state.TextTrimming = TextTrimming.CharacterEllipsis;
        var footer = new Border { Child = state }; footer.SetResourceReference(StyleProperty, "StatusBarStyle");
        var root = new DockPanel(); DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); root.Children.Add(scroll);
        Content = root;

        storageTable.SetResourceReference(StyleProperty, "ProcessTableStyle");
        MainWindow.AddColumn(storageTable, "Точка монтирования", "MountPoint", 170); MainWindow.AddColumn(storageTable, "Файловая система", "FileSystem", 160);
        MainWindow.AddColumn(storageTable, "Всего", "TotalText", 90); MainWindow.AddColumn(storageTable, "Свободно", "AvailableText", 90); MainWindow.AddColumn(storageTable, "Занято", "UsedPercentText", 80);
        TableSorting.Attach(storageTable, "server.storage", [new("MountPoint", r => ((StorageView)r).MountPoint, true), new("FileSystem", r => ((StorageView)r).FileSystem, true), new("TotalText", r => ((StorageView)r).TotalBytes), new("AvailableText", r => ((StorageView)r).AvailableBytes), new("UsedPercentText", r => ((StorageView)r).UsedPercent)], "MountPoint");
        RenderPlaceholder("Загрузка сведений…");
        Loaded += async (_, _) => { if (Info is null && loading is null) await Ui.Guard(() => LoadAsync()); };
        Unloaded += (_, _) => loading?.Cancel();
    }

    public sealed record StorageView(string MountPoint, string FileSystem, long? TotalBytes, long? AvailableBytes)
    {
        public double? UsedPercent => TotalBytes is > 0 && AvailableBytes is { } free ? 100d * (TotalBytes.Value - free) / TotalBytes.Value : null;
        public string TotalText => Ui.Size(TotalBytes); public string AvailableText => Ui.Size(AvailableBytes);
        public string UsedPercentText => UsedPercent is { } value ? value.ToString("0", CultureInfo.CurrentCulture) + " %" : "—";
    }

    public async Task LoadAsync(CancellationToken external = default)
    {
        loading?.Cancel(); var cts = CancellationTokenSource.CreateLinkedTokenSource(session.Lifetime, external); loading = cts;
        refresh.IsEnabled = false; state.Text = "Чтение сведений о сервере…";
        try
        {
            if (session.State != SessionState.Connected || !session.ShellAvailable)
            { RenderPlaceholder(session.ShellAvailable ? "Нет подключения к серверу. Переподключитесь, чтобы обновить сведения." : "Сервер не разрешает выполнять команды (SSH exec): доступны только файлы. Сведения о системе и управление питанием недоступны."); state.Text = "Сведения недоступны"; return; }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = session.IsWindows ? await session.ExecuteLongAsync(WindowsInfo.Command, TimeSpan.FromSeconds(45), cts.Token) : await session.ExecuteAsync(ServerInfoCollector.Command, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            updatesButton.Visibility = session.IsWindows ? Visibility.Collapsed : Visibility.Visible;
            Info = session.IsWindows ? WindowsInfo.Parse(result.Output, SteadyClock.Now) : ServerInfoCollector.Parse(result.Output, SteadyClock.Now);
            Render(Info);
            state.Text = $"Обновлено {DateTime.Now:HH:mm:ss} · запрос {watch.ElapsedMilliseconds} мс · только чтение, на сервер ничего не устанавливается";
            await CompletePendingVerificationAsync();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception error) { RenderPlaceholder("Не удалось получить сведения: " + ErrorMessages.Describe(error)); state.Text = "Ошибка чтения сведений"; }
        finally { if (ReferenceEquals(loading, cts)) { loading = null; refresh.IsEnabled = true; } cts.Dispose(); }
    }

    /// <summary>After a reconnect, compares the new boot with the one recorded before a reboot request.</summary>
    async Task CompletePendingVerificationAsync()
    {
        if (Info is null || !PowerVerificationRegistry.IsPending(session.Profile.Id, session.Profile.Endpoint)) return;
        var after = new BootMarker(Info.BootId, Info.Uptime, Info.CollectedAt);
        if (PowerVerificationRegistry.Complete(session.Profile.Id, session.Profile.Endpoint, after) is { } verdict)
            ShowBanner(verdict.Stage == PowerStage.Confirmed ? "Success" : "Warning", verdict.Message, false);
        await Task.CompletedTask;
    }

    void RenderPlaceholder(string text)
    {
        cards.Children.Clear();
        var message = Ui.Text(text); message.TextWrapping = TextWrapping.Wrap; message.MaxWidth = 560; message.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); message.Margin = new Thickness(4, 24, 4, 24); Grid.SetColumnSpan(message, int.MaxValue);
        cards.Children.Add(message);
    }

    void Render(ServerInfo info)
    {
        cards.Children.Clear();
        string Or(string? value) => string.IsNullOrWhiteSpace(value) ? "Недоступно" : value;
        string Duration(TimeSpan? value) => value is not { } v ? "Недоступно" : v.TotalDays >= 1 ? $"{(int)v.TotalDays} д {v.Hours} ч {v.Minutes} мин" : v.TotalHours >= 1 ? $"{(int)v.TotalHours} ч {v.Minutes} мин" : $"{v.Minutes} мин {v.Seconds} с";
        cards.Children.Add(Card("Система", IconCatalog.Server,
            ("Имя узла", Or(info.Hostname)), ("ОС", Or(info.OsName)), ("Версия", Or(info.OsVersion)), ("Ядро", Or(info.Kernel)), ("Архитектура", Or(info.Architecture)),
            ("Время работы", Duration(info.Uptime)), ("Инициализация", info.Systemd ? "systemd" : "без systemd")));
        string load = info.Load1 is null ? "Недоступно" : string.Join(" / ", new[] { info.Load1, info.Load5, info.Load15 }.Select(v => v?.ToString("0.00", CultureInfo.CurrentCulture) ?? "—"));
        string perCore = info.Load1 is { } l1 && info.CpuCount is > 0 ? (l1 / info.CpuCount.Value).ToString("0.00", CultureInfo.CurrentCulture) + " на ядро" : "";
        cards.Children.Add(Card("Процессор", IconCatalog.Performance,
            ("Модель", Or(info.CpuModel)), ("Логических ЦП", info.CpuCount?.ToString(CultureInfo.CurrentCulture) ?? "Недоступно"),
            ("Загрузка 1 / 5 / 15 мин", load), ("", perCore.Length > 0 ? perCore + ". Это длина очереди задач, а не процент ЦП; процент — в «Производительности»." : "Средняя загрузка — длина очереди задач, а не процент ЦП.")));
        string swap = info.SwapTotalBytes is > 0 ? $"{Ui.Size(info.SwapUsedBytes)} из {Ui.Size(info.SwapTotalBytes)}" : info.SwapTotalBytes == 0 ? "Не настроен" : "Недоступно";
        cards.Children.Add(Card("Память", IconCatalog.ViewLarge,
            ("Всего", Ui.Size(info.MemoryTotalBytes)), ("Доступно", Ui.Size(info.MemoryAvailableBytes)), ("Подкачка", swap)));
        var addresses = info.Addresses.Count == 0 ? [("Адреса", "Недоступно")] : info.Addresses.Select(a => (a.Interface + " · " + a.Family, a.Address)).ToArray();
        cards.Children.Add(Card("Сеть", IconCatalog.Network, addresses));
        cards.Children.Add(Card("Доступ", Ui.PropertiesGlyph,
            ("Пользователь SSH", info.User is null ? "Недоступно" : info.User + (info.IsRoot ? " (root)" : info.UserId is { } uid ? $" (uid {uid})" : "")),
            ("Перезагрузка", info.Reboot.Available ? "Доступна: " + info.Reboot.Command : "Недоступна"), ("", info.Reboot.Explanation),
            ("Выключение", info.PowerOff.Available ? "Доступно: " + info.PowerOff.Command : "Недоступно"),
            ("Команды", info.Tools.Count == 0 ? "—" : string.Join(", ", info.Tools))));
        storageTable.ItemsSource = info.Storage.Select(s => new StorageView(s.MountPoint, s.FileSystem, s.TotalBytes, s.AvailableBytes)).ToList();
        var storageCard = CardShell("Хранилища", IconCatalog.Storage); Grid.SetColumnSpan(storageCard, 2); ((StackPanel)storageCard.Child).Children.Add(info.Storage.Count == 0 ? Muted("df недоступен или не вернул данных.") : storageTable);
        if (storageTable.Parent is Panel old && !ReferenceEquals(old, storageCard.Child)) old.Children.Remove(storageTable);
        cards.Children.Add(storageCard);
        if (info.Limitations.Count > 0) cards.Children.Add(Card("Ограничения", Ui.PropertiesGlyph, info.Limitations.Select(l => ("", l)).ToArray()));
    }

    internal static bool IsLongToken(string value) => value.Length > 20 && !value.Any(char.IsWhiteSpace);

    static TextBlock Muted(string text) { var t = Ui.Text(text, 12); t.TextWrapping = TextWrapping.Wrap; t.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); return t; }

    static Border CardShell(string title, string icon)
    {
        var card = new Border { Padding = new Thickness(16, 12, 16, 14), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "Surface"); card.SetResourceReference(Border.BorderBrushProperty, "Edge");
        var stack = new StackPanel(); var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var glyph = Ui.Icon(icon, 16, false); glyph.Margin = new Thickness(0, 0, 10, 0); DockPanel.SetDock(glyph, Dock.Left); head.Children.Add(glyph); head.Children.Add(Ui.Text(title, 14, true));
        stack.Children.Add(head); card.Child = stack; AutomationProperties.SetName(card, title);
        return card;
    }

    static Border Card(string title, string icon, params (string Label, string Value)[] rows)
    {
        var card = CardShell(title, icon); var stack = (StackPanel)card.Child;
        foreach (var (label, value) in rows)
        {
            if (label.Length == 0) { var note = Muted(value); note.Margin = new Thickness(0, 2, 0, 4); stack.Children.Add(note); continue; }
            var caption = Ui.Text(label, 12); caption.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); caption.TextWrapping = TextWrapping.Wrap; caption.VerticalAlignment = VerticalAlignment.Center;
            var text = new TextBox { Text = value, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(text, label);
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            if (IsLongToken(value))
            {
                // An unbreakable value (IPv6 address, path, hash) gets the card's full width under its label
                // instead of being split mid-token inside the narrow value column.
                row.Name = "StackedFact"; row.RowDefinitions.Add(new RowDefinition()); row.RowDefinitions.Add(new RowDefinition());
                caption.Margin = new Thickness(0, 0, 0, 2); row.Children.Add(caption); Grid.SetRow(text, 1); row.Children.Add(text);
            }
            else
            {
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) }); row.ColumnDefinitions.Add(new ColumnDefinition());
                caption.Margin = new Thickness(0, 0, 12, 0); row.Children.Add(caption); Grid.SetColumn(text, 1); row.Children.Add(text);
            }
            stack.Children.Add(row);
        }
        return card;
    }

    ContextMenu BuildPowerMenu()
    {
        var menu = new ContextMenu();
        var busy = operation is { Stage: not (PowerStage.Rejected or PowerStage.Confirmed or PowerStage.NotConfirmed or PowerStage.Unreachable or PowerStage.Abandoned or PowerStage.Disconnected or PowerStage.StillReachable or PowerStage.ResultUnknown) };
        foreach (var capability in new[] { Info?.Reboot, Info?.PowerOff })
        {
            if (capability is null) continue;
            var label = capability.Action == PowerAction.Reboot ? "Перезагрузить сервер…" : "Выключить сервер…";
            var item = Ui.AsyncMenu(label, () => RequestPowerAsync(capability), glyph: capability.Action == PowerAction.Reboot ? IconCatalog.Restart : IconCatalog.Power);
            item.IsEnabled = capability.Available && !busy && session.State == SessionState.Connected;
            item.ToolTip = capability.Available ? capability.Explanation : "Недоступно: " + capability.Explanation; ToolTipService.SetShowOnDisabled(item, true);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = Info is null ? "Сведения ещё загружаются" : "Нет данных о правах", IsEnabled = false });
        return menu;
    }

    internal async Task RequestPowerAsync(PowerCapability capability)
    {
        if (Info is null || !capability.Available) return;
        // Read the boot identity immediately before the request; a stale value would weaken verification.
        await LoadAsync(); if (Info is null) return;
        capability = capability.Action == PowerAction.Reboot ? Info.Reboot : Info.PowerOff;
        if (!capability.Available) { Ui.Error(capability.Explanation); return; }
        var confirmed = ConfirmOverride?.Invoke(Info, capability) ?? PowerConfirmDialog.Ask(Window.GetWindow(this), session.Profile, Info, capability, pendingWork());
        if (!confirmed) return;
        var before = new BootMarker(Info.BootId, Info.Uptime, Info.CollectedAt);
        operation = new PowerOperation(capability, before,
            async (command, ct) => { var r = await session.ExecuteAsync(command, ct); return new ExecResult(r.Output, r.Error, r.ExitStatus); },
            SessionAliveAsync, ServiceProbeOverride ?? ProbeServiceAsync);
        operation.Progress += progress => Dispatcher.BeginInvoke(() => ShowProgress(progress));
        powerWait = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        if (capability.Action == PowerAction.Reboot) PowerVerificationRegistry.Register(session.Profile.Id, session.Profile.Endpoint, before);
        var id = Guid.NewGuid();
        await LocalDiagnostics.RecordOperationAsync(id, "server.power." + capability.Method, "requested");
        MetricsHub.Note(session.Profile.Id, "change", capability.Action == PowerAction.Reboot ? "Перезагрузка из приложения" : "Выключение из приложения");
        var final = await operation.RunAsync(powerWait.Token);
        await LocalDiagnostics.RecordOperationAsync(id, "server.power." + capability.Method, final.ToString());
    }

    async Task<bool> SessionAliveAsync(CancellationToken ct)
    {
        if (session.State != SessionState.Connected) return false;
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct); probe.CancelAfter(TimeSpan.FromSeconds(5));
        try { var result = await session.ExecuteAsync("true", probe.Token); return result.ExitStatus == 0; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
    }

    /// <summary>TCP connect plus the "SSH-" banner; no login, credentials or host key are involved here.</summary>
    async Task<bool> ProbeServiceAsync(CancellationToken ct)
    {
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct); probe.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(session.Profile.Host, session.Profile.Port, probe.Token);
            var buffer = new byte[4]; var stream = client.GetStream(); int read = 0;
            while (read < 4) { int n = await stream.ReadAsync(buffer.AsMemory(read), probe.Token); if (n == 0) break; read += n; }
            return read == 4 && buffer[0] == (byte)'S' && buffer[1] == (byte)'S' && buffer[2] == (byte)'H' && buffer[3] == (byte)'-';
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
    }

    void ShowProgress(PowerProgress progress)
    {
        bool terminal = progress.Stage is PowerStage.Rejected or PowerStage.Confirmed or PowerStage.NotConfirmed or PowerStage.Unreachable or PowerStage.Abandoned or PowerStage.StillReachable or PowerStage.ResultUnknown
            || progress.Stage == PowerStage.Disconnected && operation?.Action == PowerAction.PowerOff || progress.Stage == PowerStage.ServiceReachable;
        var kind = progress.Stage switch
        {
            PowerStage.Rejected or PowerStage.Unreachable => "Error",
            PowerStage.ResultUnknown or PowerStage.StillReachable or PowerStage.NotConfirmed or PowerStage.Abandoned => "Warning",
            PowerStage.Confirmed or PowerStage.ServiceReachable => "Success",
            _ => "Info",
        };
        ShowBanner(kind, progress.Message, !terminal);
        if (!terminal) { var stop = Ui.Button("Прекратить ожидание", () => powerWait?.Cancel(), "Перестать ждать результата. Отправленная команда этим не отменяется."); bannerActions.Children.Add(stop); }
        if (progress.Stage == PowerStage.ServiceReachable && reconnect is not null) { var again = Ui.AsyncButton("Переподключиться и проверить", reconnect); again.SetResourceReference(StyleProperty, "PrimaryButtonStyle"); bannerActions.Children.Add(again); }
        state.Text = progress.Stage switch { PowerStage.Sending => "Отправка команды питания…", PowerStage.WaitingForService => "Ожидание запуска сервера…", _ => state.Text };
    }

    void ShowBanner(string kind, string text, bool working)
    {
        banner.Visibility = Visibility.Visible; bannerText.Text = text; bannerActions.Children.Clear();
        bannerProgress.Visibility = working ? Visibility.Visible : Visibility.Collapsed; bannerProgress.IsIndeterminate = working && UiMotion.Enabled;
        bool contrast = SystemParameters.HighContrast; bool dark = Theme.Current == "Dark" || Theme.Current == "System" && ((TryFindResource("Paper") as SolidColorBrush)?.Color.R ?? 255) < 128;
        Color background = kind switch { "Error" => dark ? Color.FromRgb(0x44, 0x27, 0x26) : Color.FromRgb(0xFD, 0xE7, 0xE9), "Warning" => dark ? Color.FromRgb(0x43, 0x35, 0x19) : Color.FromRgb(0xFF, 0xF4, 0xCE), "Success" => dark ? Color.FromRgb(0x1E, 0x3B, 0x26) : Color.FromRgb(0xDF, 0xF6, 0xDD), _ => dark ? Color.FromRgb(0x2B, 0x2F, 0x36) : Color.FromRgb(0xF0, 0xF4, 0xF9) };
        if (contrast) { banner.Background = SystemColors.WindowBrush; banner.BorderBrush = SystemColors.WindowTextBrush; }
        else { banner.Background = new SolidColorBrush(background); banner.SetResourceReference(Border.BorderBrushProperty, "Edge"); }
    }
}

/// <summary>Explicit confirmation naming the exact server; Cancel is the default and Enter does not confirm.</summary>
public static class PowerConfirmDialog
{
    public static bool Ask(Window? owner, ServerProfile profile, ServerInfo info, PowerCapability capability, (int Transfers, int Documents) pending)
    {
        bool reboot = capability.Action == PowerAction.Reboot;
        var window = Dialogs.Window(reboot ? "Перезагрузка сервера" : "Выключение сервера", 520, owner);
        var body = new StackPanel { Margin = new Thickness(24, 20, 24, 12) };
        body.Children.Add(Ui.DialogHeading((reboot ? "Перезагрузить «" : "Выключить «") + profile.Name + "»?"));
        var target = Ui.Text($"{profile.Username}@{profile.Endpoint}" + (info.Hostname is { } host ? $" · узел {host}" : "") + (info.OsName is { } os ? $" · {os}" : ""), 12);
        target.TextWrapping = TextWrapping.Wrap; target.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); body.Children.Add(target);
        void Line(string text, bool strong = false) { var t = Ui.Text(text, 14, strong); t.TextWrapping = TextWrapping.Wrap; t.Margin = new Thickness(0, 10, 0, 0); body.Children.Add(t); }
        Line("Будет отправлена одна команда: " + capability.Command + ". " + capability.Explanation);
        Line(reboot ? "Все подключения к серверу будут разорваны. После загрузки SSH Explorer предложит переподключиться и сверит boot_id." : "Все подключения будут разорваны. Включить сервер обратно через SSH нельзя; результат выключения по SSH подтвердить невозможно.");
        if (pending.Transfers > 0) Line($"Незавершённых передач с этим сервером: {pending.Transfers}. Они будут прерваны.", true);
        if (pending.Documents > 0) Line($"Документов с неотправленными правками на этом сервере: {pending.Documents}. Локальные копии сохранятся, отправка остановится.", true);
        bool result = false;
        var confirm = Ui.Button(reboot ? "Перезагрузить" : "Выключить", () => { result = true; window.DialogResult = true; }); confirm.Name = "PowerConfirm";
        confirm.SetResourceReference(FrameworkElement.StyleProperty, "DangerButtonStyle");
        var cancel = Ui.Button("Отмена", () => window.DialogResult = false); cancel.IsCancel = true; cancel.IsDefault = true; cancel.Name = "PowerCancel";
        window.Content = Ui.DialogSurface(body, confirm, cancel);
        window.Loaded += (_, _) => cancel.Focus();
        window.ShowDialog();
        return result;
    }
}
