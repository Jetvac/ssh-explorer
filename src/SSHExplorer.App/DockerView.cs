using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>
/// «Docker» module: containers of this server, their actions and logs, and saved run parameters (templates) per container,
/// including environment variables, for one-click «Пересоздать с параметрами».
/// </summary>
public sealed class DockerView : UserControl, IDisposable
{
    readonly DockerService docker;
    readonly ObservableCollection<DockerContainer> rows = [];
    readonly ListCollectionView view;
    readonly TextBox search = new() { Name = "DockerSearch", MinHeight = 32, MinWidth = 220 };
    readonly TextBlock subtitle = Ui.Text("", 12), state = Ui.Text("", 12), logTitle = Ui.Text("Журнал", 14, true);
    readonly TextBox log = new() { Name = "DockerLog", IsReadOnly = true, VerticalContentAlignment = VerticalAlignment.Top, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Padding = new Thickness(12, 8, 12, 8) };
    readonly ComboBox tail = new() { ItemsSource = new[] { 200, 1000, 5000 }, SelectedIndex = 0, MinWidth = 90, Name = "DockerLogTail" };
    readonly CheckBox follow = new() { Content = "Следить", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Name = "DockerLogFollow" };
    readonly DispatcherTimer followTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly Dictionary<string, Button> commands = [];
    readonly Border placeholder = new() { Padding = new Thickness(24), Visibility = Visibility.Collapsed };
    readonly Grid content = new();
    readonly CancellationTokenSource lifetime = new();
    bool loaded, busy;
    internal DataGrid Table { get; } = new() { Name = "DockerTable", IsReadOnly = true, AutoGenerateColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single };
    internal Func<DockerTemplate, DockerContainer?, IReadOnlyList<string>, DockerTemplateWindow.Result?>? EditOverride { get; set; }
    internal Func<string, bool>? ConfirmOverride { get; set; }
    DockerContainer? Selected => Table.SelectedItem as DockerContainer;

    public DockerView(DockerService service)
    {
        docker = service; Name = "DockerView";
        SetResourceReference(BackgroundProperty, "Paper");
        var title = Ui.Text("Docker", 18, true);
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); subtitle.TextTrimming = TextTrimming.CharacterEllipsis; subtitle.MaxWidth = 340; subtitle.Text = docker.Session.Profile.Name + " · " + docker.Session.Profile.Username + "@" + docker.Session.Profile.Endpoint;
        var heading = new StackPanel { Margin = new Thickness(4, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center }; heading.Children.Add(title); heading.Children.Add(subtitle);
        // A responsive row: commands that do not fit go into «…», so nothing is cut off or scrolls over the heading.
        var bar = new ResponsiveCommandBar { Name = "DockerCommandBar", VerticalAlignment = VerticalAlignment.Center };
        void Command(string key, string glyph, string label, Func<Task> action, string tooltip, int priority)
        {
            var b = Ui.AsyncCommandButton(glyph, label, () => Run(action), tooltip); b.Name = "Docker" + key; commands[key] = b;
            bar.Add(key, b, () => Ui.AsyncMenu(label, () => Run(action), glyph: glyph), priority);
        }
        Command("Refresh", Ui.RefreshGlyph, "Обновить", () => RefreshAsync(), "Обновить список контейнеров", 100);
        Command("Start", "", "Запустить", () => Act(c => docker.StartAsync(c.Id, lifetime.Token)), "docker start", 90);
        Command("Stop", "", "Остановить", () => Act(c => docker.StopAsync(c.Id, lifetime.Token)), "docker stop", 90);
        Command("Recreate", IconCatalog.Reconnect, "Пересоздать с параметрами", RecreateAsync, "Удалить и запустить заново по сохранённым параметрам", 80);
        Command("Edit", IconCatalog.Settings, "Параметры запуска…", EditAsync, "Изменить env, порты, тома и образ; сохранить и пересоздать", 70);
        Command("Restart", IconCatalog.Restart, "Перезапустить", () => Act(c => docker.RestartAsync(c.Id, lifetime.Token)), "docker restart — те же параметры", 60);
        Command("New", Ui.AddGlyph, "Новый контейнер…", NewAsync, "Задать параметры и запустить новый контейнер", 50);
        Command("Remove", Ui.DeleteGlyph, "Удалить…", RemoveAsync, "Удалить контейнер (данные в его слое будут потеряны)", 10);
        var barRow = new DockPanel(); DockPanel.SetDock(heading, Dock.Left); barRow.Children.Add(heading); var divider = Ui.Line(); DockPanel.SetDock(divider, Dock.Left); barRow.Children.Add(divider); barRow.Children.Add(bar);
        var barHost = new Border { Child = barRow, Padding = new Thickness(10, 4, 10, 4) };
        barHost.SetResourceReference(StyleProperty, "CommandBarStyle");

        var searchHost = new Grid { Margin = new Thickness(16, 10, 16, 6) }; searchHost.Children.Add(search);
        var hint = Ui.Text("Имя, образ, порт или состояние", 13); hint.IsHitTestVisible = false; hint.Margin = new Thickness(10, 0, 6, 0); hint.VerticalAlignment = VerticalAlignment.Center; hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); searchHost.Children.Add(hint);
        search.TextChanged += (_, _) => { hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; view!.Refresh(); UpdateState(); };
        System.Windows.Automation.AutomationProperties.SetName(search, "Поиск контейнеров");

