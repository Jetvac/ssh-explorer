using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>Row of the export list: a saved server and whether it goes into the file.</summary>
public sealed class ExportRow(ServerProfile profile, bool selected) : ViewModels.ObservableObject
{
    bool selected = selected;
    public ServerProfile Profile => profile;
    public bool Selected { get => selected; set => Set(ref selected, value); }
    public string Name => profile.Name;
    public string Group => profile.Group.Length > 0 ? profile.Group : "—";
    public string Address => profile.Username + "@" + profile.Endpoint;
    public string Contents => ExchangeText.Contents(profile);
}

internal static class ExchangeText
{
    public static string Contents(ServerProfile p)
    {
        var parts = new List<string>();
        if (p.Scripts.Count > 0) parts.Add($"скрипты: {p.Scripts.Count}");
        if (p.Tunnels.Count > 0) parts.Add($"туннели: {p.Tunnels.Count}");
        if (p.DockerTemplates.Count > 0) parts.Add($"Docker: {p.DockerTemplates.Count}");
        if (p.Bookmarks.Count > 0) parts.Add($"закладки: {p.Bookmarks.Count}");
        if (p.ProxyJump.Length > 0) parts.Add("через " + p.ProxyJump);
        return parts.Count == 0 ? "только профиль" : string.Join(", ", parts);
    }

    public static TextBlock Muted(string text, double size = 12)
    {
        var t = Ui.Text(text, size); t.TextWrapping = TextWrapping.Wrap; t.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); return t;
    }

    public static Style Wrapping()
    {
        var style = new Style(typeof(TextBlock)); style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap)); style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center)); style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 4)));
        return style;
    }
}

/// <summary>
/// «Экспорт серверов»: which servers go into the <c>.sshx</c> file, whether their secrets go too (only sealed with a
/// passphrase) and which extras. Nothing is read from the secret store until «Сохранить файл…».
/// </summary>
public sealed class ExportWindow : Window
{
    public ObservableCollection<ExportRow> Rows { get; }
    readonly RadioButton withoutSecrets = new() { Name = "ExportWithoutSecrets", IsChecked = true, Margin = new Thickness(0, 4, 0, 2) };
    readonly RadioButton withSecrets = new() { Name = "ExportWithSecrets", Margin = new Thickness(0, 4, 0, 2) };
    readonly PasswordBox passphrase = new() { Name = "ExportPassphrase", MinHeight = 32 }, repeat = new() { Name = "ExportPassphraseRepeat", MinHeight = 32 };
    readonly StackPanel passphrasePanel = new() { Margin = new Thickness(26, 4, 0, 4) };
    readonly CheckBox hostKeys = new() { Name = "ExportHostKeys", IsChecked = true, Content = "Доверенные ключи этих серверов — на новом компьютере не придётся заново сверять отпечатки", Margin = new Thickness(0, 4, 0, 2) };
    readonly CheckBox sidebar = new() { Name = "ExportSidebar", Content = "Порядок и видимость разделов левой панели", Margin = new Thickness(0, 4, 0, 2) };
    readonly CheckBox ruleSets = new() { Name = "ExportRuleSets", Margin = new Thickness(0, 4, 0, 2) };
    readonly TextBlock problem = Ui.Text("", 12);
    readonly Button save;
    internal string? Problem => problem.Text.Length > 0 ? problem.Text : null;
    internal Button SaveButton => save;
    public IReadOnlyList<ServerProfile> Selected => Rows.Where(r => r.Selected).Select(r => r.Profile).ToArray();
    ExportOptions? accepted;
    /// <summary>The options as they were when the window closed (the passphrase boxes are cleared then).</summary>
    public ExportOptions Chosen => accepted ?? Options;
    public ExportOptions Options => new(withSecrets.IsChecked == true, withSecrets.IsChecked == true ? passphrase.Password : null, hostKeys.IsChecked == true, sidebar.IsChecked == true, ruleSets.IsChecked == true && ruleSets.IsEnabled);
    internal void SetPassphrase(string a, string b) { passphrase.Password = a; repeat.Password = b; Update(); }
    internal void UseSecrets(bool on) { withSecrets.IsChecked = on; withoutSecrets.IsChecked = !on; Update(); }

