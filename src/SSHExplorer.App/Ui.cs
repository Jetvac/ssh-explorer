using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Automation;
using Microsoft.Win32;

namespace SSHExplorer.App;

public static class Ui
{
    public const string AddGlyph = "\uE710", BackGlyph = "\uE72B", ForwardGlyph = "\uE72A", UpGlyph = "\uE74A", RefreshGlyph = "\uE72C", HomeGlyph = "\uE80F", FolderGlyph = "\uE8B7", CutGlyph = "\uE8C6", CopyGlyph = "\uE8C8", PasteGlyph = "\uE77F", RenameGlyph = "\uE8AC", DeleteGlyph = "\uE74D", UploadGlyph = "\uE898", DownloadGlyph = "\uE896", SortGlyph = "\uE8CB", ViewGlyph = "\uE8A9", MoreGlyph = "\uE712", SettingsGlyph = "\uE713", CloseGlyph = "\uE711", SearchGlyph = "\uE721";
    public static Button Button(string text, Action action, string? tooltip = null)
    {
        var symbols = new Dictionary<string, string> { ["←"] = BackGlyph, ["→"] = ForwardGlyph, ["↑"] = UpGlyph, ["↓"] = DownloadGlyph, ["＋"] = AddGlyph, ["+"] = AddGlyph, ["−"] = "\uE738", ["×"] = CloseGlyph, ["⚙"] = SettingsGlyph, ["⇄"] = "\uE8AB" };
        var b = new Button { Content = symbols.TryGetValue(text, out var glyph) ? Icon(glyph, 16, false) : text, ToolTip = tooltip ?? text };
        AutomationProperties.SetName(b, tooltip ?? text);
        b.Click += (_, _) => action();
        return b;
    }
    public static Button AsyncButton(string text, Func<Task> action, string? tooltip = null) => Button(text, async () => await Guard(action), tooltip);
    public static Button CommandButton(string glyph, string label, Action action, string? tooltip = null, bool chevron = false)
    {
        var button = Button("", action, tooltip ?? label); button.SetResourceReference(FrameworkElement.StyleProperty, "CommandButtonStyle");
        var row = Row(Icon(glyph, 16, label.Length > 0)); if (label.Length > 0) row.Children.Add(Text(label));
        if (chevron) { var arrow = Icon("\uE70D", 10, false); arrow.Margin = new Thickness(8, 0, 0, 0); row.Children.Add(arrow); }
        button.Content = row; return button;
    }
    public static Button AsyncCommandButton(string glyph, string label, Func<Task> action, string? tooltip = null, bool chevron = false) => CommandButton(glyph, label, async () => await Guard(action), tooltip, chevron);
    public static Button IconButton(string glyph, Action action, string tooltip) { var b = CommandButton(glyph, "", action, tooltip); b.Width = 36; b.Padding = new Thickness(0); return b; }
    public static Button AsyncIconButton(string glyph, Func<Task> action, string tooltip) => IconButton(glyph, async () => await Guard(action), tooltip);
    public static async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _ = LocalDiagnostics.RecordAsync("ui.operation.failure", ex); Error(ex.Message); }
    }
    public static TextBlock Text(string text, double size = 14, bool bold = false) => new() { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center };
    public static TextBlock Icon(string glyph, double size = 18, bool trailingSpace = true) => new() { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = size, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = trailingSpace ? new Thickness(0, 0, 9, 0) : new Thickness(0) };
    public static StackPanel Row(params UIElement[] items) { var p = new StackPanel { Orientation = Orientation.Horizontal }; foreach (var x in items) p.Children.Add(x); return p; }
    public static MenuItem Menu(string text, Action action, string shortcut = "") { var m = new MenuItem { Header = text, InputGestureText = shortcut, Icon = Icon(MenuGlyph(text), 16, false) }; m.Click += (_, _) => action(); return m; }
    static string MenuGlyph(string text) => text switch { var t when t.StartsWith("Создать") => AddGlyph, var t when t.StartsWith("Вырезать") => CutGlyph, var t when t.StartsWith("Копировать") => CopyGlyph, var t when t.StartsWith("Вставить") => PasteGlyph, var t when t.StartsWith("Переименовать") => RenameGlyph, var t when t.StartsWith("Удалить") => DeleteGlyph, var t when t.StartsWith("Скачать") => DownloadGlyph, var t when t.StartsWith("Загрузить") => UploadGlyph, var t when t.StartsWith("Обновить") => RefreshGlyph, var t when t.StartsWith("Свойства") => "\uE946", var t when t.Contains("терминале") => "\uE756", var t when t.Contains("Поиск") || t.Contains("Искать") => SearchGlyph, var t when t.Contains("Закрепить") || t.Contains("Открепить") => "\uE718", var t when t.StartsWith("Открыть") => FolderGlyph, _ => "\uE8A5" };
    public static MenuItem AsyncMenu(string text, Func<Task> action, string shortcut = "") => Menu(text, async () => await Guard(action), shortcut);
    public static Border Line() { var b = new Border { Width = 1, Height = 26, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 5, 9, 5) }; b.SetResourceReference(Border.BackgroundProperty, "Edge"); return b; }
    public static void Error(string message) => MessageBox.Show(Application.Current.MainWindow, message, "SSH Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
    public static bool Confirm(string message, string title = "SSH Explorer") => MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    public static string Size(long? bytes)
    {
        if (bytes is null) return "—";
        double n = bytes.Value; var units = new[] { "Б", "КБ", "МБ", "ГБ", "ТБ" }; var i = 0;
        while (n >= 1024 && i < units.Length - 1) { n /= 1024; i++; }
        return $"{n:0.##} {units[i]}";
    }
    public static string? Prompt(string title, string label, string value = "")
    {
        var w = new Window { Title = title, Width = 500, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Application.Current.MainWindow };
        var p = new StackPanel { Margin = new Thickness(22) }; p.Children.Add(Text(label));
        var t = new TextBox { Text = value, Margin = new Thickness(0, 12, 0, 16) }; p.Children.Add(t);
        var ok = Button("Готово", () => w.DialogResult = true); ok.IsDefault = true;
        var cancel = Button("Отмена", () => w.DialogResult = false); cancel.IsCancel = true;
        var buttons = Row(ok, cancel); buttons.HorizontalAlignment = HorizontalAlignment.Right; p.Children.Add(buttons); w.Content = p;
        w.Loaded += (_, _) => { t.Focus(); t.SelectAll(); };
        return w.ShowDialog() == true ? t.Text : null;
    }
}

