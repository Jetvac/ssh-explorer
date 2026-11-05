using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>
/// A log read from the server line by line: the last N lines, optionally followed (journalctl -f / tail -F) until stopped.
/// A filter (text, or /regex/) narrows what is shown without stopping the stream; at most 20 000 lines are kept.
/// </summary>
public sealed class LogView : UserControl, IDisposable
{
    /// <summary>Starts the source: (lines, follow, onLine, token) → exit status when it ends by itself.</summary>
    public delegate Task<int?> LogSource(int lines, bool follow, Action<string> onLine, CancellationToken ct);

    readonly TextBox output = new() { Name = "LogOutput", IsReadOnly = true, IsReadOnlyCaretVisible = true, VerticalContentAlignment = VerticalAlignment.Top, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Padding = new Thickness(12, 8, 12, 8) };
    readonly TextBox filter = new() { Name = "LogFilter", MinWidth = 200, MinHeight = 30 };
    readonly ComboBox count = new() { ItemsSource = new[] { 200, 1000, 5000, 20000 }, SelectedIndex = 0, MinWidth = 90, Name = "LogLines" };
    readonly CheckBox follow = new() { Content = "Следить", Name = "LogFollow", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0), IsChecked = true };
    readonly TextBlock title = Ui.Text("Журнал", 14, true), state = Ui.Text("", 12);
    readonly ConcurrentQueue<string> pending = new();
    readonly DispatcherTimer drain = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly LogBuffer buffer = new();
    LogSource? source; CancellationTokenSource? run; Func<string, bool>? match; long shownDropped; int generation; bool disposed;
    internal string Shown => output.Text;
    internal string StateText => state.Text;
    internal bool Running => run is not null;

    public LogView(bool showTitle = true)
    {
        Name = "LogView";
        var bar = new DockPanel { Margin = new Thickness(16, 8, 16, 4) };
        var restart = Ui.AsyncCommandButton(Ui.RefreshGlyph, "Заново", () => { Restart(); return Task.CompletedTask; }, "Перечитать последние строки");
        var clear = Ui.CommandButton("", "Очистить", () => { buffer.Clear(); shownDropped = 0; Render(); }, "Очистить окно (на сервере ничего не меняется)");
        var tools = Ui.Row(Ui.Text("Строк: ", 12), count, follow, restart, clear); DockPanel.SetDock(tools, Dock.Right); bar.Children.Add(tools);
        var filterHost = new Grid { Margin = new Thickness(showTitle ? 12 : 0, 0, 12, 0) }; filterHost.Children.Add(filter);
        var hint = Ui.Text("Фильтр: текст или /регулярное выражение/", 12); hint.IsHitTestVisible = false; hint.Margin = new Thickness(10, 0, 6, 0); hint.VerticalAlignment = VerticalAlignment.Center; hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); filterHost.Children.Add(hint);
        System.Windows.Automation.AutomationProperties.SetName(filter, "Фильтр строк журнала");
        if (showTitle) { title.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(title, Dock.Left); bar.Children.Add(title); }
        bar.Children.Add(filterHost);
        filter.TextChanged += (_, _) =>
        {
            hint.Visibility = filter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            match = LogFollow.Filter(filter.Text, out var error); Ui.SetHasValidationError(filter, error is not null); filter.ToolTip = error;
            Render();
            if (run is not null) state.Text = (follow.IsChecked == true ? "Слежение · " : "Чтение · ") + Summary();
            else if (buffer.Count > 0) state.Text = Summary();
        };
        count.SelectionChanged += (_, _) => { if (source is not null) Restart(); };
        follow.Click += (_, _) => { if (follow.IsChecked == true) Restart(); else Stop("Слежение остановлено"); };
        output.SetResourceReference(BackgroundProperty, "Surface");
        var border = new Border { Child = output, BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(16, 0, 16, 0) }; border.SetResourceReference(Border.BorderBrushProperty, "Edge");
        state.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); state.Margin = new Thickness(16, 4, 16, 4); state.TextTrimming = TextTrimming.CharacterEllipsis; state.Name = "LogState";
        var root = new DockPanel(); DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar); DockPanel.SetDock(state, Dock.Bottom); root.Children.Add(state); root.Children.Add(border);
        Content = root;
        drain.Tick += (_, _) => Drain();
    }

    /// <summary>Shows a new log (another service or file): the old stream is stopped first.</summary>
    public void Open(string heading, LogSource newSource)
    {
        title.Text = heading; source = newSource; Restart();
    }

    public void Close() { Stop(null); source = null; buffer.Clear(); shownDropped = 0; Render(); state.Text = ""; }

    void Restart()
    {
        if (source is null || disposed) return;
        Stop(null); buffer.Clear(); shownDropped = 0; while (pending.TryDequeue(out _)) { } output.Clear();
        var cts = new CancellationTokenSource(); run = cts; var version = ++generation; var active = source; bool followed = follow.IsChecked == true;
        state.Text = followed ? "Слежение…" : "Чтение…"; drain.Start();
        _ = RunAsync(active, (int)count.SelectedItem, followed, cts, version);
    }

    async Task RunAsync(LogSource active, int lines, bool followed, CancellationTokenSource cts, int version)
    {
        string? end;
        try { var status = await active(lines, followed, pending.Enqueue, cts.Token); end = status is null or 0 ? "Вывод завершён" : $"Команда завершилась с кодом {status}"; }
        catch (OperationCanceledException) { end = null; }
        catch (Exception ex) { end = "Ошибка: " + ErrorMessages.Describe(ex); }
        if (disposed || version != generation) return;
        Drain(); run = null; cts.Dispose(); drain.Stop();
        if (end is not null) state.Text = end + " · " + Summary();
    }

    void Stop(string? reason)
    {
        generation++;
        if (run is { } cts) { run = null; try { cts.Cancel(); } catch (ObjectDisposedException) { } }
        Drain(); drain.Stop();
        if (reason is not null) state.Text = reason + " · " + Summary();
    }

    void Drain()
    {
        if (pending.IsEmpty) return;
        var added = new List<string>();
        while (pending.TryDequeue(out var line)) { buffer.Add(line); added.Add(line); }
        if (buffer.Dropped != shownDropped || added.Count > buffer.Count) { shownDropped = buffer.Dropped; Render(); }
        else
        {
            var visible = match is null ? added : added.Where(match).ToList();
            if (visible.Count > 0) { bool atEnd = AtEnd(); output.AppendText((output.Text.Length > 0 ? "\n" : "") + string.Join("\n", visible)); if (atEnd) output.ScrollToEnd(); }
        }
        if (run is not null) state.Text = (follow.IsChecked == true ? "Слежение · " : "Чтение · ") + Summary();
    }

    bool AtEnd() => output.ExtentHeight <= 0 || output.VerticalOffset + output.ViewportHeight >= output.ExtentHeight - 4;

    void Render()
    {
        bool atEnd = AtEnd();
        output.Text = string.Join("\n", buffer.Matching(match));
        if (atEnd) output.ScrollToEnd();
    }

    string Summary()
    {
        var shown = match is null ? buffer.Count : buffer.Lines.Count(match);
        return (match is null ? $"строк: {buffer.Count}" : $"показано {shown} из {buffer.Count}") + (buffer.Dropped > 0 ? $" · старые строки отброшены: {buffer.Dropped}" : "");
    }

    public void Dispose() { if (disposed) return; Stop(null); disposed = true; drain.Stop(); }
}
