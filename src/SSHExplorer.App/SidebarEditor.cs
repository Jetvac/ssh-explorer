using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>Title, icon and explanation of each top-level server section.</summary>
public static class SidebarCatalog
{
    public sealed record Info(string Glyph, string Title, string Description);
    public static readonly IReadOnlyDictionary<string, Info> Entries = new Dictionary<string, Info>
    {
        ["server"] = new(IconCatalog.Server, "Сервер", "Сводка, питание и состояние сервера"),
        ["terminal"] = new(IconCatalog.Terminal, "Терминал", "Терминалы, в том числе разделённые"),
        ["monitor"] = new(IconCatalog.Performance, "Диспетчер задач", "Процессы, производительность и службы"),
        ["tunnels"] = new(IconCatalog.Network, "Туннели", "Проброс портов через SSH"),
        ["firewall"] = new(IconCatalog.Firewall, "Файервол", "Правила ufw и доступ только через промежуточный сервер"),
        ["security"] = new(IconCatalog.Security, "Защита SSH", "Вход по ключу, ключи сервера и проверка настроек SSH"),
        ["cron"] = new(IconCatalog.Clock, "Задания cron", "Расписание команд сервера (crontab) с понятным описанием и ближайшими запусками"),
        ["docker"] = new(IconCatalog.Docker, "Docker", "Модуль: контейнеры, журналы и параметры запуска. Команды docker выполняются по SSH от вашей учётной записи; на сервер ничего не устанавливается."),
        ["scripts"] = new("", "Скрипты", "Модуль: сохранённые команды — вручную, при подключении, по расписанию. Пока модуль скрыт, скрипты сами не запускаются."),
        ["files"] = new("", "Файлы", "Файловый менеджер — основной раздел, его нельзя скрыть"),
    };
}

/// <summary>
/// In-place editor of the server panel: a tick shows a section, the grip or the arrows (Alt+↑/↓) move it.
/// Every change is saved at once and applied to all open tabs; «Готово» or Esc returns to the panel.
/// </summary>
public sealed class SidebarEditor : StackPanel
{
    const string DragFormat = "sshx-sidebar-section";
    readonly Action closed;
    readonly StackPanel rows = new() { Name = "SidebarEditorRows" };
    readonly Button done, reset;
    IReadOnlyList<SidebarItem> items = [];
    Point? dragStart; string? dragKey;

