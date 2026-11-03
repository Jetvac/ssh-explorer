using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SSHExplorer.App;
using SSHExplorer.App.ViewModels;
using SSHExplorer.Core;
using System.Globalization;

internal static class ThemeResourceTests
{
    // Loads and seals real application styles without creating a Window or driving the desktop.
    public static int Run()
    {
        try
        {
            var app = TestApplication.Create();
            foreach (var mode in new[] { "Light", "Dark", "System" })
            {
                Theme.Apply(mode);
                foreach (var type in new[] { typeof(Button), typeof(TextBox), typeof(PasswordBox), typeof(ComboBox), typeof(CheckBox), typeof(DataGrid), typeof(DataGridColumnHeader), typeof(DataGridRow), typeof(DataGridCell), typeof(ListBox), typeof(ListBoxItem), typeof(TreeView), typeof(TreeViewItem), typeof(ContextMenu), typeof(MenuItem), typeof(ToolTip) })
                {
                    if (app.FindResource(type) is not Style style) throw new Exception("Style missing: " + type.Name);
                    style.Seal();
                }
                foreach (var key in new[] { "Paper", "Surface", "NavigationSurface", "CaptionSurface", "Accent", "DropTarget", "Hover", "Selected", "DisabledInk", "FocusStroke", "SelectedFocusStroke", "PrimaryFocusStroke" })
                    if (app.FindResource(key) is not Brush) throw new Exception("Theme brush missing: " + key);
                foreach (var key in new[] { "UIFontSize", "UICaptionSize", "UIHeadingSize", "ControlMinHeight", "CommandHeight", "RowHeight", "IconSize", "TreeIndent" })
                    if (app.FindResource(key) is not double) throw new Exception("Metric token missing: " + key);
                foreach (var key in new[] { "ControlRadius", "FlyoutRadius" }) if (app.FindResource(key) is not CornerRadius) throw new Exception("Radius token missing: " + key);
                foreach (var key in new[] { "MotionFast", "MotionSurface", "MotionSlow" }) if (app.FindResource(key) is not TimeSpan) throw new Exception("Motion token missing: " + key);
                Console.WriteLine("PASS actual application Fluent styles and parity tokens: " + mode);
            }
            var contrast = new ResourceDictionary { Source = new Uri("/PresentationFramework.Fluent;component/Themes/Fluent.HC.xaml", UriKind.Relative) };
            if (contrast[typeof(ComboBox)] is not Style contrastStyle) throw new Exception("Native High Contrast style missing."); contrastStyle.Seal();
            Console.WriteLine("PASS bundled native Fluent High Contrast resource dictionary loads");
            var cellStyle = (Style)app.FindResource(typeof(DataGridCell));
            if (cellStyle.BasedOn is not null) throw new Exception("Per-cell native/HC triggers can restore the removed cell focus adorner.");
            var cellTemplate = (ControlTemplate)cellStyle.Setters.OfType<Setter>().Single(s => s.Property == Control.TemplateProperty).Value;
            if (cellTemplate.LoadContent() is not Border { BorderThickness: { Left: 0, Top: 0, Right: 0, Bottom: 0 } }) throw new Exception("Cell template restored an inner selection/focus border.");
            var rowStyle = (Style)app.FindResource(typeof(DataGridRow)); var rowTemplate = (ControlTemplate)rowStyle.Setters.OfType<Setter>().Single(s => s.Property == Control.TemplateProperty).Value;
            if (!rowTemplate.Triggers.OfType<Trigger>().Any(t => t.Property == UIElement.IsKeyboardFocusWithinProperty && t.Setters.OfType<Setter>().Any(s => s.TargetName == "RowChrome"))) throw new Exception("Whole-row keyboard focus cue missing.");
            Console.WriteLine("PASS inherited Fluent cell visuals are replaced while whole-row keyboard focus remains defined");
            var converter = new NonEmptyTextVisibilityConverter();
            if (!Equals(converter.Convert(" ", typeof(Visibility), null!, CultureInfo.InvariantCulture), Visibility.Collapsed) || !Equals(converter.Convert("Ошибка", typeof(Visibility), null!, CultureInfo.InvariantCulture), Visibility.Visible)) throw new Exception("Empty status message visibility is incorrect.");
            Console.WriteLine("PASS empty message collapses and nonempty status remains available");
            var special = new RemoteEntry { Name = "looks-like.png", FullPath = "/special" }; var link = new RemoteEntry { Name = "looks-like.jpg", FullPath = "/link", IsSymbolicLink = true, IsRegularFile = true };
            if (FileSystemIcons.Kind(special) != FileIconKind.Special || FileItemViewModel.IsImage(special) || new FileItemViewModel(special).CanPreview || FileSystemIcons.Kind(link) != FileIconKind.Link || FileItemViewModel.IsImage(link) || new FileItemViewModel(link).CanPreview) throw new Exception("Special files/links are being represented as safe preview documents.");
            foreach (var entry in new[] { special, link, new RemoteEntry { IsDirectory = true }, new RemoteEntry { Name = "photo.png", IsRegularFile = true }, new RemoteEntry { Name = "notes.txt", IsRegularFile = true }, new RemoteEntry { Name = "app.sh", IsRegularFile = true }, new RemoteEntry { Name = "a.zip", IsRegularFile = true } })
                if (FileSystemIcons.For(entry) is not DrawingImage { IsFrozen: true }) throw new Exception("File object icon is not a frozen scalable drawing.");
            Console.WriteLine("PASS filled vector categories preserve special/link identity and preview refusal");
            var owner = new Button(); var menu = new ContextMenu(); Ui.ConfigureFlyout(owner, menu, true);
            if (!ReferenceEquals(menu.PlacementTarget, owner) || menu.Placement != PlacementMode.Bottom || menu.StaysOpen) throw new Exception("Flyout is not anchored/dismissible.");
            if (Ui.Menu("Произвольный перевод", () => { }, glyph: Ui.CopyGlyph).Icon is not TextBlock { Text: Ui.CopyGlyph } || Ui.Menu("Удалить", () => { }).Icon is not null) throw new Exception("Menu icon depends on a localized label.");
            Console.WriteLine("PASS flyout placement and explicit command icons do not depend on translated labels");
            var stateChecks = ViewModelTests.Run((condition, description) => { if (!condition) throw new Exception(description); Console.WriteLine("PASS " + description); });
            if (app.Windows.Count != 0) throw new Exception("A headless resource/state check unexpectedly created a Window.");
            app.Shutdown(); Console.WriteLine($"RESULT: 8 resource/contract + {stateChecks} MVVM checks passed; no Windows were created; layout, focus and live UI are separate"); return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }
}