    public ExportWindow(IReadOnlyList<ServerProfile> profiles, IReadOnlyCollection<Guid> preselected, int ruleSetCount)
    {
        Title = "Экспорт серверов"; Width = 920; Height = 680; MinWidth = 660; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "DialogWindowStyle");
        Rows = new(profiles.OrderBy(p => p.Group, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Select(p => new ExportRow(p, preselected.Count == 0 || preselected.Contains(p.Id))));
        var body = new DockPanel { Margin = new Thickness(24, 20, 24, 12) };
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top);
        heading.Children.Add(Ui.DialogHeading("Экспорт серверов"));
        var intro = ExchangeText.Muted("Отмеченные серверы попадут в файл .sshx вместе со своими туннелями, скриптами, параметрами Docker и закладками. Файл можно импортировать на другом компьютере: «Главная» → «Импорт» → «Из файла SSH Explorer».", 13);
        intro.Margin = new Thickness(0, 2, 0, 10); heading.Children.Add(intro);
        var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        var all = Ui.Button("Выбрать все", () => { foreach (var r in Rows) r.Selected = true; }); all.Margin = new Thickness(0, 0, 8, 0);
        var none = Ui.Button("Снять все", () => { foreach (var r in Rows) r.Selected = false; });
        tools.Children.Add(all); tools.Children.Add(none); heading.Children.Add(tools);
        body.Children.Add(heading);

        var options = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; DockPanel.SetDock(options, Dock.Bottom);
        options.Children.Add(Ui.Text("Пароли", 14, true));
        withoutSecrets.Content = new TextBlock { Text = "Без паролей — файл безопасно передавать. Пароли нужно будет ввести при первом подключении.", TextWrapping = TextWrapping.Wrap };
        withSecrets.Content = new TextBlock { Text = "С сохранёнными паролями и секретами Docker, зашифрованными парольной фразой", TextWrapping = TextWrapping.Wrap };
        options.Children.Add(withoutSecrets); options.Children.Add(withSecrets);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        StackPanel Field(string label, PasswordBox box) { var p = new StackPanel(); var l = Ui.Text(label, 12); l.Margin = new Thickness(0, 0, 0, 4); l.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); p.Children.Add(l); p.Children.Add(box); AutomationProperties.SetName(box, label); return p; }
        var first = Field($"Парольная фраза (не короче {ProfileExchange.MinPassphrase} символов)", passphrase); grid.Children.Add(first);
        var second = Field("Повторите", repeat); Grid.SetColumn(second, 2); grid.Children.Add(second);
        passphrasePanel.Children.Add(grid);
        var phraseNote = ExchangeText.Muted("Фраза не сохраняется и не восстанавливается: без неё пароли из файла не открыть. Ключевые файлы в экспорт не входят — только путь к ним."); phraseNote.Margin = new Thickness(0, 6, 0, 0); passphrasePanel.Children.Add(phraseNote);
        options.Children.Add(passphrasePanel);
        var extras = Ui.Text("Дополнительно", 14, true); extras.Margin = new Thickness(0, 10, 0, 0); options.Children.Add(extras);
        options.Children.Add(hostKeys); options.Children.Add(sidebar);
        ruleSets.Content = ruleSetCount > 0 ? $"Наборы правил файервола ({ruleSetCount})" : "Наборы правил файервола — сохранённых наборов нет";
        ruleSets.IsChecked = ruleSetCount > 0; ruleSets.IsEnabled = ruleSetCount > 0; options.Children.Add(ruleSets);
        problem.Name = "ExportProblem"; problem.TextWrapping = TextWrapping.Wrap; problem.Margin = new Thickness(0, 8, 0, 0); problem.SetResourceReference(TextBlock.ForegroundProperty, "DangerText"); options.Children.Add(problem);
        body.Children.Add(options);

        var table = new DataGrid { Name = "ExportTable", AutoGenerateColumns = false, CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single, ItemsSource = Rows, MinHeight = 160 };
        table.SetResourceReference(StyleProperty, "ProcessTableStyle");
        table.Columns.Add(new DataGridCheckBoxColumn { Header = "", Binding = new Binding(nameof(ExportRow.Selected)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 44 });
        MainWindow.AddColumn(table, "Название", nameof(ExportRow.Name), 190); MainWindow.AddColumn(table, "Группа", nameof(ExportRow.Group), 120); MainWindow.AddColumn(table, "Адрес", nameof(ExportRow.Address), 210);
        table.Columns.Add(new DataGridTextColumn { Header = "Что входит", Binding = new Binding(nameof(ExportRow.Contents)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 160, ElementStyle = ExchangeText.Wrapping() });
        foreach (var column in table.Columns.Skip(1)) column.IsReadOnly = true;
        body.Children.Add(table);

        save = Ui.Button("Сохранить файл…", () => { if (save!.IsEnabled) DialogResult = true; }); save.Name = "ExportSave"; save.IsDefault = true; save.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        var cancel = Ui.Button("Отмена", () => DialogResult = false); cancel.IsCancel = true;
        Content = Ui.DialogSurface(body, cancel, save);
        foreach (var r in Rows) r.PropertyChanged += (_, _) => Update();
        withSecrets.Checked += (_, _) => Update(); withoutSecrets.Checked += (_, _) => Update();
        passphrase.PasswordChanged += (_, _) => Update(); repeat.PasswordChanged += (_, _) => Update();
        // Read the choices before the passphrase boxes are wiped on close.
        Closing += (_, _) => accepted ??= Options;
        Closed += (_, _) => { passphrase.Clear(); repeat.Clear(); };
        Update();
    }

    void Update()
    {
        passphrasePanel.Visibility = withSecrets.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        int n = Rows.Count(r => r.Selected);
        string? error = n == 0 ? "Отметьте хотя бы один сервер." : withSecrets.IsChecked == true ? ProfileExchange.ValidatePassphrase(passphrase.Password, repeat.Password) : null;
        // An empty passphrase is not an error yet, just incomplete.
        problem.Text = error is not null && !(withSecrets.IsChecked == true && passphrase.Password.Length == 0 && repeat.Password.Length == 0 && n > 0) ? error : "";
        save.IsEnabled = error is null; save.Content = n > 0 ? $"Сохранить файл ({n})…" : "Сохранить файл…";
    }
}

