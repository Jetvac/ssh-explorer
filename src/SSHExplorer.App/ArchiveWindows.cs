using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>«Сжать в архив»: name and format; formats whose tools the server lacks are shown as unavailable; a taken name blocks.</summary>
public sealed class ArchiveCreateWindow : Window
{
    readonly TextBox name = new() { Name = "ArchiveName", MinHeight = 32 };
    readonly ComboBox format = new() { Name = "ArchiveFormat", MinHeight = 32, DisplayMemberPath = nameof(Choice.Label) };
    readonly TextBlock problem = Ui.Text("", 12);
    readonly Button create;
    readonly IReadOnlySet<string> existing;
    readonly IReadOnlyList<string> items;
    readonly string directory;
    public sealed record Choice(ArchiveFormat Format, string Label, bool Available);
    public string ArchiveName => name.Text.Trim();
    public ArchiveFormat Format => ((Choice)format.SelectedItem).Format;

    public ArchiveCreateWindow(string directory, IReadOnlyList<string> items, IReadOnlySet<string> tools, IReadOnlySet<string> existing)
    {
        this.directory = directory; this.items = items; this.existing = existing;
        Title = "Сжать в архив"; Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "DialogWindowStyle");
        var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(Ui.DialogHeading(items.Count == 1 ? "Сжать «" + items[0] + "»" : "Сжать объекты: " + items.Count));
        var where = Ui.Text("Архив будет создан в " + directory + ". Существующие файлы не заменяются.", 12); where.TextWrapping = TextWrapping.Wrap; where.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); where.Margin = new Thickness(0, 0, 0, 12); body.Children.Add(where);
        var choices = Enum.GetValues<ArchiveFormat>().Select(f => Archives.Missing(f, true, tools) is { } missing ? new Choice(f, Archives.Label(f) + " — нет " + missing + " на сервере", false) : new Choice(f, Archives.Label(f), true)).ToArray();
        format.ItemsSource = choices; format.SelectedItem = choices.FirstOrDefault(c => c.Available) ?? choices[0];
        body.Children.Add(Labeled("Формат", format)); body.Children.Add(Labeled("Имя архива", name));
        name.Text = Archives.SuggestName(directory, items, Format);
        problem.Name = "ArchiveProblem"; problem.TextWrapping = TextWrapping.Wrap; problem.SetResourceReference(TextBlock.ForegroundProperty, "DangerText"); body.Children.Add(problem);
        var cancel = Ui.Button("Отмена", () => DialogResult = false); cancel.IsCancel = true;
        create = Ui.Button("Сжать", () => DialogResult = true); create.Name = "ArchiveCreate"; create.IsDefault = true; create.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        Content = Ui.DialogSurface(body, cancel, create);
        string? previousExtension = Archives.Extension(Format);
        format.SelectionChanged += (_, _) =>
        {
            // Keep the typed stem, swap only the extension.
            var stem = Archives.BaseName(name.Text.Trim()); var ext = Archives.Extension(Format);
            if (previousExtension is not null && (name.Text.Trim().EndsWith(previousExtension, StringComparison.OrdinalIgnoreCase) || stem == name.Text.Trim())) name.Text = stem + ext;
            previousExtension = ext; Update();
        };
        name.TextChanged += (_, _) => Update();
        Loaded += (_, _) => { name.Focus(); name.Select(0, Archives.BaseName(name.Text).Length); };
        Update();
    }

    static FrameworkElement Labeled(string label, Control input)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) }; panel.Children.Add(Ui.Text(label, 12)); input.Margin = new Thickness(0, 4, 0, 0); panel.Children.Add(input);
        System.Windows.Automation.AutomationProperties.SetName(input, label); return panel;
    }

    internal string? Problem => problem.Text.Length > 0 ? problem.Text : null;

    void Update()
    {
        var choice = (Choice)format.SelectedItem;
        var text = !choice.Available ? "На сервере нет нужной программы для этого формата." : Archives.ValidateName(ArchiveName)
            ?? (existing.Contains(ArchiveName) ? "Файл «" + ArchiveName + "» уже есть в этой папке." : items.Contains(ArchiveName, StringComparer.Ordinal) ? "Архив не может называться как сжимаемый объект." : null);
        problem.Text = text ?? ""; problem.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        create.IsEnabled = text is null;
    }
}