        Table.SetResourceReference(StyleProperty, "ProcessTableStyle"); Table.Margin = new Thickness(16, 0, 16, 0);
        MainWindow.AddColumn(Table, "Имя", nameof(DockerContainer.Name), 180); MainWindow.AddColumn(Table, "Состояние", nameof(DockerContainer.State), 110);
        MainWindow.AddColumn(Table, "Образ", nameof(DockerContainer.Image), 200); MainWindow.AddColumn(Table, "Статус", nameof(DockerContainer.Status), 170);
        MainWindow.AddColumn(Table, "Порты", nameof(DockerContainer.Ports), 220);
        var last = Table.Columns[^1]; last.MinWidth = 160; last.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
        view = (ListCollectionView)CollectionViewSource.GetDefaultView(rows);
        view.Filter = item => search.Text.Trim() is not { Length: > 0 } q || ((DockerContainer)item).SearchText.Contains(q, StringComparison.CurrentCultureIgnoreCase);
        Table.ItemsSource = view;
        TableSorting.Attach(Table, "docker.containers", [new(nameof(DockerContainer.Name), r => ((DockerContainer)r).Name, true), new(nameof(DockerContainer.State), r => ((DockerContainer)r).IsRunning ? 0 : 1),
            new(nameof(DockerContainer.Image), r => ((DockerContainer)r).Image, true), new(nameof(DockerContainer.Status), r => ((DockerContainer)r).Status), new(nameof(DockerContainer.Ports), r => ((DockerContainer)r).Ports)], nameof(DockerContainer.State));
        Table.SelectionChanged += async (_, _) => { UpdateCommands(); await Ui.Guard(() => LoadLogAsync()); };
        Table.MouseDoubleClick += async (_, _) => { if (Selected is not null) await Ui.Guard(() => Run(EditAsync)); };

