using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using System.Globalization;
using System.Windows.Data;
using Microsoft.Win32;

namespace SSHExplorer.App;

public static class Ui
{
    public static readonly DependencyProperty HasValidationErrorProperty = DependencyProperty.RegisterAttached("HasValidationError", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static void SetHasValidationError(DependencyObject target, bool value) => target.SetValue(HasValidationErrorProperty, value);
    public static bool GetHasValidationError(DependencyObject target) => (bool)target.GetValue(HasValidationErrorProperty);
    public static readonly DependencyProperty ValidationMessageProperty = DependencyProperty.RegisterAttached("ValidationMessage", typeof(string), typeof(Ui), new PropertyMetadata("", (target,args)=>
    {
        var message=args.NewValue as string??"";target.SetCurrentValue(HasValidationErrorProperty,!string.IsNullOrWhiteSpace(message));AutomationProperties.SetHelpText(target,message);
    }));
    public static void SetValidationMessage(DependencyObject target,string value)=>target.SetValue(ValidationMessageProperty,value);
    public static string GetValidationMessage(DependencyObject target)=>(string)target.GetValue(ValidationMessageProperty);
    public const string AddGlyph = "\uE710", BackGlyph = "\uE72B", ForwardGlyph = "\uE72A", UpGlyph = "\uE74A", RefreshGlyph = "\uE72C", HomeGlyph = "\uE80F", FolderGlyph = "\uE8B7", CutGlyph = "\uE8C6", CopyGlyph = "\uE8C8", PasteGlyph = "\uE77F", RenameGlyph = "\uE8AC", DeleteGlyph = "\uE74D", UploadGlyph = "\uE898", DownloadGlyph = "\uE896", SortGlyph = "\uE8CB", ViewGlyph = "\uE8A9", MoreGlyph = "\uE712", SettingsGlyph = "\uE713", CloseGlyph = "\uE711", SearchGlyph = "\uE721", FileGlyph = "\uE8A5", TerminalGlyph = "\uE756", PinGlyph = "\uE718", PropertiesGlyph = "\uE946", UndoGlyph = "\uE7A7", NewTabGlyph = IconCatalog.NewTab;
    public static readonly DependencyProperty IsCurrentFolderProperty = DependencyProperty.RegisterAttached("IsCurrentFolder", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false));
    public static void SetIsCurrentFolder(DependencyObject target, bool value) => target.SetValue(IsCurrentFolderProperty, value);
    public static bool GetIsCurrentFolder(DependencyObject target) => (bool)target.GetValue(IsCurrentFolderProperty);
    public static readonly DependencyProperty IsFileSectionActiveProperty = DependencyProperty.RegisterAttached("IsFileSectionActive", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits));
    public static void SetIsFileSectionActive(DependencyObject target, bool value) => target.SetValue(IsFileSectionActiveProperty, value);
    public static bool GetIsFileSectionActive(DependencyObject target) => (bool)target.GetValue(IsFileSectionActiveProperty);
    public static readonly DependencyProperty IsNavigationActiveProperty = DependencyProperty.RegisterAttached("IsNavigationActive", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false));
    public static void SetIsNavigationActive(DependencyObject target, bool value) => target.SetValue(IsNavigationActiveProperty, value);
    public static bool GetIsNavigationActive(DependencyObject target) => (bool)target.GetValue(IsNavigationActiveProperty);
    public static readonly DependencyProperty TreeDepthProperty = DependencyProperty.RegisterAttached("TreeDepth", typeof(int), typeof(Ui), new FrameworkPropertyMetadata(0));
    public static void SetTreeDepth(DependencyObject target, int value) => target.SetValue(TreeDepthProperty, value);
    public static int GetTreeDepth(DependencyObject target) => (int)target.GetValue(TreeDepthProperty);
    public static readonly DependencyProperty IsDropTargetProperty = DependencyProperty.RegisterAttached("IsDropTarget", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false));
    public static void SetIsDropTarget(DependencyObject target, bool value) => target.SetValue(IsDropTargetProperty, value);
    public static bool GetIsDropTarget(DependencyObject target) => (bool)target.GetValue(IsDropTargetProperty);
    static readonly DependencyProperty FlyoutOwnerProperty = DependencyProperty.RegisterAttached("FlyoutOwner", typeof(FrameworkElement), typeof(Ui));
    public static Button Button(string text, Action action, string? tooltip = null)
    {
        var b = new Button { Content = text, ToolTip = tooltip ?? text };
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
    public static Button FlyoutButton(string glyph, string label, Func<ContextMenu> createMenu, string? tooltip = null)
    {
        Button? button = null;
        button = CommandButton(glyph, label, () => OpenFlyout(button!, createMenu()), tooltip, label.Length > 0);
        if (label.Length == 0) { button.Width = 36; button.Padding = new Thickness(0); }
        button.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Alt))
            { e.Handled = true; OpenFlyout(button, createMenu()); }
        };
        return button;
    }
    // Used by command flyouts and normal context menus. WPF's popup service
    // constrains placement to the monitor work area; no screen pixel offsets.
    public static void ConfigureFlyout(FrameworkElement owner, ContextMenu menu, bool command = false)
    {
        menu.PlacementTarget = owner; menu.Placement = command ? PlacementMode.Bottom : PlacementMode.MousePoint;
        menu.StaysOpen = false; menu.DataContext ??= owner.DataContext;
        if (ReferenceEquals(menu.GetValue(FlyoutOwnerProperty), owner)) return;
        menu.SetValue(FlyoutOwnerProperty, owner);
        menu.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && !menu.Items.OfType<MenuItem>().Any(i => i.IsSubmenuOpen)) { e.Handled = true; menu.IsOpen = false; } };
        RoutedEventHandler unloaded = (_, _) => menu.IsOpen = false;
        menu.Opened += (_, _) => { owner.Unloaded += unloaded; UiMotion.OpenFlyout(menu); };
        menu.Closed += (_, _) => owner.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            owner.Unloaded -= unloaded;
            // Do not steal focus from an async command's dialog or another field.
            var window = Window.GetWindow(owner);
            if (!owner.IsLoaded || !owner.IsEnabled || window?.IsActive != true) return;
            if (Keyboard.FocusedElement is null or MenuItem or ContextMenu) owner.Focus();
        });
    }
    public static void AttachContextMenu(FrameworkElement owner, ContextMenu menu)
    {
        ConfigureFlyout(owner, menu); owner.ContextMenu = menu;
        owner.ContextMenuOpening += (_, e) =>
        {
            menu.PlacementTarget = e.CursorLeft < 0 && owner is System.Windows.Controls.Primitives.Selector selector && selector.SelectedItem is { } selected && selector.ItemContainerGenerator.ContainerFromItem(selected) is FrameworkElement item ? item : owner;
            menu.Placement = e.CursorLeft < 0 ? PlacementMode.Bottom : PlacementMode.MousePoint;
        };
    }
    public static void AttachContextMenu(FrameworkElement owner, Func<ContextMenu> createMenu)
    {
        var menu = new ContextMenu(); AttachContextMenu(owner, menu);
        owner.ContextMenuOpening += (_, _) =>
        {
            var fresh = createMenu(); menu.Items.Clear();
            while (fresh.Items.Count > 0) { var item = fresh.Items[0]; fresh.Items.RemoveAt(0); menu.Items.Add(item); }
        };
    }
    public static void OpenContextMenu(FrameworkElement owner, ContextMenu menu)
    {
        ConfigureFlyout(owner, menu);
        if (owner is System.Windows.Controls.Primitives.Selector selector && selector.SelectedItem is { } selected && selector.ItemContainerGenerator.ContainerFromItem(selected) is FrameworkElement item) menu.PlacementTarget = item;
        menu.Placement = PlacementMode.Bottom; menu.IsOpen = true;
        menu.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { if (menu.IsOpen) menu.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)); });
    }
    public static void OpenFlyout(FrameworkElement owner, ContextMenu menu)
    {
        ConfigureFlyout(owner, menu, true); menu.IsOpen = true;
        menu.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (menu.IsOpen) menu.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        });
    }
    public static async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _ = LocalDiagnostics.RecordAsync("ui.operation.failure", ex); Error(ex.Message); }
    }
    public static TextBlock Text(string text, double size = 14, bool bold = false)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center };
        if (size is 14 or 12 or 18) block.SetResourceReference(TextBlock.FontSizeProperty, size == 12 ? "UICaptionSize" : size == 18 ? "UIHeadingSize" : "UIFontSize");
        return block;
    }
    /// <summary>Command/navigation icon: a Segoe Fluent system glyph or an <see cref="IconCatalog"/> vector key.</summary>
    public static FrameworkElement Icon(string glyph, double size = 18, bool trailingSpace = true)
    {
        var margin = trailingSpace ? new Thickness(0, 0, 9, 0) : new Thickness(0);
        if (IconCatalog.IsVector(glyph))
        {
            var icon = new VectorIcon(glyph, size == 18 ? 18 : size) { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = margin };
            if (size == 18) icon.SetResourceReference(VectorIcon.IconSizeProperty, "IconSize");
            return icon;
        }
        var block = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = size, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = margin };
        if (size == 18) block.SetResourceReference(TextBlock.FontSizeProperty, "IconSize"); return block;
    }
    public static StackPanel Row(params UIElement[] items) { var p = new StackPanel { Orientation = Orientation.Horizontal }; foreach (var x in items) p.Children.Add(x); return p; }
    public static DialogActionPanel Actions(params UIElement[] items) { var p = new DialogActionPanel(); foreach (var item in items) p.Children.Add(item); return p; }
    public static DockPanel DialogSurface(FrameworkElement body, params UIElement[] actions)
    {
        var root=new DockPanel();root.SetResourceReference(Panel.BackgroundProperty,"Paper");
        var footer=new Border{Child=Actions(actions)};footer.SetResourceReference(FrameworkElement.StyleProperty,"DialogFooterStyle");DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        root.Children.Add(new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});return root;
    }
    public static TextBlock DialogHeading(string text) { var heading = Text(text, 18, true); heading.SetResourceReference(FrameworkElement.StyleProperty, "DialogTitleStyle"); return heading; }
    public static MenuItem Menu(string text, Action action, string shortcut = "", string? glyph = null) { var m = new MenuItem { Header = text, InputGestureText = shortcut }; if (glyph is not null) m.Icon = Icon(glyph, 16, false); m.Click += (_, _) => action(); return m; }
    public static MenuItem AsyncMenu(string text, Func<Task> action, string shortcut = "", string? glyph = null) => Menu(text, async () => await Guard(action), shortcut, glyph);
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
        w.SetResourceReference(FrameworkElement.StyleProperty, "DialogWindowStyle");
        var p = new StackPanel { Margin = new Thickness(22) }; p.Children.Add(Text(label));
        var t = new TextBox { Text = value, Margin = new Thickness(0, 12, 0, 16) }; p.Children.Add(t);
        var ok = Button("Готово", () => w.DialogResult = true); ok.IsDefault = true;
        var cancel = Button("Отмена", () => w.DialogResult = false); cancel.IsCancel = true;
        var buttons = Row(ok, cancel); buttons.HorizontalAlignment = HorizontalAlignment.Right; p.Children.Add(buttons); w.Content = p;
        w.Loaded += (_, _) => { t.Focus(); t.SelectAll(); };
        return w.ShowDialog() == true ? t.Text : null;
    }
}