public static class Theme
{
    public static event Action? Changed;
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    static bool darkFrame;
    static string? fluentTheme;
    public static string Current { get; private set; } = "System";
    public static void ApplyWindowFrame(Window window)
    {
        if (SystemParameters.HighContrast) return;
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        int value = darkFrame ? 1 : 0;
        DwmSetWindowAttribute(handle, 20, ref value, sizeof(int));
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) { int rounded = 2; DwmSetWindowAttribute(handle, 33, ref rounded, sizeof(int)); }
    }
    public static void Apply(string mode)
    {
        Current = mode;
        bool dark = mode == "Dark" || mode == "System" && Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int x && x == 0;
        var nativeTheme = SystemParameters.HighContrast ? "HC" : dark ? "Dark" : "Light";
        if (fluentTheme != nativeTheme)
        {
            var dictionaries = Application.Current.Resources.MergedDictionaries;
            var dictionary = dictionaries.FirstOrDefault(d => d.Source?.OriginalString.Contains("PresentationFramework.Fluent", StringComparison.OrdinalIgnoreCase) == true);
            var replacement = new ResourceDictionary { Source = new Uri($"/PresentationFramework.Fluent;component/Themes/Fluent.{nativeTheme}.xaml", UriKind.Relative) };
            if (dictionary is not null) dictionaries[dictionaries.IndexOf(dictionary)] = replacement; else dictionaries.Insert(0, replacement);
            fluentTheme = nativeTheme;
        }
        darkFrame = dark;
        foreach (Window window in Application.Current.Windows) ApplyWindowFrame(window);
        var r = Application.Current.Resources;
        if (SystemParameters.HighContrast)
        {
            r["Paper"] = SystemColors.WindowBrush; r["Surface"] = SystemColors.ControlBrush; r["NavigationSurface"] = SystemColors.WindowBrush; r["CaptionSurface"] = SystemColors.ControlBrush; r["Ink"] = SystemColors.WindowTextBrush; r["SelectedInk"] = SystemColors.HighlightTextBrush; r["AccentInk"] = SystemColors.HighlightTextBrush; r["Muted"] = SystemColors.GrayTextBrush; r["Edge"] = SystemColors.WindowTextBrush; r["Hover"] = SystemColors.ControlBrush; r["Pressed"] = SystemColors.HighlightBrush; r["Selected"] = SystemColors.HighlightBrush; r["SelectedInactive"] = SystemColors.ControlBrush; r["Accent"] = SystemColors.HighlightBrush; r["DropTarget"] = SystemColors.HighlightBrush; r["DisabledInk"] = SystemColors.GrayTextBrush;
            r["AccentHover"] = SystemColors.HighlightBrush; r["AccentPressed"] = SystemColors.HighlightBrush;
            Changed?.Invoke();
            return;
        }
        Brush B(string s) => (Brush)new BrushConverter().ConvertFromString(s)!;
        r["Paper"] = B(dark ? "#202020" : "#FFFFFF"); r["Surface"] = B(dark ? "#292929" : "#F3F3F3");
        r["NavigationSurface"] = B(dark ? "#252525" : "#FAFAFA"); r["CaptionSurface"] = B(dark ? "#242424" : "#E9ECEC");
        r["Ink"] = B(dark ? "#F4F4F4" : "#1A1A1A"); r["Muted"] = B(dark ? "#B7B7B7" : "#626262");
        r["SelectedInk"] = r["Ink"];
        r["Edge"] = B(dark ? "#414141" : "#E4E4E4"); r["Hover"] = B(dark ? "#343434" : "#EFEFEF"); r["Pressed"] = B(dark ? "#3C3C3C" : "#E3E3E3"); r["SelectedInactive"] = B(dark ? "#353B40" : "#E8EBEF"); r["DisabledInk"] = B(dark ? "#777777" : "#A1A1A1");
        var accent = SystemColors.AccentColor; accent.A = 255;
        static Color Mix(Color a, Color b, double amount) => Color.FromRgb((byte)(a.R * amount + b.R * (1 - amount)), (byte)(a.G * amount + b.G * (1 - amount)), (byte)(a.B * amount + b.B * (1 - amount)));
        r["Accent"] = new SolidColorBrush(accent); r["AccentInk"] = accent.R * .299 + accent.G * .587 + accent.B * .114 > 165 ? Brushes.Black : Brushes.White; r["Selected"] = new SolidColorBrush(Mix(accent, dark ? Color.FromRgb(32, 32, 32) : Colors.White, dark ? .34 : .16)); r["DropTarget"] = new SolidColorBrush(Mix(accent, dark ? Color.FromRgb(32, 32, 32) : Colors.White, dark ? .55 : .28));
        r["AccentHover"] = new SolidColorBrush(Mix(accent, Colors.Black, .92)); r["AccentPressed"] = new SolidColorBrush(Mix(accent, Colors.Black, .84));
        Changed?.Invoke();
    }
}

internal static class UiTestDiagnostics
{
    public static void Record(Exception exception)
    {
        if (Application.Current.MainWindow?.Title.Contains("ИЗОЛИРОВАННЫЙ ИСПЫТАТЕЛЬНЫЙ РЕЖИМ", StringComparison.Ordinal) != true) return;
        try { System.IO.File.AppendAllText(System.IO.Path.Combine(App.Store.DirectoryPath, "ui-errors.txt"), exception.GetType().Name + "\n" + exception.StackTrace + "\n"); } catch { }
    }
}
