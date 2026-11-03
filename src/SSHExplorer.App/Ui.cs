using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Automation;
using Microsoft.Win32;

namespace SSHExplorer.App;

public static class Ui
{
    public static Button Button(string text, Action action, string? tooltip = null)
    {
        var b = new Button { Content = text, ToolTip = tooltip ?? text };
        AutomationProperties.SetName(b, tooltip ?? text);
        b.Click += (_, _) => action();
        return b;
    }
    public static Button AsyncButton(string text, Func<Task> action, string? tooltip = null) => Button(text, async () => await Guard(action), tooltip);
    public static async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _ = LocalDiagnostics.RecordAsync("ui.operation.failure", ex); Error(ex.Message); }
    }
    public static TextBlock Text(string text, double size = 14, bool bold = false) => new() { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center };
    public static TextBlock Icon(string glyph, double size = 18) => new() { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = size, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0) };
    public static StackPanel Row(params UIElement[] items) { var p = new StackPanel { Orientation = Orientation.Horizontal }; foreach (var x in items) p.Children.Add(x); return p; }
    public static MenuItem Menu(string text, Action action, string shortcut = "") { var m = new MenuItem { Header = text, InputGestureText = shortcut }; m.Click += (_, _) => action(); return m; }
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
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    static bool darkFrame;
    public static string Current { get; private set; } = "System";
    public static void ApplyWindowFrame(Window window)
    {
        if (SystemParameters.HighContrast) return;
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        int value = darkFrame ? 1 : 0;
        DwmSetWindowAttribute(handle, 20, ref value, sizeof(int));
    }
    public static void Apply(string mode)
    {
        Current = mode;
        bool dark = mode == "Dark" || mode == "System" && Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int x && x == 0;
        darkFrame = dark;
        foreach (Window window in Application.Current.Windows) ApplyWindowFrame(window);
        var r = Application.Current.Resources;
        if (SystemParameters.HighContrast)
        {
            r["Paper"] = SystemColors.WindowBrush; r["Surface"] = SystemColors.ControlBrush; r["Ink"] = SystemColors.WindowTextBrush; r["SelectedInk"] = SystemColors.HighlightTextBrush; r["Muted"] = SystemColors.GrayTextBrush; r["Edge"] = SystemColors.WindowTextBrush; r["Hover"] = SystemColors.ControlBrush; r["Selected"] = SystemColors.HighlightBrush; r["Accent"] = SystemColors.HighlightBrush;
            return;
        }
        Brush B(string s) => (Brush)new BrushConverter().ConvertFromString(s)!;
        r["Paper"] = B(dark ? "#202020" : "#FFFFFF"); r["Surface"] = B(dark ? "#292929" : "#F3F3F3");
        r["Ink"] = B(dark ? "#F4F4F4" : "#1A1A1A"); r["Muted"] = B(dark ? "#B7B7B7" : "#626262");
        r["SelectedInk"] = r["Ink"];
        r["Edge"] = B(dark ? "#414141" : "#E3E3E3"); r["Hover"] = B(dark ? "#343434" : "#F3F7FB");
        r["Selected"] = B(dark ? "#174568" : "#D9EEFC"); r["Accent"] = B(dark ? "#60CDFF" : "#0067C0");
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
