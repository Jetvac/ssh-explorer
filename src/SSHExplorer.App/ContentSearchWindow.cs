using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>
/// «Найти в файлах»: grep on the server under a folder. Results open in the read-only viewer at the matching line, or show
/// the file in the browser. Nothing on the server is changed.
/// </summary>
public sealed class ContentSearchWindow : Window
{
    readonly ServerSession? session;
    readonly string directory;
    readonly TextBox pattern = new() { Name = "GrepPattern", MinHeight = 32, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
    readonly TextBox include = new() { Name = "GrepInclude", MinHeight = 32, Width = 200 };
    readonly CheckBox regex = Check("Регулярное выражение", "GrepRegex"), ignoreCase = Check("Без учёта регистра", "GrepIgnoreCase", true), word = Check("Слово целиком", "GrepWord"), skip = Check("Пропускать .git, node_modules…", "GrepSkip", true);
    readonly ObservableCollection<ContentMatch> results = [];
    readonly TextBlock state = Ui.Text("", 12);
    readonly Button run, cancel;
    CancellationTokenSource? searching;
    readonly CancellationTokenSource lifetime = new();
    internal DataGrid Table { get; } = new() { Name = "GrepResults", IsReadOnly = true, AutoGenerateColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single };
    internal Func<string, CancellationToken, Task<CommandResult>>? ExecuteOverride { get; set; }
    internal Action<string, int, string>? OpenOverride { get; set; }
    public Action<string>? ShowInFolder { get; init; }
    internal string StateText => state.Text;
    public IReadOnlyList<ContentMatch> Results => results;

    public ContentSearchWindow(ServerSession? session, string directory)
    {
        this.session = session; this.directory = directory;
        Title = "Поиск в файлах: " + directory; Width = 1000; Height = 720; MinWidth = 640; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = true; Style = (Style)FindResource(typeof(Window));
        var form = new StackPanel { Margin = new Thickness(16, 12, 16, 6) };
        var top = new DockPanel();
        run = Ui.AsyncButton("Найти", SearchAsync); run.Name = "GrepRun"; run.IsDefault = true; run.SetResourceReference(StyleProperty, "PrimaryButtonStyle"); run.Margin = new Thickness(8, 0, 0, 0);
        cancel = Ui.Button("Остановить", () => searching?.Cancel()); cancel.Name = "GrepCancel"; cancel.IsEnabled = false; cancel.Margin = new Thickness(8, 0, 0, 0);
        var buttons = Ui.Row(run, cancel); DockPanel.SetDock(buttons, Dock.Right); top.Children.Add(buttons);
        var maskLabel = Ui.Text("Файлы ", 12); maskLabel.VerticalAlignment = VerticalAlignment.Center; maskLabel.Margin = new Thickness(12, 0, 4, 0);
        var masks = Ui.Row(maskLabel, include); DockPanel.SetDock(masks, Dock.Right); top.Children.Add(masks);
        top.Children.Add(pattern); form.Children.Add(top);
        System.Windows.Automation.AutomationProperties.SetName(pattern, "Текст для поиска"); System.Windows.Automation.AutomationProperties.SetName(include, "Маски имён файлов");
        include.ToolTip = "Например: *.conf, *.yml — пусто: все файлы";
        var options = Ui.Row(ignoreCase, word, regex, skip); options.Margin = new Thickness(0, 8, 0, 0); form.Children.Add(options);
        var scope = Ui.Text("Папка: " + directory + " · двоичные файлы пропускаются · символические ссылки на папки не обходятся", 12); scope.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); scope.Margin = new Thickness(0, 6, 0, 0); scope.TextTrimming = TextTrimming.CharacterEllipsis; form.Children.Add(scope);
        var barHost = new Border { Child = form }; barHost.SetResourceReference(StyleProperty, "CommandBarStyle");

        Table.SetResourceReference(StyleProperty, "ProcessTableStyle"); Table.Margin = new Thickness(16, 8, 16, 0);
        MainWindow.AddColumn(Table, "Файл", nameof(ContentMatch.Path), 300); MainWindow.AddColumn(Table, "Строка", nameof(ContentMatch.Line), 70); MainWindow.AddColumn(Table, "Текст", nameof(ContentMatch.Text), 500);
        var last = Table.Columns[^1]; last.MinWidth = 200; last.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
        Table.ItemsSource = results;
        Table.MouseDoubleClick += (_, _) => OpenSelected();
        // Preview: the grid's own Enter handling would move to the next row first.
        Table.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { OpenSelected(); e.Handled = true; } };
        var menu = new ContextMenu();
        menu.Items.Add(Ui.Menu("Просмотреть на этой строке", OpenSelected, "Enter"));
        menu.Items.Add(Ui.Menu("Показать в папке", () => { if (Table.SelectedItem is ContentMatch m) ShowInFolder?.Invoke(PosixPath.Combine(directory, m.Path)); }));
        menu.Items.Add(Ui.Menu("Копировать путь", () => { if (Table.SelectedItem is ContentMatch m) Clipboard.SetText(PosixPath.Combine(directory, m.Path)); }));
        Table.ContextMenu = menu;

