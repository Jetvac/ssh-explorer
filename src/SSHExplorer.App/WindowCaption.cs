using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace SSHExplorer.App;

/// <summary>
/// Minimize / Maximize-Restore / Close drawn on the same surface as the session tabs.
/// </summary>
/// <remarks>
/// beta.5 extended DWM glass under the tab row and let DWM draw its own caption buttons; the WPF
/// strip stopped 138 DIP before the right edge, so the area under the buttons used a different
/// (DWM) colour in light/dark/inactive states. The buttons are now WPF elements on the caption
/// surface. System behaviour is kept explicitly:
/// <list type="bullet">
/// <item>Maximize answers WM_NCHITTEST with HTMAXBUTTON, so Windows 11 shows Snap Layouts on hover;
/// its hover/pressed visuals and click are driven from the non-client messages it then receives.</item>
/// <item>Free caption space stays HTCAPTION (WindowChrome): drag, double-click maximize, Aero Snap,
/// right-click system menu; Alt+Space still opens the system menu (WS_SYSMENU is untouched).</item>
/// <item>Maximized windows are inset by the hidden resize frame so the row is not clipped.</item>
/// </list>
/// If the hook cannot run first, the maximize element still works as an ordinary client button.
/// </remarks>
public sealed class WindowCaption : StackPanel
{
    public const double ButtonWidth = 46, ButtonHeight = SessionHeaderPanel.RowHeight;
    const int WM_NCHITTEST = 0x0084, WM_NCMOUSEMOVE = 0x00A0, WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONUP = 0x00A2, WM_NCLBUTTONDBLCLK = 0x00A3, WM_NCMOUSELEAVE = 0x02A2, WM_MOUSEMOVE = 0x0200;
    const int HTMAXBUTTON = 9;
    readonly Window window;
    readonly CaptionButton minimize, maximize, close;
    bool maxHot, maxPressed, hooked;

    public CaptionButton MinimizeButton => minimize;
    public CaptionButton MaximizeButton => maximize;
    public CaptionButton CloseButton => close;

    public WindowCaption(Window window)
    {
        this.window = window;
        Orientation = Orientation.Horizontal; VerticalAlignment = VerticalAlignment.Top; HorizontalAlignment = HorizontalAlignment.Right;
        minimize = new CaptionButton("", "Свернуть", false); minimize.Name = "CaptionMinimize";
        maximize = new CaptionButton("", "Развернуть", false); maximize.Name = "CaptionMaximize";
        close = new CaptionButton("", "Закрыть", true); close.Name = "CaptionClose";
        minimize.Click += (_, _) => SystemCommands.MinimizeWindow(window);
        maximize.Click += (_, _) => ToggleMaximize();
        close.Click += (_, _) => SystemCommands.CloseWindow(window);
        foreach (var button in new[] { minimize, maximize, close }) { WindowChrome.SetIsHitTestVisibleInChrome(button, true); Children.Add(button); }
        window.StateChanged += (_, _) => SyncState();
        window.Activated += (_, _) => SyncState();
        window.Deactivated += (_, _) => SyncState();
        window.SourceInitialized += (_, _) => Hook();
        window.DpiChanged += (_, _) => SyncState();
        Theme.Changed += SyncState;
        Unloaded += (_, _) => Theme.Changed -= SyncState;
        Loaded += (_, _) => { Hook(); SyncState(); };
        SyncState();
    }

    /// <summary>Applies the shared window chrome used with this caption.</summary>
    public static void ApplyChrome(Window window) => WindowChrome.SetWindowChrome(window, new WindowChrome
    {
        CaptionHeight = SessionHeaderPanel.RowHeight,
        // One DWM pixel keeps the system shadow and Windows 11 rounded corners; the client
        // surface paints over it, so no glass is visible.
        GlassFrameThickness = new Thickness(0, 0, 0, 1),
        ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness,
        CornerRadius = new CornerRadius(0),
        UseAeroCaptionButtons = false,
        NonClientFrameEdges = NonClientFrameEdges.None,
    });

    public void ToggleMaximize()
    {
        if (window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(window); else SystemCommands.MaximizeWindow(window);
    }

    /// <summary>Inset that compensates the off-screen resize frame of a maximized custom-chrome window.</summary>
    public static Thickness MaximizedInset(Visual visual)
    {
        var dpi = VisualTreeHelper.GetDpi(visual);
        uint raw = (uint)Math.Round(96 * dpi.DpiScaleX);
        int x, y;
        try { x = GetSystemMetricsForDpi(32, raw) + GetSystemMetricsForDpi(92, raw); y = GetSystemMetricsForDpi(33, raw) + GetSystemMetricsForDpi(92, raw); }
        catch (EntryPointNotFoundException) { x = GetSystemMetrics(32) + GetSystemMetrics(92); y = GetSystemMetrics(33) + GetSystemMetrics(92); }
        return new Thickness(x / dpi.DpiScaleX, y / dpi.DpiScaleY, x / dpi.DpiScaleX, y / dpi.DpiScaleY);
    }

    void SyncState()
    {
        bool maximized = window.WindowState == WindowState.Maximized;
        maximize.Glyph = maximized ? "" : "";
        var label = maximized ? "Восстановить" : "Развернуть";
        maximize.ToolTip = label; AutomationProperties.SetName(maximize, label);
        if (window.Content is FrameworkElement root) root.Margin = maximized ? MaximizedInset(window) : new Thickness(0);
        foreach (var button in new[] { minimize, maximize, close }) button.WindowActive = window.IsActive;
        maximize.SimulatedHover = maxHot; maximize.SimulatedPressed = maxPressed;
        foreach (var button in new[] { minimize, maximize, close }) button.UpdateVisual();
    }

    void Hook()
    {
        if (hooked) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || HwndSource.FromHwnd(handle) is not HwndSource source) return;
        // Added after WindowChrome attached its own hook; HwndSource calls later hooks first.
        source.AddHook(WndProc);
        hooked = true;
    }

