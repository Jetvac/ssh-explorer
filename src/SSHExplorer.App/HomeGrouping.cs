using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using SSHExplorer.App.ViewModels;

namespace SSHExplorer.App;

/// <summary>
/// Groups the Home table by profile group with collapsible headers ("Прод · 3"). Groups are ordered by name,
/// «Без группы» last (the group key is the first sort key in <see cref="TableSorting"/>). Collapsed groups stay
/// collapsed across refreshes for the lifetime of the window. Without any group the table stays flat.
/// </summary>
public static class HomeGrouping
{
    static readonly HashSet<string> Collapsed = new(StringComparer.CurrentCultureIgnoreCase);

    public static void Attach(DataGrid grid, Func<bool> enabled)
    {
        grid.GroupStyle.Add(new GroupStyle { ContainerStyle = ContainerStyle(), HidesIfEmpty = true });
        void Apply()
        {
            if (grid.ItemsSource is null || CollectionViewSource.GetDefaultView(grid.ItemsSource) is not ListCollectionView view) return;
            bool on = enabled();
            if (on && view.GroupDescriptions.Count == 0) view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ServerProfileViewModel.GroupName), null, StringComparison.CurrentCultureIgnoreCase));
            else if (!on && view.GroupDescriptions.Count > 0) view.GroupDescriptions.Clear();
        }
        DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(DataGrid))?.AddValueChanged(grid, (_, _) => Apply());
        grid.Loaded += (_, _) => Apply();
        Apply();
    }

    public static bool IsCollapsed(string group) => Collapsed.Contains(group);

    static Style ContainerStyle()
    {
        var expander = new FrameworkElementFactory(typeof(Expander));
        expander.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 0));
        expander.SetBinding(Expander.IsExpandedProperty, new Binding(nameof(CollectionViewGroup.Name)) { Converter = new ExpandedConverter(), Mode = BindingMode.OneTime });
        expander.AddHandler(Expander.CollapsedEvent, new RoutedEventHandler((s, _) => { if (((FrameworkElement)s).DataContext is CollectionViewGroup g) Collapsed.Add(g.Name?.ToString() ?? ""); }));
        expander.AddHandler(Expander.ExpandedEvent, new RoutedEventHandler((s, _) => { if (((FrameworkElement)s).DataContext is CollectionViewGroup g) Collapsed.Remove(g.Name?.ToString() ?? ""); }));
        var header = new FrameworkElementFactory(typeof(TextBlock));
        header.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        header.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 6, 0, 6));
        header.SetBinding(TextBlock.TextProperty, new MultiBinding { Converter = new HeaderConverter(), Bindings = { new Binding(nameof(CollectionViewGroup.Name)), new Binding(nameof(CollectionViewGroup.ItemCount)) } });
        header.SetValue(AutomationProperties.NameProperty, "Группа серверов");
        var headerTemplate = new DataTemplate { VisualTree = header };
        expander.SetValue(HeaderedContentControl.HeaderTemplateProperty, headerTemplate);
        // The header template's DataContext is the Expander's Header; bind Header to the group itself.
        expander.SetBinding(HeaderedContentControl.HeaderProperty, new Binding());
        expander.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        var template = new ControlTemplate(typeof(GroupItem)) { VisualTree = expander };
        var style = new Style(typeof(GroupItem)); style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    sealed class ExpandedConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => !Collapsed.Contains(value?.ToString() ?? "");
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }
    sealed class HeaderConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture) => $"{values[0]} · {values[1]}";
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }
}
