using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>Row of the import preview: the proposed profile and whether the user keeps it.</summary>
public sealed class ImportRow(ImportCandidate candidate) : ViewModels.ObservableObject
{
    bool selected = candidate.DuplicateOf is null && !candidate.Notes.Any(n => n.StartsWith("Требует исправления", StringComparison.Ordinal));
    public ImportCandidate Candidate => candidate;
    public bool Selected { get => selected; set => Set(ref selected, value); }
    public string Name => candidate.Profile.Name;
    public string Address => candidate.Profile.Username + "@" + candidate.Profile.Endpoint;
    public string Authentication => candidate.Profile.Authentication switch { AuthenticationKind.PrivateKey => "Ключ: " + System.IO.Path.GetFileName(candidate.Profile.PrivateKeyPath), AuthenticationKind.KeyboardInteractive => "Интерактивный", _ => "Пароль" };
    public string Notes => (candidate.DuplicateOf is { } d ? $"Уже есть профиль «{d}». " : "") + string.Join(" ", candidate.Notes);
}

/// <summary>
/// Preview of profiles found in an external configuration. Nothing is written until «Импортировать»;
/// passwords are never imported; duplicates and invalid entries start unchecked.
/// </summary>
public sealed class ImportProfilesWindow : Window
{
    public ObservableCollection<ImportRow> Rows { get; }
    internal DataGrid Table { get; }
    internal Button ImportButton { get; }
    public IReadOnlyList<ServerProfile> Accepted => Rows.Where(r => r.Selected).Select(r => r.Candidate.Profile).ToArray();

    public ImportProfilesWindow(string source, IReadOnlyList<ImportCandidate> candidates, IReadOnlyList<string> warnings)
    {
        Title = "Импорт серверов"; Width = 900; Height = 560; MinWidth = 640; MinHeight = 380; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Style = (Style)FindResource(typeof(Window));
        Rows = new(candidates.Select(c => new ImportRow(c)));
        var body = new DockPanel { Margin = new Thickness(24, 20, 24, 12) };
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top);
        heading.Children.Add(Ui.DialogHeading("Импорт серверов"));
        var origin = Ui.Text(source, 12); origin.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); origin.TextTrimming = TextTrimming.CharacterEllipsis; origin.Margin = new Thickness(0, 2, 0, 8); heading.Children.Add(origin);
        var explanation = Ui.Text(candidates.Count == 0 ? "Подходящих хостов не найдено: импортируются только конкретные имена Host, без шаблонов." : "Отмеченные хосты станут профилями. Пароли не переносятся; ключи используются из их текущего расположения. Совпадения с существующими профилями и записи с ошибками не отмечены.", 13);
        explanation.TextWrapping = TextWrapping.Wrap; explanation.Margin = new Thickness(0, 0, 0, 10); heading.Children.Add(explanation);
        body.Children.Add(heading);
        if (warnings.Count > 0)
        {
            var list = Ui.Text("Замечания к файлу:\n" + string.Join("\n", warnings.Take(8)) + (warnings.Count > 8 ? $"\n…и ещё {warnings.Count - 8}" : ""), 12);
            list.TextWrapping = TextWrapping.Wrap; list.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); list.Margin = new Thickness(0, 10, 0, 0); list.Name = "ImportWarnings";
            DockPanel.SetDock(list, Dock.Bottom); body.Children.Add(list);
        }
        Table = new DataGrid { Name = "ImportTable", AutoGenerateColumns = false, CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single, ItemsSource = Rows };
        Table.SetResourceReference(StyleProperty, "ProcessTableStyle");
        var check = new DataGridCheckBoxColumn { Header = "", Binding = new Binding(nameof(ImportRow.Selected)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 44 };
        Table.Columns.Add(check);
        MainWindow.AddColumn(Table, "Название", nameof(ImportRow.Name), 150); MainWindow.AddColumn(Table, "Адрес", nameof(ImportRow.Address), 220); MainWindow.AddColumn(Table, "Вход", nameof(ImportRow.Authentication), 150);
        var notes = new DataGridTextColumn { Header = "Замечания", Binding = new Binding(nameof(ImportRow.Notes)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 200, ElementStyle = WrappingText() };
        Table.Columns.Add(notes); Table.IsReadOnly = false; foreach (var column in Table.Columns.Skip(1)) column.IsReadOnly = true;
        body.Children.Add(Table);
        ImportButton = Ui.Button("Импортировать", () => DialogResult = true); ImportButton.IsDefault = true; ImportButton.SetResourceReference(StyleProperty, "PrimaryButtonStyle"); ImportButton.Name = "ImportAccept";
        var cancel = Ui.Button("Отмена", () => DialogResult = false); cancel.IsCancel = true;
        void UpdateButton() { int n = Rows.Count(r => r.Selected); ImportButton.IsEnabled = n > 0; ImportButton.Content = n > 0 ? $"Импортировать ({n})" : "Импортировать"; }
        foreach (var row in Rows) row.PropertyChanged += (_, _) => UpdateButton();
        UpdateButton();
        Content = Ui.DialogSurface(body, cancel, ImportButton);
    }

    static Style WrappingText()
    {
        var style = new Style(typeof(TextBlock)); style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap)); style.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center)); style.Setters.Add(new Setter(MarginProperty, new Thickness(0, 4, 0, 4)));
        return style;
    }
}