    public SidebarEditor(Action closed)
    {
        this.closed = closed; Name = "SidebarEditor"; Margin = new Thickness(2, 0, 2, 0);
        var title = Ui.Text("Панель разделов", 13, true); title.Margin = new Thickness(6, 2, 6, 2); Children.Add(title);
        var hint = Ui.Text("Флажок — показывать раздел. Порядок — перетащите за ⠿ или стрелками (Alt+↑/↓).", 11); hint.TextWrapping = TextWrapping.Wrap; hint.Margin = new Thickness(6, 0, 6, 8); hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); Children.Add(hint);
        Children.Add(rows);
        var note = Ui.Text("Docker и «Скрипты» — модули: скрытый модуль выключен.", 11); note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(6, 8, 6, 8); note.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); Children.Add(note);
        done = Ui.Button("Готово", Done); done.Name = "SidebarDone"; done.SetResourceReference(StyleProperty, "PrimaryButtonStyle"); done.MinWidth = 76; done.Margin = new Thickness(4, 0, 6, 6);
        reset = Ui.Button("По умолчанию", () => Save(Sidebar.Defaults())); reset.Name = "SidebarReset"; reset.Margin = new Thickness(0, 0, 6, 6);
        reset.ToolTip = "Обычный порядок; модули Docker и «Скрипты» скрыты";
        var buttons = new WrapPanel(); buttons.Children.Add(done); buttons.Children.Add(reset); Children.Add(buttons);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Done(); e.Handled = true; } };
    }

    internal IReadOnlyList<SidebarItem> Items => items;

    /// <summary>Saves a layout and applies it to every open tab.</summary>
    public static void Save(IReadOnlyList<SidebarItem> layout)
    {
        if (!Sidebar.Apply(App.Store.Settings, layout)) return;
        _ = SaveQuietlyAsync(); App.RaiseModulesChanged();
        static async Task SaveQuietlyAsync()
        {
            try { await App.Store.SaveAsync(); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { await LocalDiagnostics.RecordAsync("sidebar.save.failed", ex); }
        }
    }

    public void Done() => closed();

    public void FocusFirst() => Dispatcher.BeginInvoke(() => rows.Children.OfType<FrameworkElement>().SelectMany(r => Descendants<CheckBox>(r)).FirstOrDefault(c => c.IsEnabled)?.Focus(), System.Windows.Threading.DispatcherPriority.Input);

    public void Refresh(IReadOnlyList<SidebarItem> layout)
    {
        // Keep keyboard focus on the same row and control across the rebuild.
        string? focusKey = null, focusPart = null;
        if (Keyboard.FocusedElement is FrameworkElement { Tag: string tag } f && IsInside(f, rows)) { var parts = tag.Split('|'); if (parts.Length == 2) { focusKey = parts[0]; focusPart = parts[1]; } }
        items = layout; rows.Children.Clear();
        for (int i = 0; i < items.Count; i++) rows.Children.Add(Row(items[i], i));
        if (focusKey is not null)
            Dispatcher.BeginInvoke(() =>
            {
                var target = rows.Children.OfType<FrameworkElement>().SelectMany(r => Descendants<Control>(r)).FirstOrDefault(c => Equals(c.Tag, focusKey + "|" + focusPart) && c.IsEnabled)
                    ?? rows.Children.OfType<FrameworkElement>().SelectMany(r => Descendants<Control>(r)).FirstOrDefault(c => Equals(c.Tag, focusKey + "|show"));
                target?.Focus();
            }, System.Windows.Threading.DispatcherPriority.Input);
    }

    FrameworkElement Row(SidebarItem item, int index)
    {
        var info = SidebarCatalog.Entries[item.Key];
        var grid = new Grid { Name = "SidebarRow_" + item.Key, Tag = item.Key, AllowDrop = true, MinHeight = 34, Background = Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var grip = Ui.Text("⠿", 14); grip.VerticalAlignment = VerticalAlignment.Center; grip.HorizontalAlignment = HorizontalAlignment.Center; grip.Cursor = Cursors.SizeAll; grip.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        grip.ToolTip = "Перетащите, чтобы изменить порядок"; grid.Children.Add(grip);
        grip.MouseLeftButtonDown += (_, e) => { dragStart = e.GetPosition(this); dragKey = item.Key; grip.CaptureMouse(); e.Handled = true; };
        grip.MouseLeftButtonUp += (_, _) => { dragStart = null; dragKey = null; grip.ReleaseMouseCapture(); };
        grip.MouseMove += (_, e) =>
        {
            if (dragStart is not { } start || dragKey is null || e.LeftButton != MouseButtonState.Pressed) return;
            var now = e.GetPosition(this);
            if (Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance && Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance) return;
            var key = dragKey; dragStart = null; dragKey = null; grip.ReleaseMouseCapture();
            DragDrop.DoDragDrop(grip, new DataObject(DragFormat, key), DragDropEffects.Move);
            ClearDropMarks();
        };

        var label = new DockPanel { LastChildFill = true };
        var icon = Ui.Icon(info.Glyph, 16, false); icon.Margin = new Thickness(0, 0, 8, 0); DockPanel.SetDock(icon, Dock.Left); label.Children.Add(icon);
        if (Sidebar.IsModule(item.Key))
        {
            var tag = new Border { CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(1) };
            tag.SetResourceReference(Border.BorderBrushProperty, "Edge"); var tagText = Ui.Text("модуль", 10); tagText.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); tag.Child = tagText;
            DockPanel.SetDock(tag, Dock.Right); label.Children.Add(tag);
        }
        var name = Ui.Text(info.Title); name.ClearValue(TextBlock.FontWeightProperty); name.TextTrimming = TextTrimming.CharacterEllipsis; name.VerticalAlignment = VerticalAlignment.Center; label.Children.Add(name);
        var show = new CheckBox { Content = label, IsChecked = item.Visible, IsEnabled = Sidebar.CanHide(item.Key), VerticalAlignment = VerticalAlignment.Center, Tag = item.Key + "|show", Margin = new Thickness(2, 0, 4, 0), ToolTip = info.Description };
        ToolTipService.SetShowOnDisabled(show, true);
        AutomationProperties.SetName(show, "Показывать «" + info.Title + "»"); AutomationProperties.SetHelpText(show, info.Description);
        show.Click += (_, _) => Save(Sidebar.SetVisible(items, item.Key, show.IsChecked == true));
        Grid.SetColumn(show, 1); grid.Children.Add(show);

        Button Arrow(string glyph, int delta, string text, string part)
        {
            var b = Ui.IconButton(glyph, () => Save(Sidebar.Move(items, item.Key, delta)), text + " «" + info.Title + "»"); b.Width = 28; b.Height = 28; b.Tag = item.Key + "|" + part;
            AutomationProperties.SetName(b, text + " «" + info.Title + "»"); return b;
        }
        var up = Arrow("", -1, "Выше", "up"); up.IsEnabled = index > 0; Grid.SetColumn(up, 2); grid.Children.Add(up);
        var down = Arrow("", 1, "Ниже", "down"); down.IsEnabled = index < items.Count - 1; Grid.SetColumn(down, 3); grid.Children.Add(down);

        grid.PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Alt) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.Up or Key.Down) { Save(Sidebar.Move(items, item.Key, key == Key.Up ? -1 : 1)); e.Handled = true; }
        };
        grid.DragOver += (_, e) =>
        {
            if (!e.Data.GetDataPresent(DragFormat)) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
            e.Effects = DragDropEffects.Move; bool lower = e.GetPosition(grid).Y > grid.ActualHeight / 2;
            ClearDropMarks(); Mark(grid, lower); e.Handled = true;
        };
        grid.DragLeave += (_, _) => Unmark(grid);
        grid.Drop += (_, e) =>
        {
            ClearDropMarks();
            if (e.Data.GetData(DragFormat) is not string key) return;
            bool lower = e.GetPosition(grid).Y > grid.ActualHeight / 2;
            int from = items.ToList().FindIndex(i => i.Key == key), target = index + (lower ? 1 : 0);
            if (from < 0) return; if (from < target) target--;
            Save(Sidebar.MoveTo(items, key, target)); e.Handled = true;
        };
        var frame = new Border { Child = grid, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(0, 2, 0, 2), BorderBrush = Brushes.Transparent, Tag = item.Key };
        return frame;
    }

    void Mark(Grid grid, bool lower) { if (grid.Parent is Border b) { b.BorderBrush = (Brush)FindResource("Accent"); b.BorderThickness = lower ? new Thickness(0, 0, 0, 4) : new Thickness(0, 4, 0, 0); } }
    static void Unmark(Grid grid) { if (grid.Parent is Border b) { b.BorderBrush = Brushes.Transparent; b.BorderThickness = new Thickness(0, 2, 0, 2); } }
    void ClearDropMarks() { foreach (var b in rows.Children.OfType<Border>()) if (b.Child is Grid g) Unmark(g); }

    static bool IsInside(DependencyObject node, DependencyObject root) { for (var n = node; n is not null; n = VisualTreeHelper.GetParent(n) ?? LogicalTreeHelper.GetParent(n)) if (ReferenceEquals(n, root)) return true; return false; }
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject { foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) { if (child is T match) yield return match; foreach (var nested in Descendants<T>(child)) yield return nested; } }
}