/// <summary>Row of an <c>.sshx</c> import: the proposed server, what happens to it and why.</summary>
public sealed class ExchangeRow : ViewModels.ObservableObject
{
    public const string Import = "Импортировать", Skip = "Пропустить", Replace = "Заменить существующий", KeepBoth = "Добавить копию";
    string choice;
    public ExchangeRow(ExchangeCandidate candidate)
    {
        Candidate = candidate;
        Choices = candidate.NeedsFix ? [Skip] : candidate.Existing is null ? [Import, Skip] : [Skip, Replace, KeepBoth];
        choice = Choices[0];
    }
    public ExchangeCandidate Candidate { get; }
    public IReadOnlyList<string> Choices { get; }
    public string Choice { get => choice; set => Set(ref choice, Choices.Contains(value) ? value : Choices[0]); }
    public bool Imported => Choice != Skip;
    public ConflictChoice Conflict => Choice == Replace ? ConflictChoice.Replace : ConflictChoice.KeepBoth;
    public string Name => Candidate.Profile.Name;
    public string Address => Candidate.Profile.Username + "@" + Candidate.Profile.Endpoint;
    public string Contents => ExchangeText.Contents(Candidate.Profile) + (Candidate.Secret is not null ? ", пароль" : "");
    public string Notes => (Candidate.Existing is { } e ? $"Уже есть «{e.Name}». " : "") + string.Join(" ", Candidate.Notes);
}

