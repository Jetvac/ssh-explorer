using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SSHExplorer.App.ViewModels;

namespace SSHExplorer.App;

/// <summary>A typed sort key for one table column. Null means "unknown" and sorts last in both directions.</summary>
public sealed record SortColumn(string Key, Func<object, IComparable?> Value, bool Natural = false);

/// <summary>
/// Header-click sorting for tables that have no domain-specific sorter (Home, network, disk I/O,
/// storage, transfers, documents, server panel). Rules shared with the file and process tables:
/// typed values (bytes, bytes/s, numbers, dates, durations) — never their formatted text; natural
/// order for names (file2 &lt; file10); unknown after known values in both directions; a stable
/// secondary key for equal values; the choice is persisted per table and survives refreshes.
/// </summary>
public static class TableSorting
{
    sealed class State(DataGrid grid, string table, IReadOnlyDictionary<string, SortColumn> columns, SortColumn tieBreaker, SortColumn? group)
    {
        /// <summary>Compared before the chosen column, always ascending (unknown last): keeps groups ordered.</summary>
        public SortColumn? Group { get; } = group;
        public DataGrid Grid { get; } = grid;
        public string Table { get; } = table;
        public IReadOnlyDictionary<string, SortColumn> Columns { get; } = columns;
        public SortColumn TieBreaker { get; } = tieBreaker;
        public string? Key { get; set; }
        public ListSortDirection Direction { get; set; }
    }

    static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("TableSortState", typeof(State), typeof(TableSorting));

    /// <summary>Attach header sorting. <paramref name="columns"/> maps DataGridColumn.SortMemberPath to typed keys.</summary>
    public static void Attach(DataGrid grid, string table, IEnumerable<SortColumn> columns, string? defaultKey = null, ListSortDirection defaultDirection = ListSortDirection.Ascending, SortColumn? tieBreaker = null, SortColumn? group = null)
    {
        var map = columns.ToDictionary(c => c.Key, StringComparer.Ordinal);
        var state = new State(grid, table, map, tieBreaker ?? map.Values.First(), group);
        grid.SetValue(StateProperty, state);
        grid.CanUserSortColumns = true;
        foreach (var column in grid.Columns) column.CanUserSort = map.ContainsKey(column.SortMemberPath ?? "");
        var saved = App.Store?.Settings.TableSorts.FirstOrDefault(s => s.Table == table);
        if (saved is not null && map.ContainsKey(saved.Column)) { state.Key = saved.Column; state.Direction = saved.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending; }
        else if (defaultKey is not null && map.ContainsKey(defaultKey)) { state.Key = defaultKey; state.Direction = defaultDirection; }
        grid.Sorting += (_, e) =>
        {
            e.Handled = true;
            var key = e.Column.SortMemberPath;
            if (key is null || !map.ContainsKey(key)) return;
            var direction = state.Key == key && state.Direction == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
            Set(grid, key, direction);
        };
        // A new ItemsSource (e.g. a refreshed filtered list) gets the same order.
        DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(DataGrid))?.AddValueChanged(grid, (_, _) => Apply(state));
        grid.Loaded += (_, _) => Apply(state);
        Apply(state);
    }

    public static string? CurrentKey(DataGrid grid) => (grid.GetValue(StateProperty) as State)?.Key;
    public static ListSortDirection CurrentDirection(DataGrid grid) => (grid.GetValue(StateProperty) as State)?.Direction ?? ListSortDirection.Ascending;

    /// <summary>Programmatic sort (sort menus use the same path as header clicks).</summary>
    public static void Set(DataGrid grid, string key, ListSortDirection direction)
    {
        if (grid.GetValue(StateProperty) is not State state || !state.Columns.ContainsKey(key)) return;
        state.Key = key; state.Direction = direction; Apply(state);
        if (App.Store is { } store)
        {
            store.Settings.TableSorts.RemoveAll(s => s.Table == state.Table);
            store.Settings.TableSorts.Add(new Core.TableSortSetting { Table = state.Table, Column = key, Descending = direction == ListSortDirection.Descending });
            _ = SaveQuietlyAsync(store);
        }
    }

    static async Task SaveQuietlyAsync(Core.AppStore store)
    {
        try { await store.SaveAsync(); }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException) { await LocalDiagnostics.RecordAsync("table.sort.save.failed", error); }
    }

    static void Apply(State state)
    {
        foreach (var gridColumn in state.Grid.Columns)
            gridColumn.SortDirection = state.Key is not null && gridColumn.SortMemberPath == state.Key ? state.Direction : null;
        if (state.Grid.ItemsSource is null) return;
        if (CollectionViewSource.GetDefaultView(state.Grid.ItemsSource) is not ListCollectionView view) return;
        if (state.Key is null || !state.Columns.TryGetValue(state.Key, out var column)) { if (view.CustomSort is TypedComparer) view.CustomSort = null; return; }
        var comparer = new TypedComparer(column, state.Direction, state.TieBreaker, state.Group);
        if (view.CustomSort is TypedComparer existing && existing.SameAs(comparer)) return;
        // Keep the selected object visible: CustomSort refreshes the view, the selected item is
        // the same instance, so the Selector keeps it; scroll it back into view afterwards.
        var selected = state.Grid.SelectedItem;
        view.CustomSort = comparer;
        if (selected is not null && state.Grid.IsLoaded) state.Grid.Dispatcher.BeginInvoke(() => { if (state.Grid.SelectedItem == selected) state.Grid.ScrollIntoView(selected); });
    }

    internal sealed class TypedComparer(SortColumn column, ListSortDirection direction, SortColumn tieBreaker, SortColumn? group = null) : IComparer
    {
        public bool SameAs(TypedComparer other) => ReferenceEquals(column, other.Column) && direction == other.Direction;
        public SortColumn Column => column;
        public ListSortDirection Direction => direction;
        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return 1; if (y is null) return -1;
            if (group is not null && CompareKeys(group, x, y, false) is var byGroup and not 0) return byGroup;
            var result = CompareKeys(column, x, y, direction == ListSortDirection.Descending);
            if (result != 0) return result;
            return ReferenceEquals(column, tieBreaker) ? 0 : CompareKeys(tieBreaker, x, y, false);
        }
    }

    /// <summary>Unknown (null) values are after known ones in either direction.</summary>
    public static int CompareKeys(SortColumn column, object x, object y, bool descending)
    {
        var a = column.Value(x); var b = column.Value(y);
        if (a is null && b is null) return 0;
        if (a is null) return 1;
        if (b is null) return -1;
        int value = column.Natural && a is string sa && b is string sb ? NaturalNameComparer.Instance.Compare(sa, sb)
            : a is string ta && b is string tb ? string.Compare(ta, tb, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase)
            : a.CompareTo(b);
        return descending ? -value : value;
    }
}
