using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>
/// Read-only text view of a server file: encoding and line endings detected like the editor does, a manual encoding choice,
/// search (Ctrl+F, Enter / Shift+Enter) and word wrap. Files over 10 MiB or binary content are explained, not shown.
/// </summary>
public sealed class TextViewerWindow : Window
{
    public const long MaximumBytes = 10 * 1024 * 1024;
    readonly Func<CancellationToken, Task<byte[]>> read;
    readonly TextBox text = new() { Name = "ViewerText", IsReadOnly = true, IsReadOnlyCaretVisible = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap, BorderThickness = new Thickness(0), Padding = new Thickness(12, 8, 12, 8), VerticalContentAlignment = VerticalAlignment.Top };
    readonly TextBox search = new() { Name = "ViewerSearch", MinWidth = 220, MinHeight = 30 };
    readonly ComboBox encoding = new() { ItemsSource = new[] { "Определить", "utf-8", "windows-1251", "koi8-r", "utf-16", "utf-16BE", "iso-8859-1" }, SelectedIndex = 0, MinWidth = 140, Name = "ViewerEncoding" };
    readonly CheckBox wrap = new() { Content = "Переносить строки", Name = "ViewerWrap", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    readonly TextBlock state = Ui.Text("Чтение…", 12);
    readonly CancellationTokenSource lifetime = new();
    byte[]? bytes;
    public string Shown => text.Text;

    public TextViewerWindow(string title, Func<CancellationToken, Task<byte[]>> readBytes)
    {
        read = readBytes; Title = "Просмотр: " + title; Width = 980; Height = 760; MinWidth = 520; MinHeight = 360; ShowInTaskbar = true; WindowStartupLocation = WindowStartupLocation.CenterOwner; Style = (Style)FindResource(typeof(Window));
        var bar = new DockPanel { Margin = new Thickness(12, 8, 12, 8) };
        var right = Ui.Row(Ui.Text("Кодировка ", 12), encoding, wrap); DockPanel.SetDock(right, Dock.Right); bar.Children.Add(right);
        var find = Ui.Row(Ui.Icon("", 16), search); bar.Children.Add(find);
        System.Windows.Automation.AutomationProperties.SetName(search, "Найти в тексте");
        state.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); state.Margin = new Thickness(16, 6, 16, 6); state.Name = "ViewerState"; state.TextTrimming = TextTrimming.CharacterEllipsis;
        var footer = new Border { Child = state }; footer.SetResourceReference(StyleProperty, "StatusBarStyle");
        var barHost = new Border { Child = bar }; barHost.SetResourceReference(StyleProperty, "CommandBarStyle");
        text.SetResourceReference(BackgroundProperty, "Paper");
        var root = new DockPanel(); DockPanel.SetDock(barHost, Dock.Top); root.Children.Add(barHost); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); root.Children.Add(text);
        Content = root;
        wrap.Click += (_, _) => text.TextWrapping = wrap.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;
        encoding.SelectionChanged += (_, _) => Render();
        search.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Find(backwards: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); e.Handled = true; } };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { search.Focus(); search.SelectAll(); e.Handled = true; } else if (e.Key == Key.Escape) Close(); };
        Loaded += async (_, _) => await Ui.Guard(LoadAsync);
        Closed += (_, _) => lifetime.Cancel();
    }

    internal async Task LoadAsync()
    {
        try { bytes = await read(lifetime.Token); }
        catch (Exception ex) when (ex is not OperationCanceledException) { state.Text = "Не удалось прочитать файл: " + ErrorMessages.Describe(ex); return; }
        Render();
    }

    void Render()
    {
        if (bytes is null) return;
        try
        {
            var chosen = encoding.SelectedIndex <= 0 ? null : (string)encoding.SelectedItem;
            var (content, format) = DocumentText.Decode(bytes, chosen);
            text.Text = content;
            int lines = content.Length == 0 ? 0 : content.Count(c => c == '\n') + (content.EndsWith('\n') ? 0 : 1);
            state.Text = $"{Ui.Size(bytes.LongLength)} · строк: {lines} · {format.Description} · только просмотр";
        }
        catch (System.IO.IOException ex) { text.Text = ""; state.Text = ex.Message + (encoding.SelectedIndex <= 0 ? " Можно выбрать кодировку вручную." : ""); }
    }

    /// <summary>Finds the search text after (or before) the caret, case-insensitive, and selects it.</summary>
    internal bool Find(bool backwards = false)
    {
        var needle = search.Text; if (needle.Length == 0 || text.Text.Length == 0) return false;
        int from = backwards ? Math.Max(0, text.SelectionStart - 1) : text.SelectionStart + Math.Max(text.SelectionLength, 0);
        int index = backwards ? text.Text.LastIndexOf(needle, Math.Min(from, text.Text.Length - 1), StringComparison.CurrentCultureIgnoreCase) : text.Text.IndexOf(needle, Math.Min(from, text.Text.Length), StringComparison.CurrentCultureIgnoreCase);
        if (index < 0) index = backwards ? text.Text.LastIndexOf(needle, StringComparison.CurrentCultureIgnoreCase) : text.Text.IndexOf(needle, StringComparison.CurrentCultureIgnoreCase);
        if (index < 0) { state.Text = $"«{needle}» не найдено"; return false; }
        text.Focus(); text.Select(index, needle.Length);
        var line = text.GetLineIndexFromCharacterIndex(index); if (line >= 0) text.ScrollToLine(line);
        return true;
    }
}
