using System.ComponentModel;

namespace SSHExplorer.Core;

public enum MonitorColumnWidthMode { Pixel, Star, Auto }
public sealed class MonitorColumnPreference
{
    public string Key { get; set; } = "";
    public bool Visible { get; set; }
    public bool ExplicitVisibility { get; set; }
    public int Order { get; set; }
    public double Width { get; set; }
    public MonitorColumnWidthMode WidthMode { get; set; }
}
public sealed class MonitorPreferences
{
    public List<MonitorColumnPreference> Columns { get; set; } = [];
    public string? SortColumn { get; set; }
    public ListSortDirection SortDirection { get; set; }
}
