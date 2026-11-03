using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace SSHExplorer.App;

/// <summary>A single command row. Lower priorities move into the same keyboard-accessible flyout.</summary>
public sealed class ResponsiveCommandBar : Panel
{
    readonly List<Entry> entries = [];
    readonly Button more;
    readonly Func<ContextMenu>? additionalMenu;
    public ResponsiveCommandBar(Func<ContextMenu>? additionalMenu = null)
    {
        this.additionalMenu = additionalMenu;
        more = Ui.FlyoutButton(Ui.MoreGlyph, "", CreateOverflowMenu, "Другие команды");
        more.Name = "CommandOverflowButton";
        Children.Add(more);
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Continue);
    }
    /// <param name="priority">Higher values stay visible longer. Ties keep earlier commands.</param>
    /// <param name="menuItem">Called only when opened; use current availability and the same action.</param>
    public void Add(string key, FrameworkElement command, Func<MenuItem> menuItem, int priority = 0)
    {
        entries.Add(new(key, command, menuItem, priority));
        Children.Insert(Children.Count - 1, command);
        InvalidateMeasure();
    }
    public ContextMenu CreateOverflowMenu()
    {
        var menu = new ContextMenu { DataContext = DataContext };
        foreach (var entry in entries.Where(e => !e.OnBar))
        {
            var item = entry.MenuItem();
            // The command's enabled binding is authoritative even if the factory omits one.
            item.IsEnabled = item.IsEnabled && entry.Command.IsEnabled;
            menu.Items.Add(item);
        }
        if (additionalMenu?.Invoke() is { } additional)
        {
            if (menu.Items.Count > 0 && additional.Items.Count > 0) menu.Items.Add(new Separator());
            while (additional.Items.Count > 0) { var item = additional.Items[0]; additional.Items.RemoveAt(0); menu.Items.Add(item); }
        }
        return menu;
    }
    internal IReadOnlyList<string> VisibleCommandKeys => entries.Where(e => e.OnBar).Select(e => e.Key).ToArray();
    internal IReadOnlyList<string> OverflowCommandKeys => entries.Where(e => !e.OnBar).Select(e => e.Key).ToArray();
    protected override Size MeasureOverride(Size available)
    {
        more.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        foreach (var entry in entries)
        {
            // Hidden still measures, while collapsed would erase the width needed for reflow.
            entry.Command.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }
        double width = double.IsInfinity(available.Width) ? entries.Sum(e => e.Command.DesiredSize.Width) + more.DesiredSize.Width : Math.Max(0, available.Width);
        bool overflow = additionalMenu is not null || entries.Sum(e => e.Command.DesiredSize.Width) > width;
        double remaining = Math.Max(0, width - (overflow ? more.DesiredSize.Width : 0));
        foreach (var entry in entries.OrderByDescending(e => e.Priority).ThenBy(e => entries.IndexOf(e)))
        {
            entry.OnBar = entry.Command.DesiredSize.Width <= remaining;
            if (entry.OnBar) remaining -= entry.Command.DesiredSize.Width;
        }
        more.Visibility = overflow ? Visibility.Visible : Visibility.Hidden;
        KeyboardNavigation.SetIsTabStop(more, overflow);
        foreach (var entry in entries)
        {
            if (!entry.OnBar && entry.Command.IsKeyboardFocusWithin) more.Focus();
            entry.Command.Visibility = entry.OnBar ? Visibility.Visible : Visibility.Hidden;
            KeyboardNavigation.SetIsTabStop(entry.Command, entry.OnBar);
        }
        return new Size(Math.Min(width, entries.Where(e => e.OnBar).Sum(e => e.Command.DesiredSize.Width) + (overflow ? more.DesiredSize.Width : 0)), Math.Max(more.DesiredSize.Height, entries.Count == 0 ? 0 : entries.Max(e => e.Command.DesiredSize.Height)));
    }
    protected override Size ArrangeOverride(Size final)
    {
        double x = 0;
        foreach (var entry in entries)
        {
            if (entry.OnBar) { entry.Command.Arrange(new Rect(x, 0, entry.Command.DesiredSize.Width, final.Height)); x += entry.Command.DesiredSize.Width; }
            else entry.Command.Arrange(new Rect(0, 0, 0, 0));
        }
        more.Arrange(new Rect(x, 0, more.Visibility == Visibility.Visible ? more.DesiredSize.Width : 0, final.Height));
        return final;
    }
    sealed class Entry(string key, FrameworkElement command, Func<MenuItem> menuItem, int priority)
    {
        public string Key { get; } = key;
        public FrameworkElement Command { get; } = command;
        public Func<MenuItem> MenuItem { get; } = menuItem;
        public int Priority { get; } = priority;
        public bool OnBar { get; set; } = true;
    }
}