/// <summary>
/// Preview of an <c>.sshx</c> file. Secrets open only with the passphrase (the file can also be imported without
/// them). Matching servers are skipped unless replaced or added as a copy; extras are opt-in. Nothing is written
/// until «Импортировать».
/// </summary>
public sealed class ExchangeImportWindow : Window
{
    readonly ExportDocument doc;
    readonly IReadOnlyCollection<ServerProfile> existing;
    readonly Func<string, bool>? fileExists;
    readonly Func<string, string>? protectDocker;
    readonly PasswordBox passphrase = new() { Name = "ImportPassphrase", MinHeight = 32, MinWidth = 220 };
    readonly TextBlock secretState = Ui.Text("", 12);
    readonly CheckBox hostKeys = new() { Name = "ImportHostKeys", Margin = new Thickness(0, 4, 0, 2) };
    readonly CheckBox sidebar = new() { Name = "ImportSidebar", Content = "Порядок и видимость разделов левой панели (заменит текущие)", Margin = new Thickness(0, 4, 0, 2) };
    readonly CheckBox ruleSets = new() { Name = "ImportRuleSets", Margin = new Thickness(0, 4, 0, 2) };
    readonly DataGrid table;
    readonly Button accept;
    public ObservableCollection<ExchangeRow> Rows { get; } = [];
    public ExportDocument Document => doc;
    public bool SecretsOpened { get; private set; }
    public bool IncludeHostKeys => hostKeys.IsChecked == true && hostKeys.IsEnabled;
    public bool IncludeSidebar => sidebar.IsChecked == true && sidebar.IsEnabled;
    public bool IncludeRuleSets => ruleSets.IsChecked == true && ruleSets.IsEnabled;
    internal Button AcceptButton => accept;
    internal string SecretState => secretState.Text;
    internal void SetExtras(bool keys, bool layout, bool sets) { hostKeys.IsChecked = keys; sidebar.IsChecked = layout; ruleSets.IsChecked = sets; Update(); }

