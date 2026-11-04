using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>
/// "Параметры": section navigation on the left, one group per page on the right (Windows 11
/// Settings layout). Every label sits directly above or beside its own control; technical limits
/// use numeric fields with units; version, commit and diagnostics live in "О приложении".
/// </summary>
public sealed class SettingsWindow : Window
{
    public sealed record Section(string Key, string Title, string Icon, string Description);
    public static readonly Section[] Sections =
    [
        new("general", "Общие и внешний вид", IconCatalog.ViewTiles, "Тема, анимация, отображение файлов"),
        new("connections", "Подключения", IconCatalog.Server, "Доверенные ключи серверов и локальные копии"),
        new("terminal", "Терминал и редактор", IconCatalog.Terminal, "Шрифт терминала и редактор для текстовых файлов"),
        new("monitoring", "Мониторинг", IconCatalog.Performance, "Частота обновления диспетчера задач"),
        new("preview", "Предпросмотр и кэш", Ui.FileGlyph, "Лимиты изображений и миниатюр"),
        new("about", "О приложении", Ui.PropertiesGlyph, "Версия, сборка, диагностика и лицензии"),
    ];

    readonly AppSettings settings = App.Store.Settings;
    readonly ListBox navigation = new() { Name = "SettingsNavigation" };
    readonly ContentControl page = new() { Focusable = false };
    readonly Dictionary<string, FrameworkElement> pages = [];
    readonly ComboBox theme = new() { Name = "SettingsTheme" };
    readonly CheckBox reduceMotion = new() { Name = "SettingsReduceMotion", Content = "Уменьшить анимацию" };
    readonly CheckBox showHidden = new() { Content = "Показывать скрытые файлы и папки" };
    readonly CheckBox showExtensions = new() { Content = "Показывать расширения имён файлов" };
    readonly NumberField poll = new("Интервал обновления", "с", 1, 60, 1);
    readonly NumberField font = new("Размер шрифта терминала", "пт", 8, 36, 1);
    readonly NumberField maxBytes = new("Наибольший размер изображения для предпросмотра", "МБ", 1, 200, 1);
    readonly NumberField pixels = new("Наибольшее разрешение изображения", "Мпикс", 1, 100, 1);
    readonly NumberField parallel = new("Одновременных запросов миниатюр", "запросов", 1, 4, 1);
    readonly NumberField cache = new("Кэш изображений на вкладку", "МБ", 16, 1024, 16);
    readonly TextBox editor = new() { Name = "SettingsEditorPath" };
    readonly TextBlock editorState = Ui.Text("", 12);
    public string CurrentSection { get; private set; } = "general";

