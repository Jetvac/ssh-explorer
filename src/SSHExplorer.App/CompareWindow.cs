using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SSHExplorer.App.ViewModels;
using SSHExplorer.Core;

namespace SSHExplorer.App;

public sealed class CompareRow(CompareResult result) : ObservableObject
{
    bool selected;
    public CompareResult Result => result;
    public bool Selected { get => selected; set => Set(ref selected, value); }
    public string Path => result.RelativePath + (result.IsDirectory ? "/" : "");
    public string Reason => result.Reason;
    public string Local => Describe(result.Local);
    public string Remote => Describe(result.Remote);
    public bool IsDifference => result.Kind != CompareKind.Same;
    static string Describe(CompareEntry? e) => e is null ? "—" : e.IsDirectory ? "папка" : Ui.Size(e.Length) + " · " + e.ModifiedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.CurrentCulture);
}

/// <summary>
/// Compares a Windows folder with a server folder and copies the chosen differences through the normal transfer queue
/// (with its conflict prompts). Nothing is deleted on either side.
/// </summary>
public sealed class CompareWindow : Window
{
    readonly ServerSession session; readonly string remoteRoot;
    readonly TextBox localBox = new() { MinHeight = 32, Name = "CompareLocal", IsReadOnly = true };
    readonly CheckBox byContent = new() { Content = "Сравнивать содержимое (SHA-256) у файлов одного размера", Name = "CompareByContent", Margin = new Thickness(0, 6, 0, 0) };
    readonly CheckBox onlyDifferences = new() { Content = "Только различия", IsChecked = true, Name = "CompareOnlyDifferences", Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock state = Ui.Text("", 12);
    readonly Expander skipped = new() { Header = "Пропущено", Visibility = Visibility.Collapsed, Name = "CompareSkipped", Margin = new Thickness(0, 6, 0, 0) };
    readonly ObservableCollection<CompareRow> rows = [];
    readonly ListCollectionView view;
    readonly Button upload, download, compare;
    CancellationTokenSource? running;
    internal DataGrid Table { get; } = new() { Name = "CompareTable", AutoGenerateColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column, CanUserAddRows = false, MinHeight = 200 };
    internal Func<string, string, bool, Task<CompareReport>>? CompareOverride { get; set; }
    internal Func<string, bool>? ConfirmOverride { get; set; }
    internal Action<IReadOnlyList<string>, string>? UploadOverride { get; set; }
    internal Action<IReadOnlyList<string>, string>? DownloadOverride { get; set; }
    public string LocalRoot { get; private set; }
    public IEnumerable<CompareRow> Rows => rows;

    public CompareWindow(ServerSession session, string remoteRoot, string localRoot)
    {
        this.session = session; this.remoteRoot = remoteRoot; LocalRoot = localRoot;
        Title = "Сравнение папок"; Width = 1100; Height = 720; MinWidth = 760; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; Style = (Style)FindResource(typeof(Window));
        localBox.Text = localRoot;
        var body = new DockPanel { Margin = new Thickness(24, 20, 24, 12) };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top);
        top.Children.Add(Ui.DialogHeading("Сравнение папок"));
        var remoteText = Ui.Text("Сервер: " + session.Profile.Name + " · " + remoteRoot, 13); remoteText.TextTrimming = TextTrimming.CharacterEllipsis; remoteText.Margin = new Thickness(0, 4, 0, 6); top.Children.Add(remoteText);
        var localRow = new DockPanel();
        var browse = Ui.Button("Другая папка…", () => { var pick = new Microsoft.Win32.OpenFolderDialog { Title = "Папка на компьютере", InitialDirectory = LocalRoot }; if (pick.ShowDialog(this) == true) { LocalRoot = pick.FolderName; localBox.Text = LocalRoot; rows.Clear(); UpdateState(); } });
        browse.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(browse, Dock.Right); localRow.Children.Add(browse);
        compare = Ui.AsyncButton("Сравнить", RunCompareAsync); compare.Name = "CompareRun"; compare.Margin = new Thickness(8, 0, 0, 0); compare.SetResourceReference(StyleProperty, "PrimaryButtonStyle"); DockPanel.SetDock(compare, Dock.Right); localRow.Children.Add(compare);
        var localLabel = Ui.Text("Компьютер:", 13); localLabel.Margin = new Thickness(0, 0, 8, 0); DockPanel.SetDock(localLabel, Dock.Left); localRow.Children.Add(localLabel); localRow.Children.Add(localBox);
        top.Children.Add(localRow); top.Children.Add(byContent);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 6) };
        var mark = Ui.Button("Отметить различия", MarkDifferences); mark.Name = "CompareMark"; mark.ToolTip = "Только на компьютере и новее на компьютере — для загрузки; только на сервере и новее на сервере — для скачивания";
        var clear = Ui.Button("Снять отметки", () => { foreach (var r in rows) r.Selected = false; });
        tools.Children.Add(mark); tools.Children.Add(clear); tools.Children.Add(onlyDifferences); top.Children.Add(tools);
        body.Children.Add(top);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom);
        state.TextWrapping = TextWrapping.Wrap; state.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); state.Name = "CompareState"; state.Margin = new Thickness(0, 6, 0, 0);
        bottom.Children.Add(state); bottom.Children.Add(skipped); body.Children.Add(bottom);
        Table.SetResourceReference(StyleProperty, "ProcessTableStyle");
        Table.Columns.Add(new DataGridCheckBoxColumn { Header = "", Binding = new Binding(nameof(CompareRow.Selected)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 40 });
        MainWindow.AddColumn(Table, "Путь", nameof(CompareRow.Path), 300); MainWindow.AddColumn(Table, "Состояние", nameof(CompareRow.Reason), 230);
        MainWindow.AddColumn(Table, "На компьютере", nameof(CompareRow.Local), 200); MainWindow.AddColumn(Table, "На сервере", nameof(CompareRow.Remote), 200);
        var last = Table.Columns[^1]; last.MinWidth = 200; last.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
        foreach (var column in Table.Columns.Skip(1)) column.IsReadOnly = true;
        view = (ListCollectionView)CollectionViewSource.GetDefaultView(rows); view.Filter = item => onlyDifferences.IsChecked != true || ((CompareRow)item).IsDifference; Table.ItemsSource = view;
        onlyDifferences.Checked += (_, _) => { view.Refresh(); UpdateState(); }; onlyDifferences.Unchecked += (_, _) => { view.Refresh(); UpdateState(); };
        body.Children.Add(Table);
        upload = Ui.AsyncButton("Загрузить выбранные на сервер", UploadAsync); upload.Name = "CompareUpload";
        download = Ui.AsyncButton("Скачать выбранные", DownloadAsync); download.Name = "CompareDownload";
        var close = Ui.Button("Закрыть", Close); close.IsCancel = true;
        Content = Ui.DialogSurface(body, close, download, upload);
        Closed += (_, _) => running?.Cancel();
        UpdateState();
    }

    async Task RunCompareAsync()
    {
        running?.Cancel(); var cts = new CancellationTokenSource(); running = cts;
        compare.IsEnabled = false; rows.Clear(); state.Text = "Сравнение…";
        try
        {
            var report = CompareOverride is { } fake ? await fake(LocalRoot, remoteRoot, byContent.IsChecked == true)
                : await new FolderComparer(session).CompareAsync(LocalRoot, remoteRoot, byContent.IsChecked == true, new Progress<string>(text => state.Text = text), cts.Token);
            Apply(report);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        finally { compare.IsEnabled = true; }
    }

    internal void Apply(CompareReport report)
    {
        rows.Clear(); foreach (var r in report.Results) { var row = new CompareRow(r); row.PropertyChanged += (_, _) => UpdateState(); rows.Add(row); }
        skipped.Visibility = report.Skipped.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        skipped.Header = $"Пропущено: {report.Skipped.Count} (ссылки, специальные файлы, нет доступа)";
        var list = Ui.Text(string.Join("\n", report.Skipped.Take(200)), 12); list.TextWrapping = TextWrapping.Wrap; skipped.Content = list;
        UpdateState(report.Truncated);
    }

    void UpdateState(bool truncated = false)
    {
        int differences = rows.Count(r => r.IsDifference), selected = rows.Count(r => r.Selected);
        upload.IsEnabled = rows.Any(r => r.Selected && r.Result.Local is not null);
        download.IsEnabled = rows.Any(r => r.Selected && r.Result.Remote is not null);
        state.Text = rows.Count == 0 ? "Нажмите «Сравнить». Ничего не копируется и не удаляется, пока вы не выберете действие."
            : $"Объектов: {rows.Count}, различий: {differences}, отмечено: {selected}." + (truncated ? $" Показаны первые {FolderCompare.MaxEntries} объектов." : "")
              + (byContent.IsChecked == true ? "" : " Файлы одного размера и времени считаются одинаковыми — для проверки содержимого включите SHA-256.");
    }

    void MarkDifferences()
    {
        foreach (var r in rows) r.Selected = r.Result.Kind is CompareKind.OnlyLocal or CompareKind.OnlyRemote || r.Result.Kind == CompareKind.Different && r.Result.Newer is "local" or "remote";
    }

    /// <summary>Selected paths without those inside another selected folder (the folder brings them along).</summary>
    static IReadOnlyList<string> Outermost(IEnumerable<CompareRow> chosen)
    {
        var paths = chosen.Select(r => r.Result.RelativePath).Order(StringComparer.Ordinal).ToList(); var result = new List<string>();
        foreach (var p in paths) if (!result.Any(parent => p.StartsWith(parent + "/", StringComparison.Ordinal))) result.Add(p);
        return result;
    }

    bool Confirm(string text) => ConfirmOverride?.Invoke(text) ?? Ui.Confirm(text);

    async Task UploadAsync()
    {
        var chosen = Outermost(rows.Where(r => r.Selected && r.Result.Local is not null && r.Result.Kind != CompareKind.TypeConflict));
        if (chosen.Count == 0) { state.Text = "Отмечены только объекты, которых нет на компьютере, или конфликты типов."; return; }
        int replacing = chosen.Count(p => rows.First(r => r.Result.RelativePath == p).Result.Remote is not null);
        if (!Confirm($"Загрузить на сервер {chosen.Count} объект(ов) в {remoteRoot}?\n\n" + (replacing > 0 ? $"{replacing} уже есть на сервере: для каждого будет спрошено, заменить или пропустить." : "Все они на сервере отсутствуют.") + "\nНичего не удаляется.")) return;
        foreach (var group in chosen.GroupBy(p => PosixPath.Parent("/" + p).TrimStart('/')))
        {
            var target = group.Key.Length == 0 ? remoteRoot : PosixPath.Combine(remoteRoot, group.Key);
            var sources = group.Select(p => System.IO.Path.Combine(LocalRoot, p.Replace('/', System.IO.Path.DirectorySeparatorChar))).ToArray();
            if (UploadOverride is { } fake) { fake(sources, target); continue; }
            if (group.Key.Length > 0) await session.ExecuteAsync("mkdir -p -- " + PosixPath.Quote(target));
            App.Transfers.Upload(session, sources, target);
        }
        state.Text = "Передачи добавлены в очередь «Передачи». Нажмите «Сравнить», когда они завершатся, чтобы проверить результат.";
    }

    async Task DownloadAsync()
    {
        var chosen = Outermost(rows.Where(r => r.Selected && r.Result.Remote is not null && r.Result.Kind != CompareKind.TypeConflict));
        if (chosen.Count == 0) { state.Text = "Отмечены только объекты, которых нет на сервере, или конфликты типов."; return; }
        int replacing = chosen.Count(p => rows.First(r => r.Result.RelativePath == p).Result.Local is not null);
        if (!Confirm($"Скачать {chosen.Count} объект(ов) в {LocalRoot}?\n\n" + (replacing > 0 ? $"{replacing} уже есть на компьютере: для каждого будет спрошено, заменить или пропустить." : "Все они на компьютере отсутствуют.") + "\nНичего не удаляется.")) return;
        foreach (var group in chosen.GroupBy(p => PosixPath.Parent("/" + p).TrimStart('/')))
        {
            var target = group.Key.Length == 0 ? LocalRoot : System.IO.Path.Combine(LocalRoot, group.Key.Replace('/', System.IO.Path.DirectorySeparatorChar));
            var sources = group.Select(p => PosixPath.Combine(remoteRoot, p)).ToArray();
            if (DownloadOverride is { } fake) { fake(sources, target); continue; }
            System.IO.Directory.CreateDirectory(target);
            App.Transfers.Download(session, sources, target);
        }
        state.Text = "Передачи добавлены в очередь «Передачи». Нажмите «Сравнить», когда они завершатся, чтобы проверить результат.";
        await Task.CompletedTask;
    }
}
