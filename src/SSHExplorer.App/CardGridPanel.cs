using System.Windows;
using System.Windows.Controls;

namespace SSHExplorer.App;

/// <summary>
/// Responsive card grid: as many equal columns of at least <see cref="MinColumnWidth"/> as fit,
/// cards stretch to the column width, rows take the tallest card. <see cref="Grid.ColumnSpanProperty"/>
/// lets a card (a table) span several columns; it is clamped to the columns available.
/// Replaces fixed-width cards in a WrapPanel, which left an empty gutter and squeezed values.
/// </summary>
public sealed class CardGridPanel : Panel
{
    public double MinColumnWidth { get; set; } = 320;
    public double Gap { get; set; } = 12;

    int columns = 1;
    double columnWidth;
    readonly List<(UIElement Child, int Column, int Span, int Row)> slots = [];
    readonly List<double> rowHeights = [];

    public int Columns => columns;

    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsFinite(available.Width) ? available.Width : MinColumnWidth * 3 + Gap * 2;
        columns = Math.Max(1, (int)Math.Floor((width + Gap) / (MinColumnWidth + Gap)));
        columnWidth = Math.Max(0, (width - Gap * (columns - 1)) / columns);
        slots.Clear(); rowHeights.Clear();
        int row = 0, column = 0; double rowHeight = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            int span = Math.Clamp(Grid.GetColumnSpan(child), 1, columns);
            if (column + span > columns) { rowHeights.Add(rowHeight); row++; column = 0; rowHeight = 0; }
            child.Measure(new Size(SpanWidth(span), double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            slots.Add((child, column, span, row)); column += span;
        }
        if (slots.Count > 0) rowHeights.Add(rowHeight);
        double height = rowHeights.Sum() + Gap * Math.Max(0, rowHeights.Count - 1);
        return new Size(double.IsFinite(available.Width) ? width : SpanWidth(columns), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // Arrange with the final width (a scrollbar may have appeared after measure).
        if (Math.Abs(finalSize.Width - (columnWidth * columns + Gap * (columns - 1))) > .5)
            columnWidth = Math.Max(0, (finalSize.Width - Gap * (columns - 1)) / columns);
        double y = 0; int currentRow = 0;
        foreach (var (child, column, span, row) in slots)
        {
            while (currentRow < row) { y += rowHeights[currentRow] + Gap; currentRow++; }
            child.Arrange(new Rect(column * (columnWidth + Gap), y, SpanWidth(span), rowHeights[row]));
        }
        return finalSize;
    }

    double SpanWidth(int span) => columnWidth * span + Gap * (span - 1);
}
