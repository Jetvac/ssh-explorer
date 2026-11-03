using System.Windows;
using System.Windows.Data;
using System.Globalization;

namespace SSHExplorer.App;

/// <summary>Unabridged tab identity, independent of its truncated visual header or drag token.</summary>
public static class SessionTabInfo
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.RegisterAttached("Title", typeof(string), typeof(SessionTabInfo), new PropertyMetadata(""));
    public static void SetTitle(DependencyObject target, string value) => target.SetValue(TitleProperty, value);
    public static string GetTitle(DependencyObject target) => (string)target.GetValue(TitleProperty);
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.RegisterAttached("Description", typeof(string), typeof(SessionTabInfo), new PropertyMetadata(""));
    public static void SetDescription(DependencyObject target, string value) => target.SetValue(DescriptionProperty, value);
    public static string GetDescription(DependencyObject target) => (string)target.GetValue(DescriptionProperty);
}

public sealed class TabScrollAvailabilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.Length == 2 && values[0] is double offset && values[1] is double extent && (parameter is "Right" ? extent-offset > .5 : offset > .5);
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)=>throw new NotSupportedException();
}
