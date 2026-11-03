using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace SSHExplorer.App;

internal sealed class FileContextCommandStrip : MenuItem
{
    readonly StackPanel row = new() { Orientation = Orientation.Horizontal };
    readonly List<Button> buttons = [];
    internal FileContextCommandStrip()
    {
        Header = row; StaysOpenOnClick = true; Padding = new Thickness(2);
        AutomationProperties.SetName(this, "Основные действия с файлами");
        GotKeyboardFocus += (_, e) => { if (ReferenceEquals(e.OriginalSource, this)) buttons.FirstOrDefault(b => b.IsEnabled)?.Focus(); };
        PreviewKeyDown += (_, e) =>
        {
            var current = buttons.FindIndex(b => b.IsKeyboardFocusWithin);
            if (e.Key is Key.Left or Key.Right or Key.Home or Key.End)
            {
                var enabled = buttons.Where(b => b.IsEnabled).ToList();
                if (enabled.Count == 0) return;
                var at = enabled.IndexOf(current >= 0 ? buttons[current] : enabled[0]);
                var next = e.Key == Key.Home ? 0 : e.Key == Key.End ? enabled.Count - 1 : (at + (e.Key == Key.Right ? 1 : -1) + enabled.Count) % enabled.Count;
                enabled[next].Focus(); e.Handled = true;
            }
            else if (e.Key is Key.Down or Key.Up)
            {
                if (Parent is ContextMenu menu)
                {
                    var index = menu.Items.IndexOf(this); var step = e.Key == Key.Down ? 1 : -1;
                    for (var i = index + step; i >= 0 && i < menu.Items.Count; i += step)
                        if (menu.Items[i] is MenuItem { IsEnabled: true, Visibility: Visibility.Visible } item) { item.Focus(); e.Handled = true; break; }
                }
            }
        };
    }
    internal void Add(string glyph, string name, Func<Task> action, bool enabled)
    {
        var button = Ui.AsyncIconButton(glyph, async () =>
        {
            if (Parent is ContextMenu menu) menu.IsOpen = false;
            await action();
        }, name);
        button.IsEnabled = enabled; button.Margin = new Thickness(1, 0, 1, 0);
        buttons.Add(button); row.Children.Add(button);
    }
}