    public SettingsWindow(string section = "general")
    {
        SetResourceReference(StyleProperty, "DialogWindowStyle");
        Title = "Параметры — SSH Explorer"; Width = 860; Height = 620; MinWidth = 640; MinHeight = 460;
        ResizeMode = ResizeMode.CanResize; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (Application.Current?.MainWindow is { IsLoaded: true } owner && !ReferenceEquals(owner, this)) Owner = owner; else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        AppIcon.Apply(this);
        theme.ItemsSource = new[] { "Как в Windows", "Светлая", "Тёмная" };
        theme.SelectedIndex = settings.Theme == "Light" ? 1 : settings.Theme == "Dark" ? 2 : 0;
        AutomationProperties.SetName(theme, "Тема приложения");
        reduceMotion.IsChecked = settings.ReduceMotion; showHidden.IsChecked = settings.ShowHidden; showExtensions.IsChecked = settings.ShowExtensions;
        poll.Value = settings.PollIntervalSeconds; font.Value = settings.TerminalFontSize;
        maxBytes.Value = settings.PreviewMaxBytes / 1024 / 1024; pixels.Value = settings.PreviewMaxPixels / 1_000_000;
        parallel.Value = settings.PreviewParallelism; cache.Value = settings.PreviewCacheBytes / 1024 / 1024;
        editor.Text = settings.ExternalEditorPath; AutomationProperties.SetName(editor, "Путь к редактору");
        BuildPages();

        var layout = new Grid(); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) }); layout.ColumnDefinitions.Add(new ColumnDefinition());
        var side = new DockPanel { Margin = new Thickness(8, 12, 4, 12) };
        var heading = Ui.DialogHeading("Параметры"); heading.Margin = new Thickness(12, 4, 12, 12); DockPanel.SetDock(heading, Dock.Top); side.Children.Add(heading);
        navigation.BorderThickness = new Thickness(0); navigation.Background = Brushes.Transparent;
        foreach (var item in Sections)
        {
            var row = new DockPanel { Margin = new Thickness(4, 2, 4, 2), MinHeight = 36 };
            var icon = Ui.Icon(item.Icon, 16, false); icon.Margin = new Thickness(4, 0, 12, 0); DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon);
            var text = Ui.Text(item.Title); text.TextTrimming = TextTrimming.CharacterEllipsis; row.Children.Add(text);
            var entry = new ListBoxItem { Content = row, Tag = item.Key, ToolTip = item.Description, Padding = new Thickness(6, 2, 6, 2) };
            AutomationProperties.SetName(entry, item.Title); AutomationProperties.SetHelpText(entry, item.Description);
            navigation.Items.Add(entry);
        }
        navigation.SelectionChanged += (_, _) => { if (navigation.SelectedItem is ListBoxItem { Tag: string key }) Show(key); };
        side.Children.Add(navigation); side.SetResourceReference(Panel.BackgroundProperty, "NavigationSurface");
        layout.Children.Add(side);
        var scroller = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(28, 20, 28, 20) };
        Grid.SetColumn(scroller, 1); layout.Children.Add(scroller);

        var save = Ui.AsyncButton("Сохранить", SaveAsync); save.IsDefault = true; save.SetResourceReference(StyleProperty, "PrimaryButtonStyle"); save.Name = "SettingsSave";
        var cancel = Ui.Button("Отмена", () => { DialogResult = false; }); cancel.IsCancel = true;
        var footer = new Border { Child = Ui.Actions(cancel, save) }; footer.SetResourceReference(StyleProperty, "DialogFooterStyle");
        var root = new DockPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); root.Children.Add(layout);
        root.SetResourceReference(Panel.BackgroundProperty, "Paper");
        Content = root;
        Show(section);
        Loaded += (_, _) => (navigation.SelectedItem as ListBoxItem)?.Focus();
    }

    public void Show(string key)
    {
        if (!pages.TryGetValue(key, out var content)) key = "general";
        CurrentSection = key; page.Content = pages[key];
        var entry = navigation.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == key);
        if (!ReferenceEquals(navigation.SelectedItem, entry)) navigation.SelectedItem = entry;
    }

    void BuildPages()
    {
        var hc = SystemParameters.HighContrast;
        pages["general"] = Page("Общие и внешний вид",
            Group("Тема", Setting("Тема приложения", "«Как в Windows» следует светлому или тёмному режиму системы. В режиме высокой контрастности используются системные цвета.", theme)),
            Group("Анимация", Setting(null, UiMotion.SystemAnimationsEnabled ? "Короткие переходы состояния. Системное отключение анимации Windows всегда имеет приоритет." : "Анимация отключена в параметрах Windows; переходы выполняются без движения.", reduceMotion)),
            Group("Файлы", Setting(null, null, showHidden), Setting(null, "Файлы, начинающиеся с точки, в Linux считаются скрытыми.", showExtensions)));
        _ = hc;

        var trustList = new ListBox { MinHeight = 120, MaxHeight = 220, DisplayMemberPath = nameof(TrustRow.DisplayName) };
        AutomationProperties.SetName(trustList, "Доверенные ключи серверов");
        void RefreshTrust() => trustList.ItemsSource = App.Store.Trust.Records.Select(r => new TrustRow(r.Host, r.Port, r.Algorithm, r.Fingerprint)).OrderBy(r => r.Host, StringComparer.CurrentCultureIgnoreCase).ToList();
        RefreshTrust();
        var removeTrust = Ui.AsyncButton("Удалить выбранный ключ…", async () =>
        {
            if (trustList.SelectedItem is not TrustRow row) return;
            if (!Ui.Confirm($"Удалить сохранённое доверие к {row.Host}:{row.Port} ({row.Algorithm})?\n\nПри следующем подключении ключ нужно будет проверить снова. Делайте это только после сверки нового отпечатка с администратором.", "Удаление доверенного ключа")) return;
            await App.Store.Trust.ForgetAsync(row.Host, row.Port, row.Algorithm); RefreshTrust();
        });
        removeTrust.IsEnabled = false; trustList.SelectionChanged += (_, _) => removeTrust.IsEnabled = trustList.SelectedItem is TrustRow;
        pages["connections"] = Page("Подключения",
            Group("Доверенные ключи серверов", Setting(null, "Ключ сервера проверяется до входа. Изменившийся ключ блокирует подключение и требует отдельного решения.", trustList), Buttons(removeTrust)),
            Group("Локальные копии для Windows", Setting(null, "Подготовленные копии файлов для буфера обмена и перетаскивания в Проводник. Файлы на сервере не затрагиваются.", Buttons(Ui.AsyncButton("Очистить локальные копии…", Dialogs.ClearTransferCacheAsync)))));

        editorState.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); editorState.TextWrapping = TextWrapping.Wrap;
        void UpdateEditorState() => editorState.Text = string.IsNullOrWhiteSpace(editor.Text) ? "Используется Блокнот Windows (" + System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe") + ")." : System.IO.File.Exists(editor.Text) ? "Будет запускаться выбранная программа; путь к рабочей копии передаётся отдельным аргументом." : "Файл не найден.";
        editor.TextChanged += (_, _) => { Ui.SetValidationMessage(editor, ""); UpdateEditorState(); }; UpdateEditorState();
        var browse = Ui.Button("Обзор…", () => { var pick = new Microsoft.Win32.OpenFileDialog { Filter = "Программа Windows|*.exe", CheckFileExists = true, Title = "Выберите установленный текстовый редактор" }; if (pick.ShowDialog(this) == true) editor.Text = pick.FileName; });
        var notepad = Ui.Button("Блокнот Windows", () => editor.Text = "");
        pages["terminal"] = Page("Терминал и редактор",
            Group("Терминал", font),
            Group("Редактор текстовых файлов", Setting("Программа", null, editor), editorState, Buttons(browse, notepad),
                Setting(null, "Документы, редактирование которых явно завершено, можно убрать из списка. Незавершённые копии не удаляются.", Buttons(Ui.AsyncButton("Очистить завершённые документы…", () => ((MainWindow)Application.Current.MainWindow).ClearCompletedDocumentsAsync())))));

        pages["monitoring"] = Page("Мониторинг",
            Group("Диспетчер задач", poll, Note("Новый замер начинается не раньше окончания предыдущего. Если сервер собирает данные дольше интервала, графики остаются непрерывными, а фактическое время сбора видно в «Сведениях». Скрытые разделы не опрашиваются.")));

        pages["preview"] = Page("Предпросмотр и кэш",
            Group("Изображения", maxBytes, pixels, Note("Большие файлы не скачиваются для миниатюры; их можно открыть отдельно.")),
            Group("Миниатюры", parallel, cache, Note("Количество запросов применяется к новым вкладкам. F5 в файловом менеджере очищает кэш миниатюр текущей вкладки.")));

        var icon = new Image { Source = AppIcon.ImageForSize(64, VisualTreeHelper.GetDpi(this).DpiScaleX), Width = 64, Height = 64, Margin = new Thickness(0, 0, 16, 0) };
        var identity = new StackPanel(); identity.Children.Add(Ui.Text("SSH Explorer", 18, true));
        foreach (var line in new[] { "Версия " + BuildInfo.DisplayVersion, "Сборка " + (BuildInfo.Commit.Length > 12 ? BuildInfo.Commit[..12] : BuildInfo.Commit), RuntimeInformation.FrameworkDescription + " · " + RuntimeInformation.ProcessArchitecture })
        { var text = Ui.Text(line, 12); text.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); text.Margin = new Thickness(0, 2, 0, 0); identity.Children.Add(text); }
        var header = new DockPanel(); DockPanel.SetDock(icon, Dock.Left); header.Children.Add(icon); header.Children.Add(identity);
        var copyVersion = Ui.Button("Скопировать сведения о версии", () => Clipboard.SetText(BuildInfo.EnvironmentDescription + "\ncommit " + BuildInfo.Commit));
        var data = Ui.Text(App.Store.DirectoryPath, 12); data.TextWrapping = TextWrapping.Wrap; data.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var openData = Ui.Button("Открыть папку", () => OpenFolder(App.Store.DirectoryPath));
        var copyLogs = Ui.Button("Скопировать путь к диагностике", () => Clipboard.SetText(System.IO.Path.Combine(App.Store.DirectoryPath, "Logs")));
        var notices = Ui.Button("Сторонние компоненты и лицензии", () => OpenFolder(System.IO.Path.Combine(AppContext.BaseDirectory, "licenses")));
        pages["about"] = Page("О приложении", header,
            Group("Сборка", Buttons(copyVersion)),
            Group("Настройки и диагностика", Setting("Папка данных", "Профили, доверенные ключи, защищённые секреты (DPAPI текущего пользователя) и журналы диагностики без паролей и содержимого файлов.", data), Buttons(openData, copyLogs)),
            Group("Лицензии", Note("SSH.NET, xterm.js, Microsoft Edge WebView2 Runtime и .NET распространяются по своим лицензиям; тексты включены в папку licenses рядом с программой."), Buttons(notices)));
    }

    static void OpenFolder(string path)
    {
        if (!System.IO.Directory.Exists(path)) { Ui.Error("Папка не найдена: " + path); return; }
        var explorer = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var info = new System.Diagnostics.ProcessStartInfo(explorer) { UseShellExecute = false }; info.ArgumentList.Add(path);
        try { System.Diagnostics.Process.Start(info)?.Dispose(); } catch (Exception error) { Ui.Error("Не удалось открыть папку: " + error.Message); }
    }

    static FrameworkElement Page(string title, params FrameworkElement[] content)
    {
        var panel = new StackPanel { MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
        var heading = Ui.DialogHeading(title); heading.Margin = new Thickness(0, 0, 0, 16); panel.Children.Add(heading);
        foreach (var item in content) panel.Children.Add(item);
        return panel;
    }

    static FrameworkElement Group(string title, params FrameworkElement[] settings)
    {
        var card = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(16, 12, 16, 14), Margin = new Thickness(0, 0, 0, 12), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "Surface"); card.SetResourceReference(Border.BorderBrushProperty, "Edge");
        var stack = new StackPanel();
        var caption = Ui.Text(title, 14, true); caption.Margin = new Thickness(0, 0, 0, 8); stack.Children.Add(caption);
        foreach (var setting in settings) { setting.Margin = new Thickness(0, 4, 0, 6); stack.Children.Add(setting); }
        card.Child = stack; return card;
    }

    /// <summary>A label directly above its control, with an optional explanation under the control.</summary>
    static FrameworkElement Setting(string? label, string? help, FrameworkElement control)
    {
        var stack = new StackPanel();
        if (label is not null) { var text = Ui.Text(label); text.Margin = new Thickness(0, 0, 0, 6); stack.Children.Add(text); AutomationProperties.SetName(control, AutomationProperties.GetName(control) is { Length: > 0 } name ? name : label); }
        if (control.Parent is Panel old) old.Children.Remove(control);
        stack.Children.Add(control);
        if (help is not null) stack.Children.Add(Note(help));
        return stack;
    }

    static TextBlock Note(string text)
    {
        var note = Ui.Text(text, 12); note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(0, 6, 0, 0); note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return note;
    }

    static FrameworkElement Buttons(params Button[] buttons)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var button in buttons) { button.Margin = new Thickness(0, 0, 8, 4); row.Children.Add(button); }
        return row;
    }

    async Task SaveAsync()
    {
        NumberField? invalid = null;
        foreach (var field in new[] { poll, font, maxBytes, pixels, parallel, cache }) if (!field.Validate()) invalid ??= field;
        if (invalid is not null)
        {
            Show(invalid == poll ? "monitoring" : invalid == font ? "terminal" : "preview");
            invalid.FocusInput(); return;
        }
        var path = editor.Text.Trim();
        if (path.Length > 0 && (!System.IO.Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(path)))
        { Show("terminal"); Ui.SetValidationMessage(editor, "Выберите существующую программу (.exe) на этом компьютере или вернитесь к Блокноту."); editor.Focus(); return; }
        settings.ExternalEditorPath = path;
        settings.PollIntervalSeconds = (int)poll.Value; settings.TerminalFontSize = (int)font.Value;
        settings.PreviewMaxBytes = maxBytes.Value * 1024 * 1024; settings.PreviewMaxPixels = pixels.Value * 1_000_000;
        settings.PreviewParallelism = (int)parallel.Value; settings.PreviewCacheBytes = cache.Value * 1024 * 1024;
        settings.ShowHidden = showHidden.IsChecked == true; settings.ShowExtensions = showExtensions.IsChecked == true;
        settings.ReduceMotion = reduceMotion.IsChecked == true; UiMotion.UserReduced = settings.ReduceMotion;
        settings.Theme = theme.SelectedIndex == 1 ? "Light" : theme.SelectedIndex == 2 ? "Dark" : "System"; Theme.Apply(settings.Theme);
        await App.Store.SaveAsync();
        DialogResult = true;
    }

    sealed record TrustRow(string Host, int Port, string Algorithm, string Fingerprint) { public string DisplayName => $"{Host}:{Port} · {Algorithm}\n{Fingerprint}"; }
}