    public ExchangeImportWindow(string source, ExportDocument doc, IReadOnlyCollection<ServerProfile> existing, Func<string, bool>? fileExists = null, Func<string, string>? protectDocker = null)
    {
        this.doc = doc; this.existing = existing; this.fileExists = fileExists; this.protectDocker = protectDocker;
        Title = "Импорт из файла SSH Explorer"; Width = 980; Height = 640; MinWidth = 680; MinHeight = 440; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "DialogWindowStyle");
        var body = new DockPanel { Margin = new Thickness(24, 20, 24, 12) };
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top);
        heading.Children.Add(Ui.DialogHeading("Импорт серверов"));
        var origin = ExchangeText.Muted(source + $" · создан {doc.CreatedUtc.ToLocalTime():dd.MM.yyyy HH:mm}" + (doc.Application.Length > 0 ? " · SSH Explorer " + doc.Application : "")); origin.TextWrapping = TextWrapping.NoWrap; origin.TextTrimming = TextTrimming.CharacterEllipsis; origin.Margin = new Thickness(0, 2, 0, 8); heading.Children.Add(origin);
        var intro = ExchangeText.Muted("Выберите, что сделать с каждым сервером. Совпадающие с уже сохранёнными по адресу или имени по умолчанию пропускаются. Автоматические скрипты из файла приходят приостановленными.", 13); intro.Margin = new Thickness(0, 0, 0, 8); heading.Children.Add(intro);
        if (ProfileExchange.HasSecrets(doc))
        {
            var lockRow = new DockPanel { Margin = new Thickness(0, 2, 0, 8) };
            var open = Ui.Button("Открыть пароли", OpenSecrets); open.Name = "ImportOpenSecrets"; open.Margin = new Thickness(8, 0, 12, 0); DockPanel.SetDock(open, Dock.Left);
            AutomationProperties.SetName(passphrase, "Парольная фраза файла"); DockPanel.SetDock(passphrase, Dock.Left);
            var label = Ui.Text("Парольная фраза:", 13); label.VerticalAlignment = VerticalAlignment.Center; label.Margin = new Thickness(0, 0, 8, 0); DockPanel.SetDock(label, Dock.Left);
            lockRow.Children.Add(label); lockRow.Children.Add(passphrase); lockRow.Children.Add(open);
            secretState.Name = "ImportSecretState"; secretState.VerticalAlignment = VerticalAlignment.Center; secretState.TextWrapping = TextWrapping.Wrap; lockRow.Children.Add(secretState);
            secretState.Text = "Файл содержит пароли, защищённые парольной фразой. Без неё серверы импортируются без паролей.";
            secretState.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            passphrase.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { OpenSecrets(); e.Handled = true; } };
            heading.Children.Add(lockRow);
        }
        body.Children.Add(heading);

        var extras = new StackPanel { Margin = new Thickness(0, 10, 0, 0) }; DockPanel.SetDock(extras, Dock.Bottom);
        int keys = doc.HostKeys?.Count ?? 0, sets = doc.RuleSets?.Count ?? 0;
        if (keys + sets > 0 || doc.Sidebar is not null) { var t = Ui.Text("Дополнительно из файла", 14, true); extras.Children.Add(t); }
        hostKeys.Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = $"Доверенные ключи серверов ({keys}). Добавляются только новые; уже сохранённый другой ключ не заменяется." };
        ruleSets.Content = $"Наборы правил файервола ({sets})";
        if (keys > 0) extras.Children.Add(hostKeys); if (doc.Sidebar is not null) extras.Children.Add(sidebar); if (sets > 0) { ruleSets.IsChecked = true; extras.Children.Add(ruleSets); }
        hostKeys.IsEnabled = keys > 0; sidebar.IsEnabled = doc.Sidebar is not null; ruleSets.IsEnabled = sets > 0;
        body.Children.Add(extras);

        table = new DataGrid { Name = "ExchangeTable", AutoGenerateColumns = false, CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single, ItemsSource = Rows };
        table.SetResourceReference(StyleProperty, "ProcessTableStyle");
        var combo = new FrameworkElementFactory(typeof(ComboBox));
        combo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(ExchangeRow.Choices)));
        combo.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, new Binding(nameof(ExchangeRow.Choice)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        combo.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 3, 6, 3)); combo.SetValue(FrameworkElement.MinHeightProperty, 30.0);
        table.Columns.Add(new DataGridTemplateColumn { Header = "Действие", Width = 200, CellTemplate = new DataTemplate { VisualTree = combo } });
        MainWindow.AddColumn(table, "Название", nameof(ExchangeRow.Name), 170); MainWindow.AddColumn(table, "Адрес", nameof(ExchangeRow.Address), 200);
        table.Columns.Add(new DataGridTextColumn { Header = "Что входит", Binding = new Binding(nameof(ExchangeRow.Contents)), Width = 170, ElementStyle = ExchangeText.Wrapping() });
        table.Columns.Add(new DataGridTextColumn { Header = "Замечания", Binding = new Binding(nameof(ExchangeRow.Notes)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 180, ElementStyle = ExchangeText.Wrapping() });
        table.IsReadOnly = false; foreach (var column in table.Columns.Skip(1)) column.IsReadOnly = true;
        body.Children.Add(table);

        accept = Ui.Button("Импортировать", () => { if (accept!.IsEnabled) DialogResult = true; }); accept.Name = "ExchangeAccept"; accept.IsDefault = true; accept.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        var cancel = Ui.Button("Отмена", () => DialogResult = false); cancel.IsCancel = true;
        Content = Ui.DialogSurface(body, cancel, accept);
        hostKeys.Click += (_, _) => Update(); sidebar.Click += (_, _) => Update(); ruleSets.Click += (_, _) => Update();
        Closed += (_, _) => passphrase.Clear();
        Load(null);
    }

    void Load(string? phrase)
    {
        var candidates = ProfileExchange.Plan(doc, phrase, existing, fileExists, protectDocker);
        var previous = Rows.ToDictionary(r => r.Name + "\0" + r.Address, r => r.Choice);
        Rows.Clear();
        foreach (var c in candidates)
        {
            var row = new ExchangeRow(c); if (previous.TryGetValue(row.Name + "\0" + row.Address, out var was)) row.Choice = was;
            row.PropertyChanged += (_, _) => Update(); Rows.Add(row);
        }
        Update();
    }

    /// <summary>Opens the sealed secrets; a wrong passphrase is said in place and nothing changes.</summary>
    internal void OpenSecrets()
    {
        if (passphrase.Password.Length == 0) { secretState.Text = "Введите парольную фразу, с которой файл был сохранён."; return; }
        if (!ProfileExchange.CheckPassphrase(doc, passphrase.Password)) { secretState.Text = "Неверная парольная фраза. Без неё серверы импортируются без паролей."; secretState.SetResourceReference(TextBlock.ForegroundProperty, "DangerText"); return; }
        Load(passphrase.Password); SecretsOpened = true; passphrase.IsEnabled = false;
        secretState.Text = "Пароли открыты: они будут сохранены с защитой Windows на этом компьютере."; secretState.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
    }
    internal void EnterPassphrase(string value) { passphrase.Password = value; OpenSecrets(); }

    void Update()
    {
        int n = Rows.Count(r => r.Imported), extras = (IncludeHostKeys ? 1 : 0) + (IncludeSidebar ? 1 : 0) + (IncludeRuleSets ? 1 : 0);
        accept.IsEnabled = n + extras > 0; accept.Content = n > 0 ? $"Импортировать ({n})" : "Импортировать";
    }
}