        state.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); state.Margin = new Thickness(16, 6, 16, 6); state.Name = "GrepState"; state.TextTrimming = TextTrimming.CharacterEllipsis;
        var footer = new Border { Child = state }; footer.SetResourceReference(StyleProperty, "StatusBarStyle");
        var root = new DockPanel(); DockPanel.SetDock(barHost, Dock.Top); root.Children.Add(barHost); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); root.Children.Add(Table);
        Content = root;
        state.Text = "Введите текст и нажмите «Найти».";
        Loaded += (_, _) => pattern.Focus();
        Closed += (_, _) => { searching?.Cancel(); lifetime.Cancel(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { if (searching is not null) searching.Cancel(); else Close(); e.Handled = true; } };
    }

    static CheckBox Check(string text, string name, bool value = false) => new() { Content = text, Name = name, IsChecked = value, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };

    internal ContentSearchOptions Options => new() { Pattern = pattern.Text, Regex = regex.IsChecked == true, IgnoreCase = ignoreCase.IsChecked == true, WholeWord = word.IsChecked == true, Include = include.Text, SkipCommonFolders = skip.IsChecked == true };

    internal async Task SearchAsync()
    {
        var options = Options;
        if (ContentSearch.Validate(options) is { } problem) { state.Text = problem; return; }
        searching?.Cancel(); var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); searching = cts;
        results.Clear(); run.IsEnabled = false; cancel.IsEnabled = true; state.Text = "Поиск в " + directory + "…";
        var started = DateTime.UtcNow;
        try
        {
            var command = ContentSearch.Command(directory, options);
            var output = await (ExecuteOverride?.Invoke(command, cts.Token) ?? session!.ExecuteLongAsync(command, TimeSpan.FromMinutes(3), cts.Token));
            var result = ContentSearch.Parse(output.Output, output.Error);
            foreach (var m in result.Matches) results.Add(m);
            var files = result.Matches.Select(m => m.Path).Distinct(StringComparer.Ordinal).Count();
            var seconds = (DateTime.UtcNow - started).TotalSeconds;
            state.Text = result.Error is { } error ? "Сервер сообщил: " + error
                : result.Matches.Count == 0 ? $"Ничего не найдено ({seconds:0.0} с)."
                : $"Совпадений: {result.Matches.Count} в файлах: {files} ({seconds:0.0} с)" + (result.Truncated ? $" · показаны первые {ContentSearch.MatchLimit}: уточните запрос" : "") + " · Enter или двойной щелчок — открыть на строке";
            if (results.Count > 0) Table.SelectedIndex = 0;
        }
        catch (OperationCanceledException) { state.Text = lifetime.IsCancellationRequested ? "" : $"Поиск остановлен. Найдено до остановки: {results.Count}."; }
        catch (Exception ex) when (ex is System.IO.IOException or Renci.SshNet.Common.SshException or InvalidOperationException) { state.Text = "Поиск не выполнен: " + ErrorMessages.Describe(ex); }
        finally { if (ReferenceEquals(searching, cts)) searching = null; cts.Dispose(); run.IsEnabled = true; cancel.IsEnabled = false; }
    }

    void OpenSelected()
    {
        if (Table.SelectedItem is not ContentMatch m) return;
        var path = PosixPath.Combine(directory, m.Path);
        if (OpenOverride is { } open) { open(path, m.Line, pattern.Text); return; }
        if (session is null) return;
        new TextViewerWindow(PosixPath.Name(path), ct => session.Files.ReadBytesAsync(path, TextViewerWindow.MaximumBytes, ct)) { Owner = this, StartLine = m.Line, StartSearch = regex.IsChecked == true ? null : pattern.Text }.Show();
    }
}