/// <summary>Integer input with a unit, range validation, arrow-key and button stepping.</summary>
public sealed class NumberField : StackPanel
{
    readonly TextBox input = new() { MinWidth = 96, HorizontalContentAlignment = HorizontalAlignment.Right };
    readonly TextBlock error = Ui.Text("", 12);
    public long Minimum { get; } public long Maximum { get; } public long Step { get; }
    public string Label { get; }
    public long Value
    {
        get => long.TryParse(input.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) ? value : Minimum;
        set => input.Text = Math.Clamp(value, Minimum, Maximum).ToString(CultureInfo.CurrentCulture);
    }
    public TextBox Input => input;

    public NumberField(string label, string unit, long minimum, long maximum, long step)
    {
        Label = label; Minimum = minimum; Maximum = maximum; Step = step;
        var caption = Ui.Text(label); caption.Margin = new Thickness(0, 0, 0, 6); Children.Add(caption);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        AutomationProperties.SetName(input, label); AutomationProperties.SetHelpText(input, $"Целое число от {minimum} до {maximum} ({unit})");
        input.PreviewKeyDown += (_, e) => { if (e.Key == Key.Up) { Nudge(1); e.Handled = true; } else if (e.Key == Key.Down) { Nudge(-1); e.Handled = true; } };
        input.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
        input.TextChanged += (_, _) => { if (error.Visibility == Visibility.Visible) Validate(); };
        DataObject.AddPastingHandler(input, (_, e) => { if (e.DataObject.GetData(DataFormats.UnicodeText) is string text && !text.Trim().All(char.IsDigit)) e.CancelCommand(); });
        row.Children.Add(input);
        var unitText = Ui.Text(unit); unitText.Margin = new Thickness(8, 0, 12, 0); unitText.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); row.Children.Add(unitText);
        var down = Ui.IconButton(IconCatalog.Prefix.Length > 0 ? "" : "-", () => Nudge(-1), "Уменьшить: " + label); down.Width = 32;
        var up = Ui.IconButton(Ui.AddGlyph, () => Nudge(1), "Увеличить: " + label); up.Width = 32;
        row.Children.Add(down); row.Children.Add(up);
        var range = Ui.Text($"{minimum}–{maximum}", 12); range.Margin = new Thickness(12, 0, 0, 0); range.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); row.Children.Add(range);
        Children.Add(row);
        error.Visibility = Visibility.Collapsed; error.TextWrapping = TextWrapping.Wrap; error.Margin = new Thickness(0, 4, 0, 0); error.SetResourceReference(TextBlock.ForegroundProperty, "ValidationInk"); Children.Add(error);
    }

    void Nudge(int direction) { Value = Math.Clamp(Value + direction * Step, Minimum, Maximum); input.CaretIndex = input.Text.Length; Validate(); }

    public bool Validate()
    {
        bool ok = long.TryParse(input.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) && value >= Minimum && value <= Maximum;
        var message = ok ? "" : $"Введите целое число от {Minimum} до {Maximum}.";
        error.Text = message; error.Visibility = ok ? Visibility.Collapsed : Visibility.Visible; Ui.SetValidationMessage(input, message);
        return ok;
    }

    public void FocusInput() { input.Focus(); input.SelectAll(); input.BringIntoView(); }
}
