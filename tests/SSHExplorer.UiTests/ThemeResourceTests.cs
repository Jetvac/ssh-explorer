using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SSHExplorer.App;

internal static class ThemeResourceTests
{
    // Loads and seals real application styles without creating a Window or driving the desktop.
    public static int Run()
    {
        try
        {
            var app = new App(); app.InitializeComponent();
            foreach (var mode in new[] { "Light", "Dark", "System" })
            {
                Theme.Apply(mode);
                foreach (var type in new[] { typeof(Button), typeof(TextBox), typeof(PasswordBox), typeof(ComboBox), typeof(CheckBox), typeof(DataGrid), typeof(DataGridColumnHeader), typeof(DataGridRow), typeof(DataGridCell), typeof(ListBox), typeof(ListBoxItem), typeof(TreeView), typeof(TreeViewItem), typeof(ContextMenu), typeof(MenuItem), typeof(ToolTip) })
                {
                    if (app.FindResource(type) is not Style style) throw new Exception("Style missing: " + type.Name);
                    style.Seal();
                }
                foreach (var key in new[] { "Paper", "Surface", "NavigationSurface", "CaptionSurface", "Accent", "DropTarget", "Hover", "Selected", "DisabledInk" })
                    if (app.FindResource(key) is not Brush) throw new Exception("Theme brush missing: " + key);
                Console.WriteLine("PASS actual application Fluent styles and parity tokens: " + mode);
            }
            var contrast = new ResourceDictionary { Source = new Uri("/PresentationFramework.Fluent;component/Themes/Fluent.HC.xaml", UriKind.Relative) };
            if (contrast[typeof(ComboBox)] is not Style contrastStyle) throw new Exception("Native High Contrast style missing."); contrastStyle.Seal();
            Console.WriteLine("PASS bundled native Fluent High Contrast resource dictionary loads");
            app.Shutdown(); Console.WriteLine("RESULT: 4 real theme resource checks passed; no Windows were created"); return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }
}
