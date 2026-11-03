using System.Windows;

namespace SSHExplorer.App;

internal static class FileSelectionGeometry
{
    // Only model indices are visited. Selection never realizes off-screen WPF containers.
    internal static IEnumerable<int> Intersecting(Rect rectangle, int count, int columns, double width, double height)
    {
        if (count <= 0 || columns < 1 || width <= 0 || height <= 0) yield break;
        var firstRow = Math.Max(0, (int)Math.Floor(rectangle.Top / height));
        var lastRow = Math.Min((count - 1) / columns, (int)Math.Floor(rectangle.Bottom / height));
        for (var row = firstRow; row <= lastRow; row++)
            for (var column = 0; column < columns; column++)
            {
                var index = row * columns + column;
                if (index >= count) break;
                if (rectangle.IntersectsWith(new Rect(column * width, row * height, width, height))) yield return index;
            }
    }
}
