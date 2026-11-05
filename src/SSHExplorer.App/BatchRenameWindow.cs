using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>Batch rename with a live preview; applying is possible only when every line is valid.</summary>
public sealed class BatchRenameWindow : Window
{
    readonly IReadOnlyList<(string Directory, string Name, bool IsDirectory)> items;
    readonly Func<string, IReadOnlySet<string>> folderNames;
    readonly TextBox pattern = Box("RenamePattern", "{name}{ext}"), find = Box("RenameFind", ""), replace = Box("RenameReplace", ""), start = Box("RenameStart", "1"), step = Box("RenameStep", "1"), digits = Box("RenameDigits", "1");
    readonly CheckBox regex = new() { Content = "Регулярное выражение", Name = "RenameRegex", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 18, 0, 0) };
    readonly ComboBox casing = new() { ItemsSource = new[] { "Как есть", "строчные", "ПРОПИСНЫЕ" }, SelectedIndex = 0, MinHeight = 32, Name = "RenameCase" };
    readonly ObservableCollection<RenamePlanItem> preview = [];
    readonly TextBlock summary = Ui.Text("", 12);
    readonly Button apply;
    internal DataGrid Table { get; } = new() { Name = "RenamePreview", IsReadOnly = true, AutoGenerateColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column, MinHeight = 180 };
    public IReadOnlyList<RenamePlanItem> Plan { get; private set; } = [];

    public BatchRenameWindow(IReadOnlyList<(string Directory, string Name, bool IsDirectory)> items, Func<string, IReadOnlySet<string>> folderNames)
    {
        this.items = items; this.folderNames = folderNames;
        Title = "Переименование объектов: " + items.Count; Width = 860; Height = 700; MinWidth = 640; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; Style = (Style)FindResource(typeof(Window));
        var body = new DockPanel { Margin = new Thickness(24, 20, 24, 12) };
        var form = new StackPanel(); DockPanel.SetDock(form, Dock.Top);
        form.Children.Add(Ui.DialogHeading("Переименование: " + items.Count + " объект(ов)"));
        form.Children.Add(Labeled("Шаблон имени", pattern, "{name} — имя без расширения, {ext} — расширение с точкой, {n} — номер"));
        var row = new Grid(); foreach (var w in new[] { 1.0, 16, 1, 0 }) row.ColumnDefinitions.Add(new ColumnDefinition { Width = w == 0 ? GridLength.Auto : w == 16 ? new GridLength(16) : new GridLength(w, GridUnitType.Star) });
        var f = Labeled("Найти", find, null); var r = Labeled("Заменить на", replace, "$1 — группа регулярного выражения"); Grid.SetColumn(r, 2); Grid.SetColumn(regex, 3);
        row.Children.Add(f); row.Children.Add(r); row.Children.Add(regex); form.Children.Add(row);
        var numbers = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, box) in new[] { ("Номер с", start), ("Шаг", step), ("Цифр", digits) }) { box.Width = 90; var l = Labeled(label, box, null); l.Margin = new Thickness(0, 0, 16, 10); numbers.Children.Add(l); }
        var c = Labeled("Регистр", casing, null); ((FrameworkElement)c).Width = 160; numbers.Children.Add(c); form.Children.Add(numbers);
        body.Children.Add(form);
        summary.Name = "RenameSummary"; summary.TextWrapping = TextWrapping.Wrap; summary.Margin = new Thickness(0, 6, 0, 0); DockPanel.SetDock(summary, Dock.Bottom); body.Children.Add(summary);
        Table.SetResourceReference(StyleProperty, "ProcessTableStyle");
        MainWindow.AddColumn(Table, "Было", nameof(RenamePlanItem.OldName), 260); MainWindow.AddColumn(Table, "Станет", nameof(RenamePlanItem.NewName), 260); MainWindow.AddColumn(Table, "Состояние", nameof(RenamePlanItem.Message), 200);
        var last = Table.Columns[^1]; last.MinWidth = 200; last.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
        Table.ItemsSource = preview; body.Children.Add(Table);
        var cancel = Ui.Button("Отмена", () => DialogResult = false); cancel.IsCancel = true;
        apply = Ui.Button("Переименовать", () => DialogResult = true); apply.Name = "RenameApply"; apply.IsDefault = true; apply.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        Content = Ui.DialogSurface(body, cancel, apply);
        foreach (var box in new[] { pattern, find, replace, start, step, digits }) box.TextChanged += (_, _) => Update();
        regex.Click += (_, _) => Update(); casing.SelectionChanged += (_, _) => Update();
        Loaded += (_, _) => { pattern.Focus(); pattern.SelectAll(); };
        Update();
    }

    static TextBox Box(string name, string text) => new() { Name = name, Text = text, MinHeight = 32, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
    static FrameworkElement Labeled(string label, Control input, string? hint)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) }; panel.Children.Add(Ui.Text(label, 12)); input.Margin = new Thickness(0, 4, 0, 0); panel.Children.Add(input);
        System.Windows.Automation.AutomationProperties.SetName(input, label);
        if (hint is not null) { var h = Ui.Text(hint, 11); h.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); panel.Children.Add(h); }
        return panel;
    }

    internal void Update()
    {
        static int Number(TextBox b, int fallback) => int.TryParse(b.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        var options = new BatchRenameOptions { Pattern = pattern.Text, Find = find.Text, Replace = replace.Text, UseRegex = regex.IsChecked == true, Case = (RenameCase)Math.Max(0, casing.SelectedIndex), Start = Number(start, 1), Step = Number(step, 1), Digits = Number(digits, 1) };
        Plan = BatchRename.Plan(items, options, folderNames);
        preview.Clear(); foreach (var p in Plan) preview.Add(p);
        int ok = Plan.Count(p => p.Status == RenameStatus.Ok), problems = Plan.Count(p => p.Status is RenameStatus.Invalid or RenameStatus.Duplicate or RenameStatus.Exists);
        apply.IsEnabled = ok > 0 && problems == 0;
        summary.SetResourceReference(TextBlock.ForegroundProperty, problems > 0 ? "ValidationInk" : "Muted");
        summary.Text = problems > 0 ? $"Проблем: {problems} — исправьте шаблон, чтобы продолжить. Ничего не изменено." : ok == 0 ? "Новые имена совпадают со старыми." : $"Будет переименовано: {ok}. Обмен именами внутри набора выполняется через временные имена, ничего не перезаписывается.";
    }
}