public sealed class NonEmptyTextVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is string text && !string.IsNullOrWhiteSpace(text) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NonEmptyTextBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is string text && !string.IsNullOrWhiteSpace(text);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)=>throw new NotSupportedException();
}

public sealed class TreeIndentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => new Thickness((value is int depth ? Math.Max(0, depth) : 0) * (Application.Current?.TryFindResource("TreeIndent") is double indent ? indent : 16), 0, 0, 0);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
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
        if (Current != mode) SystemFileIconProvider.Invalidate();
        Current = mode;
        bool dark = mode == "Dark" || mode == "System" && Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int x && x == 0;
        var nativeTheme = SystemParameters.HighContrast ? "HC" : dark ? "Dark" : "Light";
        if (fluentTheme != nativeTheme)
        {
            SystemFileIconProvider.Invalidate();
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
            r["NavigationSelected"] = SystemColors.HighlightBrush; r["NavigationSelectedInk"] = SystemColors.HighlightTextBrush; r["ValidationInk"] = SystemColors.WindowTextBrush;
            r["AccentHover"] = SystemColors.HighlightBrush; r["AccentPressed"] = SystemColors.HighlightBrush;
            r["FocusStroke"] = SystemColors.WindowTextBrush; r["SelectedFocusStroke"] = SystemColors.HighlightTextBrush; r["PrimaryFocusStroke"] = SystemColors.HighlightTextBrush;
            r["DangerFill"] = SystemColors.HighlightBrush; r["CaptionHover"] = SystemColors.HighlightBrush; r["CaptionPressed"] = SystemColors.HighlightBrush; r["CaptionInactiveInk"] = SystemColors.GrayTextBrush;
            Changed?.Invoke();
            return;
        }
        Brush B(string s) => (Brush)new BrushConverter().ConvertFromString(s)!;
        r["Paper"] = B(dark ? "#202020" : "#FFFFFF"); r["Surface"] = B(dark ? "#292929" : "#F3F3F3");
        r["NavigationSurface"] = B(dark ? "#252525" : "#FAFAFA"); r["CaptionSurface"] = B(dark ? "#242424" : "#E9ECEC");
        r["NavigationSelected"] = B(dark ? "#353535" : "#EAEAEA"); r["NavigationSelectedInk"] = B(dark ? "#F4F4F4" : "#1A1A1A"); r["ValidationInk"] = B(dark ? "#FFB4AB" : "#B42318");
        r["Ink"] = B(dark ? "#F4F4F4" : "#1A1A1A"); r["Muted"] = B(dark ? "#B7B7B7" : "#626262");
        r["SelectedInk"] = r["Ink"];
        r["Edge"] = B(dark ? "#414141" : "#E4E4E4"); r["Hover"] = B(dark ? "#343434" : "#EFEFEF"); r["Pressed"] = B(dark ? "#3C3C3C" : "#E3E3E3"); r["SelectedInactive"] = B(dark ? "#353B40" : "#E8EBEF"); r["DisabledInk"] = B(dark ? "#777777" : "#A1A1A1");
        var accent = SystemColors.AccentColor; accent.A = 255;
        static Color Mix(Color a, Color b, double amount) => Color.FromRgb((byte)(a.R * amount + b.R * (1 - amount)), (byte)(a.G * amount + b.G * (1 - amount)), (byte)(a.B * amount + b.B * (1 - amount)));
        r["Accent"] = new SolidColorBrush(accent); r["AccentInk"] = accent.R * .299 + accent.G * .587 + accent.B * .114 > 165 ? Brushes.Black : Brushes.White; r["Selected"] = new SolidColorBrush(Mix(accent, dark ? Color.FromRgb(32, 32, 32) : Colors.White, dark ? .34 : .16)); r["DropTarget"] = new SolidColorBrush(Mix(accent, dark ? Color.FromRgb(32, 32, 32) : Colors.White, dark ? .55 : .28));
        r["AccentHover"] = new SolidColorBrush(Mix(accent, Colors.Black, .92)); r["AccentPressed"] = new SolidColorBrush(Mix(accent, Colors.Black, .84));
        r["FocusStroke"] = r["Accent"]; r["SelectedFocusStroke"] = r["Accent"]; r["PrimaryFocusStroke"] = r["AccentInk"];
        // Caption buttons: Windows 11 subtle fills over the caption surface; inactive glyphs dimmed.
        r["CaptionHover"] = new SolidColorBrush(dark ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x10, 0x00, 0x00, 0x00));
        r["CaptionPressed"] = new SolidColorBrush(dark ? Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0A, 0x00, 0x00, 0x00));
        r["CaptionInactiveInk"] = B(dark ? "#7C7C7C" : "#8F8F8F");
        r["DangerFill"] = B(dark ? "#D13438" : "#C42B1C");
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