    bool IsOverMaximize(IntPtr lParam)
    {
        if (!maximize.IsVisible || PresentationSource.FromVisual(maximize) is null) return false;
        long value = lParam.ToInt64();
        var screen = new Point((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
        try
        {
            var local = maximize.PointFromScreen(screen);
            return local.X >= 0 && local.Y >= 0 && local.X < maximize.ActualWidth && local.Y < maximize.ActualHeight;
        }
        catch (InvalidOperationException) { return false; }
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                if (IsOverMaximize(lParam))
                {
                    handled = true;
                    if (!maxHot) { maxHot = true; SyncState(); }
                    return new IntPtr(HTMAXBUTTON);
                }
                if (maxHot || maxPressed) { maxHot = false; maxPressed = false; SyncState(); }
                break;
            case WM_NCLBUTTONDOWN when wParam.ToInt64() == HTMAXBUTTON:
                maxPressed = true; SyncState(); handled = true; return IntPtr.Zero;
            case WM_NCLBUTTONUP when wParam.ToInt64() == HTMAXBUTTON:
                bool click = maxPressed; maxPressed = false; SyncState(); handled = true;
                if (click) Dispatcher.BeginInvoke(ToggleMaximize);
                return IntPtr.Zero;
            case WM_NCLBUTTONDBLCLK when wParam.ToInt64() == HTMAXBUTTON:
                handled = true; return IntPtr.Zero;
            case WM_NCMOUSEMOVE when wParam.ToInt64() != HTMAXBUTTON:
            case WM_NCMOUSELEAVE:
            case WM_MOUSEMOVE:
                if (maxHot || maxPressed) { maxHot = false; maxPressed = false; SyncState(); }
                break;
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
}

/// <summary>A caption button with Windows 11 state colours (Close turns red on hover).</summary>
public sealed class CaptionButton : Button
{
    readonly TextBlock glyph;
    readonly bool isClose;
    public bool WindowActive { get; set; } = true;
    public bool SimulatedHover { get; set; }
    public bool SimulatedPressed { get; set; }
    public string Glyph { get => glyph.Text; set => glyph.Text = value; }

    public CaptionButton(string glyphText, string name, bool isClose)
    {
        this.isClose = isClose;
        glyph = new TextBlock { Text = glyphText, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var chrome = new FrameworkElementFactory(typeof(Border));
        chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        chrome.SetValue(Border.SnapsToDevicePixelsProperty, true);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        chrome.AppendChild(presenter);
        Template = new ControlTemplate(typeof(Button)) { VisualTree = chrome };
        Content = glyph; Width = WindowCaption.ButtonWidth; Height = WindowCaption.ButtonHeight;
        Focusable = false; IsTabStop = false; ToolTip = name; Background = Brushes.Transparent;
        AutomationProperties.SetName(this, name);
        MouseEnter += (_, _) => UpdateVisual(); MouseLeave += (_, _) => UpdateVisual();
        PreviewMouseLeftButtonDown += (_, _) => Dispatcher.BeginInvoke(UpdateVisual);
        PreviewMouseLeftButtonUp += (_, _) => Dispatcher.BeginInvoke(UpdateVisual);
        IsEnabledChanged += (_, _) => UpdateVisual();
    }

    public void UpdateVisual()
    {
        bool hover = IsMouseOver || SimulatedHover, pressed = IsPressed || SimulatedPressed;
        bool contrast = SystemParameters.HighContrast;
        Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
        var ink = Res("Ink", Brushes.Black);
        var inactive = Res("CaptionInactiveInk", Res("Muted", Brushes.Gray));
        if (isClose && (hover || pressed) && !contrast)
        {
            Background = new SolidColorBrush(pressed ? Color.FromRgb(0xC8, 0x3C, 0x31) : Color.FromRgb(0xC4, 0x2B, 0x1C));
            glyph.Foreground = Brushes.White;
            return;
        }
        if (contrast)
        {
            Background = hover || pressed ? SystemColors.HighlightBrush : Brushes.Transparent;
            glyph.Foreground = hover || pressed ? SystemColors.HighlightTextBrush : SystemColors.ControlTextBrush;
            return;
        }
        Background = pressed ? Res("CaptionPressed", Res("Pressed", Brushes.LightGray)) : hover ? Res("CaptionHover", Res("Hover", Brushes.Gainsboro)) : Brushes.Transparent;
        glyph.Foreground = !IsEnabled ? Res("DisabledInk", Brushes.Gray) : WindowActive || hover ? ink : inactive;
    }

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new System.Windows.Automation.Peers.ButtonAutomationPeer(this);
}