/// <summary>
/// «Распаковать»: what the archive contains (first entries), where it goes — a new folder (default, nothing is replaced) or
/// the archive's folder keeping existing files. Entries with absolute or «..» paths block extraction.
/// </summary>
public sealed class ArchiveExtractWindow : Window
{
    readonly RadioButton intoNew = new() { Content = "В новую папку", Name = "ExtractNew", IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
    readonly RadioButton here = new() { Content = "В эту папку — существующие файлы не заменяются", Name = "ExtractHere", Margin = new Thickness(0, 4, 0, 4) };
    readonly TextBox folder = new() { Name = "ExtractFolder", MinHeight = 32, Margin = new Thickness(24, 4, 0, 8) };
    readonly TextBlock problem = Ui.Text("", 12);
    readonly Button extract;
    readonly IReadOnlySet<string> existing;
    readonly ArchiveListing listing;
    readonly string directory;
    public ExtractMode Mode => here.IsChecked == true ? ExtractMode.HereKeepExisting : ExtractMode.NewFolder;
    public string Destination => Mode == ExtractMode.NewFolder ? PosixPath.Combine(directory, folder.Text.Trim()) : directory;
    internal string? Problem => problem.Text.Length > 0 ? problem.Text : null;

    public ArchiveExtractWindow(string archivePath, ArchiveListing listing, IReadOnlySet<string> existing, string? missingTool)
    {
        this.listing = listing; this.existing = existing; this.missingTool = missingTool; directory = PosixPath.Parent(archivePath);
        Title = "Распаковать архив"; Width = 620; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "DialogWindowStyle");
        var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(Ui.DialogHeading("Распаковать «" + PosixPath.Name(archivePath) + "»"));
        var summary = Ui.Text(listing.Truncated ? $"Объектов в архиве: больше {Archives.ListLimit}." : $"Объектов в архиве: {listing.Entries.Count}.", 12); summary.Name = "ExtractSummary"; summary.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); body.Children.Add(summary);
        var list = new TextBox { Name = "ExtractEntries", IsReadOnly = true, Height = 160, Margin = new Thickness(0, 6, 0, 12), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top,
            Text = string.Join("\n", listing.Entries.Take(500)) + (listing.Entries.Count > 500 ? $"\n… и ещё {listing.Entries.Count - 500}" : "") };
        body.Children.Add(list);
        body.Children.Add(intoNew); body.Children.Add(folder); body.Children.Add(here);
        folder.Text = Unique(Archives.BaseName(PosixPath.Name(archivePath)));
        System.Windows.Automation.AutomationProperties.SetName(folder, "Имя новой папки");
        problem.Name = "ExtractProblem"; problem.TextWrapping = TextWrapping.Wrap; problem.Margin = new Thickness(0, 8, 0, 0); problem.SetResourceReference(TextBlock.ForegroundProperty, "DangerText"); body.Children.Add(problem);
        var cancel = Ui.Button("Отмена", () => DialogResult = false); cancel.IsCancel = true;
        extract = Ui.Button("Распаковать", () => DialogResult = true); extract.Name = "ExtractApply"; extract.IsDefault = true; extract.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        Content = Ui.DialogSurface(body, cancel, extract);
        folder.TextChanged += (_, _) => Update(); intoNew.Checked += (_, _) => Update(); here.Checked += (_, _) => Update();
        Update();
    }
    readonly string? missingTool;

    string Unique(string stem)
    {
        if (!existing.Contains(stem)) return stem;
        for (int i = 2; i < 1000; i++) if (!existing.Contains($"{stem} ({i})")) return $"{stem} ({i})";
        return stem;
    }

    void Update()
    {
        folder.IsEnabled = Mode == ExtractMode.NewFolder;
        var name = folder.Text.Trim();
        string? text = missingTool is not null ? "На сервере нет " + missingTool + ": распаковать этот архив нельзя."
            : listing.Unsafe.Count > 0 ? $"Архив содержит пути вне папки назначения ({listing.Unsafe.Count}), например «{listing.Unsafe[0]}». Распаковка заблокирована."
            : Mode == ExtractMode.NewFolder && Archives.ValidateName(name) is { } bad ? bad.Replace("архива", "папки")
            : Mode == ExtractMode.NewFolder && existing.Contains(name) ? "«" + name + "» уже есть в этой папке."
            : null;
        problem.Text = text ?? ""; problem.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        extract.IsEnabled = text is null;
    }
}