        var logBar = new DockPanel { Margin = new Thickness(16, 8, 16, 4) };
        var logRefresh = Ui.AsyncCommandButton(Ui.RefreshGlyph, "Обновить журнал", () => LoadLogAsync(), "Заново прочитать журнал");
        var logTools = Ui.Row(Ui.Text("Строк: ", 12), tail, follow, logRefresh); DockPanel.SetDock(logTools, Dock.Right); logBar.Children.Add(logTools); logBar.Children.Add(logTitle);
        tail.SelectionChanged += async (_, _) => await Ui.Guard(() => LoadLogAsync());
        follow.Checked += (_, _) => followTimer.Start(); follow.Unchecked += (_, _) => followTimer.Stop();
        followTimer.Tick += async (_, _) => { if (IsVisible && Selected is not null) await Ui.Guard(() => LoadLogAsync()); };
        var logBorder = new Border { Child = log, BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(16, 0, 16, 0) }; logBorder.SetResourceReference(Border.BorderBrushProperty, "Edge"); log.SetResourceReference(BackgroundProperty, "Surface");

        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 120 });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 100 });
        Grid.SetRow(searchHost, 0); Grid.SetRow(Table, 1); var splitter = new GridSplitter { Height = 6, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent }; Grid.SetRow(splitter, 2); Grid.SetRow(logBar, 3); Grid.SetRow(logBorder, 4);
        content.Children.Add(searchHost); content.Children.Add(Table); content.Children.Add(splitter); content.Children.Add(logBar); content.Children.Add(logBorder);

        state.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); state.Margin = new Thickness(16, 6, 16, 6); state.TextTrimming = TextTrimming.CharacterEllipsis; state.Name = "DockerState";
        var footer = new Border { Child = state }; footer.SetResourceReference(StyleProperty, "StatusBarStyle");
        var root = new DockPanel(); DockPanel.SetDock(barHost, Dock.Top); root.Children.Add(barHost); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var body = new Grid(); body.Children.Add(content); body.Children.Add(placeholder); root.Children.Add(body);
        Content = root;
        UpdateCommands();
        IsVisibleChanged += async (_, e) => { if (e.NewValue is true && !loaded) await Ui.Guard(() => RefreshAsync()); if (e.NewValue is false) followTimer.Stop(); else if (follow.IsChecked == true) followTimer.Start(); };
    }

    async Task Run(Func<Task> action)
    {
        if (busy) return; busy = true; UpdateCommands();
        try { await action(); }
        catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or ArgumentException or Renci.SshNet.Common.SshException or System.Text.Json.JsonException) { state.Text = ErrorMessages.Describe(ex); Ui.Error(ErrorMessages.Describe(ex)); }
        finally { busy = false; UpdateCommands(); }
    }

    public async Task RefreshAsync()
    {
        loaded = true;
        if (!docker.Available)
        {
            // Opening the section never raises a dialog: an unreachable server or missing Docker is explained in place.
            bool found;
            try { found = await docker.ProbeAsync(lifetime.Token); }
            catch (Exception ex) when (ex is not OperationCanceledException) { ShowProblem("Не удалось проверить Docker: " + ErrorMessages.Describe(ex)); return; }
            if (!found) { ShowProblem(docker.Problem ?? "Docker недоступен."); return; }
        }
        placeholder.Visibility = Visibility.Collapsed; content.Visibility = Visibility.Visible;
        var selectedId = Selected?.Id;
        var list = await docker.ListAsync(lifetime.Token);
        rows.Clear(); foreach (var c in list) rows.Add(c);
        Table.SelectedItem = rows.FirstOrDefault(c => c.Id == selectedId);
        UpdateState(); UpdateCommands();
    }

    void ShowProblem(string text)
    {
        var message = Ui.Text(text, 14); message.TextWrapping = TextWrapping.Wrap; message.MaxWidth = 640; message.HorizontalAlignment = HorizontalAlignment.Left; message.Name = "DockerProblem";
        placeholder.Child = message; placeholder.Visibility = Visibility.Visible; content.Visibility = Visibility.Collapsed;
        state.Text = "Docker недоступен"; UpdateCommands();
    }

    void UpdateState()
    {
        if (!docker.Available) return;
        int running = rows.Count(c => c.IsRunning);
        state.Text = (search.Text.Trim().Length > 0 ? $"Найдено {view.Count} из {rows.Count}" : $"Контейнеров: {rows.Count}, запущено: {running}")
            + $" · Docker {docker.Version}" + $" · сохранённых параметров: {Templates().Count}";
    }

    void UpdateCommands()
    {
        var c = Selected; bool ok = docker.Available && !busy;
        commands["Refresh"].IsEnabled = !busy;
        commands["Start"].IsEnabled = ok && c is { IsRunning: false };
        commands["Stop"].IsEnabled = ok && c is { IsRunning: true };
        commands["Restart"].IsEnabled = ok && c is { IsRunning: true };
        commands["Remove"].IsEnabled = ok && c is not null;
        commands["New"].IsEnabled = ok;
        commands["Edit"].IsEnabled = ok && c is not null;
        commands["Recreate"].IsEnabled = ok && c is not null && TemplateFor(c.Name) is not null;
        commands["Recreate"].ToolTip = c is not null && TemplateFor(c.Name) is null ? "Сначала сохраните параметры запуска этого контейнера («Параметры запуска…»)" : "Удалить и запустить заново по сохранённым параметрам";
    }

    async Task Act(Func<DockerContainer, Task> action) { if (Selected is { } c) { await action(c); await RefreshAsync(); } }

    async Task RemoveAsync()
    {
        if (Selected is not { } c) return;
        if (!Confirm($"Удалить контейнер «{c.Name}» ({c.Image}) на {docker.Session.Profile.Name}?\n\n{(c.IsRunning ? "Он запущен и будет остановлен. " : "")}Изменения внутри контейнера будут потеряны; именованные тома и каталоги хоста не удаляются.")) return;
        await docker.RemoveAsync(c.Id, force: c.IsRunning, lifetime.Token); await RefreshAsync();
    }

    bool Confirm(string text) => ConfirmOverride?.Invoke(text) ?? Ui.Confirm(text);

    async Task NewAsync()
    {
        var result = Edit(new DockerTemplate(), null);
        if (result is null) return;
        await SaveTemplateAsync(result.Template);
        if (result.Run) { await docker.RunAsync(result.Template, replaceExisting: false, lifetime.Token); await RefreshAsync(); }
    }

    async Task EditAsync()
    {
        if (Selected is not { } c) return;
        var template = TemplateFor(c.Name)?.Clone() ?? await docker.TemplateFromAsync(c.Id, lifetime.Token);
        var result = Edit(template, c);
        if (result is null) return;
        await SaveTemplateAsync(result.Template);
        if (result.Run)
        {
            if (!Confirm($"Пересоздать контейнер «{c.Name}» с новыми параметрами?\n\nОн будет остановлен и удалён, затем запущен из образа {result.Template.Image}. Данные, записанные внутри контейнера (не в тома), будут потеряны.")) { UpdateState(); UpdateCommands(); return; }
            await docker.RunAsync(result.Template, replaceExisting: true, lifetime.Token); await RefreshAsync();
        }
        UpdateState(); UpdateCommands();
    }

    async Task RecreateAsync()
    {
        if (Selected is not { } c || TemplateFor(c.Name) is not { } template) return;
        if (!Confirm($"Пересоздать «{c.Name}» по сохранённым параметрам?\n\nОбраз {template.Image}, переменных окружения: {template.Env.Count}, портов: {template.Ports.Count}, томов: {template.Volumes.Count}.\nДанные, записанные внутри контейнера (не в тома), будут потеряны.")) return;
        await docker.RunAsync(template, replaceExisting: true, lifetime.Token); await RefreshAsync();
    }

    DockerTemplateWindow.Result? Edit(DockerTemplate template, DockerContainer? existing)
    {
        var names = rows.Where(r => existing is null || r.Id != existing.Id).Select(r => r.Name).ToArray();
        if (EditOverride is not null) return EditOverride(template, existing, names);
        var window = new DockerTemplateWindow(template, existing is not null, names) { Owner = Window.GetWindow(this) };
        return window.ShowDialog() == true ? window.Accepted : null;
    }

    ServerProfile? Profile => App.Store.Profiles.FirstOrDefault(p => p.Id == docker.Session.Profile.Id);
    IReadOnlyList<DockerTemplate> Templates() => Profile?.DockerTemplates ?? docker.Session.Profile.DockerTemplates;
    DockerTemplate? TemplateFor(string name) => Templates().FirstOrDefault(t => string.Equals(t.ContainerName, name, StringComparison.Ordinal));

    async Task SaveTemplateAsync(DockerTemplate template)
    {
        template.UpdatedUtc = DateTime.UtcNow;
        foreach (var list in new[] { Profile?.DockerTemplates, docker.Session.Profile.DockerTemplates }.OfType<List<DockerTemplate>>().Distinct())
        {
            list.RemoveAll(t => t.Id == template.Id || t.ContainerName == template.ContainerName);
            list.Add(template.Clone());
        }
        if (Profile is not null) await App.Store.SaveAsync(lifetime.Token);
        UpdateState(); UpdateCommands();
    }

    async Task LoadLogAsync()
    {
        if (Selected is not { } c || !docker.Available) { logTitle.Text = "Журнал"; log.Text = ""; return; }
        logTitle.Text = "Журнал: " + c.Name;
        bool atEnd = log.VerticalOffset + log.ViewportHeight >= log.ExtentHeight - 4;
        log.Text = await docker.LogsAsync(c.Id, tail.SelectedItem is int n ? n : 200, lifetime.Token);
        if (atEnd) log.ScrollToEnd();
    }

    public void Dispose() { followTimer.Stop(); lifetime.Cancel(); }
}
