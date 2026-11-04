using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using SSHExplorer.App.ViewModels;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>
/// Transfer queue: compact main columns (object, direction, state, progress, speed, remaining),
/// details of the selected transfer below, actions enabled by the selected transfer's state, and a
/// short empty state. Refresh updates rows in place, so selection, sort and scroll survive.
/// </summary>
public sealed class TransfersWindow : Window
{
    readonly DataGrid grid = new() { Name = "TransferTable" };
    readonly TransferListViewModel transfers = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly Button cancel, retry, clear;
    readonly TextBlock status = Ui.Text("", 12);
    readonly StackPanel empty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 420, Margin = new Thickness(24) };
    readonly Border details = new() { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(16, 10, 16, 12) };
    readonly Grid detailGrid = new();
    internal TransferListViewModel Model => transfers;
    internal DataGrid Table => grid;
    internal Button CancelButton => cancel;
    internal Button RetryButton => retry;
    internal FrameworkElement EmptyState => empty;
    internal FrameworkElement Details => details;

    public TransfersWindow()
    {
        Style = (Style)FindResource(typeof(Window));
        Title = "Передачи — SSH Explorer"; Width = 980; Height = 480; MinWidth = 620; MinHeight = 320; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AppIcon.Apply(this);
        var root = new DockPanel(); root.SetResourceReference(Panel.BackgroundProperty, "Paper");

        cancel = Ui.CommandButton(Ui.CloseGlyph, "Отменить", () => { if (Selected is { Job.CanCancel: true } row) { row.Job.Cancel(); RefreshRows(); } }, "Отменить выбранную передачу");
        retry = Ui.AsyncCommandButton(Ui.RefreshGlyph, "Повторить", async () => { if (Selected is { Job.CanRetry: true } row) { var pending = row.Job.RetryAsync(); RefreshRows(); await pending; RefreshRows(); } }, "Повторить выбранную передачу");
        clear = Ui.CommandButton(Ui.DeleteGlyph, "Убрать завершённые", () => { App.Transfers.RemoveFinished(); RefreshRows(); }, "Убрать из списка завершённые и отменённые передачи");
        var title = Ui.Text("Передачи", 18, true); title.Margin = new Thickness(4, 0, 12, 0);
        var header = new Border { Child = Ui.Row(title, Ui.Line(), cancel, retry, clear), Padding = new Thickness(12, 6, 12, 6) };
        header.SetResourceReference(StyleProperty, "CommandBarStyle"); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);

        status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var footer = new Border { Child = status }; footer.SetResourceReference(StyleProperty, "StatusBarStyle"); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

        details.SetResourceReference(Border.BackgroundProperty, "Surface"); details.SetResourceReference(Border.BorderBrushProperty, "Edge");
        detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); detailGrid.ColumnDefinitions.Add(new ColumnDefinition());
        details.Child = detailGrid; DockPanel.SetDock(details, Dock.Bottom); root.Children.Add(details);

        grid.SetResourceReference(StyleProperty, "TransferTableStyle");
        grid.SelectionMode = DataGridSelectionMode.Single; grid.IsReadOnly = true; grid.AutoGenerateColumns = false;
        grid.Columns.Add(new DataGridTemplateColumn { Header = "Объект", CellTemplate = NameTemplate(), SortMemberPath = "Name", Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 180 });
        MainWindow.AddColumn(grid, "Направление", "Direction", 200);
        grid.Columns.Add(new DataGridTemplateColumn { Header = "Состояние", CellTemplate = StateTemplate(), SortMemberPath = "State", Width = 120, MinWidth = 100 });
        // The bar must fill the cell: with the default Left content alignment the cell shrank to the bar's
        // desired width, so a running transfer showed a dot instead of its progress.
        var progressCell = new Style(typeof(DataGridCell), (Style)FindResource(typeof(DataGridCell))); progressCell.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        grid.Columns.Add(new DataGridTemplateColumn { Header = "Прогресс", CellTemplate = ProgressTemplate(), CellStyle = progressCell, SortMemberPath = "Progress", Width = 150, MinWidth = 110 });
        MainWindow.AddColumn(grid, "Скорость", "Speed", 100); MainWindow.AddColumn(grid, "Осталось", "Remaining", 100);
        grid.ItemsSource = transfers.Rows;
        TableSorting.Attach(grid, "transfers", [
            new("Name", r => ((TransferRow)r).Name, true), new("Direction", r => ((TransferRow)r).Direction, true),
            new("State", r => ((TransferRow)r).StateRank), new("Progress", r => ((TransferRow)r).ProgressKey),
            new("Speed", r => ((TransferRow)r).SpeedKey), new("Remaining", r => ((TransferRow)r).RemainingKey)], tieBreaker: new("Name", r => ((TransferRow)r).Name, true));
        grid.SelectionChanged += (_, _) => UpdateSelection();

        var emptyTitle = Ui.Text("Передач нет", 18, true); emptyTitle.HorizontalAlignment = HorizontalAlignment.Center; empty.Children.Add(emptyTitle);
        var emptyText = Ui.Text("Здесь появятся загрузки на сервер, скачивания и копирование между серверами. Начните передачу перетаскиванием или командами «Загрузить» и «Скачать» в файлах сервера."); emptyText.TextWrapping = TextWrapping.Wrap; emptyText.TextAlignment = TextAlignment.Center; emptyText.Margin = new Thickness(0, 8, 0, 0); emptyText.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); empty.Children.Add(emptyText);
        AutomationProperties.SetName(empty, "Очередь передач пуста");
        var body = new Grid(); body.Children.Add(grid); body.Children.Add(empty); root.Children.Add(body);
        Content = root;
        timer.Tick += (_, _) => RefreshRows(); RefreshRows(); timer.Start(); Closed += (_, _) => timer.Stop();
    }

    TransferRow? Selected => grid.SelectedItem as TransferRow;

    internal void RefreshRows()
    {
        transfers.Refresh(App.Transfers.Jobs);
        bool none = transfers.Rows.Count == 0;
        empty.Visibility = none ? Visibility.Visible : Visibility.Collapsed; grid.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
        // Live metric sort: re-sort only when the visible order is no longer correct.
        if (TableSorting.CurrentKey(grid) is not null && CollectionViewSource.GetDefaultView(grid.ItemsSource) is ListCollectionView view && view.CustomSort is { } comparer)
        {
            var visible = view.Cast<object>().ToArray();
            for (int i = 1; i < visible.Length; i++) if (comparer.Compare(visible[i - 1], visible[i]) > 0) { view.Refresh(); break; }
        }
        int active = transfers.Active, failed = transfers.Failed;
        status.Text = none ? "Очередь пуста" : $"Всего: {transfers.Rows.Count} · выполняется или ожидает: {active}" + (failed > 0 ? $" · с ошибкой: {failed}" : "") + " · скорость — средняя за попытку";
        UpdateSelection();
    }

    void UpdateSelection()
    {
        var row = Selected;
        cancel.IsEnabled = row?.Job.CanCancel == true;
        retry.IsEnabled = row?.Job.CanRetry == true;
        clear.IsEnabled = transfers.Rows.Any(r => r.Job.State is TransferState.Completed or TransferState.Cancelled);
        cancel.ToolTip = row is null ? "Выберите передачу" : row.Job.CanCancel ? "Отменить «" + row.Name + "»" : "Передача уже завершена";
        retry.ToolTip = row is null ? "Выберите передачу" : row.Job.CanRetry ? "Повторить «" + row.Name + "»" : "Повтор доступен после ошибки или отмены";
        details.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        if (row is null) return;
        detailGrid.Children.Clear(); detailGrid.RowDefinitions.Clear();
        void Line(string label, string value)
        {
            int index = detailGrid.RowDefinitions.Count; detailGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var caption = Ui.Text(label, 12); caption.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); caption.Margin = new Thickness(0, 2, 16, 2); Grid.SetRow(caption, index); detailGrid.Children.Add(caption);
            var text = new TextBox { Text = value, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(0), Margin = new Thickness(0, 2, 0, 2) };
            AutomationProperties.SetName(text, label); Grid.SetRow(text, index); Grid.SetColumn(text, 1); detailGrid.Children.Add(text);
        }
        Line("Откуда", row.Job.SourceServer + ": " + row.Source);
        Line("Куда", row.Job.DestinationServer + ": " + row.Destination);
        Line("Передано", row.Transferred + " из " + row.Total + (row.Job.Attempt > 1 ? $" · попытка {row.Job.Attempt}" : ""));
        Line(row.Job.State == TransferState.Failed ? "Ошибка" : "Результат", row.Result);
    }

    static DataTemplate NameTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(DockPanel));
        var icon = new FrameworkElementFactory(typeof(ContentPresenter));
        icon.SetBinding(ContentPresenter.ContentProperty, new Binding(nameof(TransferRow.DirectionGlyph)) { Converter = new GlyphIconConverter() });
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0)); icon.SetValue(DockPanel.DockProperty, Dock.Left);
        panel.AppendChild(icon);
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(TransferRow.Name))); text.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(TransferRow.Name)));
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        panel.AppendChild(text);
        return new DataTemplate { VisualTree = panel };
    }

    static DataTemplate StateTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(TransferRow.State)));
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var style = new Style(typeof(TextBlock));
        var failed = new DataTrigger { Binding = new Binding(nameof(TransferRow.StateKind)), Value = nameof(TransferState.Failed) };
        failed.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("ValidationInk"))); failed.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
        var done = new DataTrigger { Binding = new Binding(nameof(TransferRow.StateKind)), Value = nameof(TransferState.Completed) };
        done.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("Muted")));
        style.Triggers.Add(failed); style.Triggers.Add(done);
        text.SetValue(FrameworkElement.StyleProperty, style);
        return new DataTemplate { VisualTree = text };
    }

    static DataTemplate ProgressTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(DockPanel));
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(TransferRow.Progress)));
        label.SetValue(DockPanel.DockProperty, Dock.Right); label.SetValue(FrameworkElement.MinWidthProperty, 52d); label.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Right);
        label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); label.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0));
        panel.AppendChild(label);
        var bar = new FrameworkElementFactory(typeof(ProgressBar));
        bar.SetBinding(System.Windows.Controls.Primitives.RangeBase.ValueProperty, new Binding(nameof(TransferRow.ProgressValue)) { Mode = BindingMode.OneWay });
        bar.SetBinding(ProgressBar.IsIndeterminateProperty, new Binding(nameof(TransferRow.ProgressKnown)) { Converter = new InverseRunningConverter() });
        bar.SetValue(FrameworkElement.HeightProperty, 4d); bar.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); bar.SetValue(System.Windows.Controls.Primitives.RangeBase.MaximumProperty, 100d);
        panel.AppendChild(bar);
        return new DataTemplate { VisualTree = panel };
    }

    sealed class GlyphIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => Ui.Icon(value as string ?? Ui.FileGlyph, 16, false);
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }

    // Indeterminate only while a running transfer has no known total; never for finished rows.
    sealed class InverseRunningConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is bool known && !known && UiMotion.Enabled;
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }
}
