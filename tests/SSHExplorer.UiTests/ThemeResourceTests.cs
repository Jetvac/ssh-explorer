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
                var templates=new HashSet<FrameworkTemplate>();
                foreach (var type in new[] { typeof(Button), typeof(TextBox), typeof(PasswordBox), typeof(ComboBox), typeof(CheckBox), typeof(DataGrid), typeof(DataGridColumnHeader), typeof(DataGridRow), typeof(DataGridCell), typeof(ListBox), typeof(ListBoxItem), typeof(TreeView), typeof(TreeViewItem), typeof(ContextMenu), typeof(MenuItem), typeof(ToolTip) })
                {
                    if (app.FindResource(type) is not Style style) throw new Exception("Style missing: " + type.Name);
                    style.Seal();MaterializeTemplates(style,templates);
                }
                foreach(var key in new[]{"CommandButtonStyle","PrimaryButtonStyle","NavigationButtonStyle","SubNavigationButtonStyle","TabCloseButtonStyle","SessionTabControlStyle","SessionTabItemStyle","FileTableStyle","HomeTableStyle","TransferTableStyle","ProcessCellStyle","ProcessMetricCellStyle","ProcessColumnHeaderStyle","ProcessTableStyle","ProcessValueStyle","MonitorPageTitleStyle","CommandBarStyle","StatusBarStyle","DialogWindowStyle","DialogTitleStyle","DialogFooterStyle","ValidationMessageStyle"})
                {
                    if(app.FindResource(key)is not Style role)throw new Exception("Role style missing: "+key);
                    // FindResource forces deferred BAML setter conversion; Seal and
                    // LoadContent exercise templates without creating a Window.
                    role.Seal();MaterializeTemplates(role,templates);
                }
                MaterializeMonitorMetricTemplate();
                MaterializeInputs();
                foreach (var key in new[] { "Paper", "Surface", "NavigationSurface", "CaptionSurface", "Accent", "DropTarget", "Hover", "Selected", "DisabledInk", "FocusStroke", "SelectedFocusStroke", "PrimaryFocusStroke" })
                    if (app.FindResource(key) is not Brush) throw new Exception("Theme brush missing: " + key);
                foreach (var key in new[] { "UIFontSize", "UICaptionSize", "UIHeadingSize", "ControlMinHeight", "CommandHeight", "RowHeight", "IconSize", "TreeIndent" })
                    if (app.FindResource(key) is not double) throw new Exception("Metric token missing: " + key);
                foreach (var key in new[] { "ControlRadius", "FlyoutRadius" }) if (app.FindResource(key) is not CornerRadius) throw new Exception("Radius token missing: " + key);
                foreach (var key in new[] { "MotionFast", "MotionSurface", "MotionSlow" }) if (app.FindResource(key) is not TimeSpan) throw new Exception("Motion token missing: " + key);
                Console.WriteLine("PASS actual application Fluent styles, 22 explicit role styles, applied input templates and metric namescopes: " + mode);
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
            // beta.6 caption row: actions and caption buttons live in the TabControl template's TrailingHost.
            var strip = new TabControl(); strip.SetResourceReference(FrameworkElement.StyleProperty, "SessionTabControlStyle"); strip.Items.Add(new TabItem { Header = "Главная" });
            var trailingProbe = new Button { Name = "TrailingProbe", Width = 36, Height = 36 }; SessionTabInfo.SetTrailingContent(strip, trailingProbe);
            strip.Measure(new Size(1000, 400)); strip.Arrange(new Rect(0, 0, 1000, 400)); strip.UpdateLayout();
            if (strip.Template?.FindName("TrailingHost", strip) is not ContentPresenter trailingHost) throw new Exception("Session tab template has no TrailingHost.");
            if (!ReferenceEquals(trailingHost.Content, trailingProbe)) throw new Exception($"TrailingHost content is not the tab control's TrailingContent (actual: {trailingHost.Content?.GetType().Name ?? "null"}; binding={System.Windows.Data.BindingOperations.GetBindingExpressionBase(trailingHost, ContentPresenter.ContentProperty)?.Status.ToString() ?? "none"}; source={trailingHost.ReadLocalValue(ContentPresenter.ContentProperty)}; templated={trailingHost.TemplatedParent?.GetType().Name}).");
            if (!strip.IsAncestorOf(trailingProbe) || trailingProbe.ActualWidth < 30) throw new Exception($"TrailingContent is not in the measured caption row (ancestor={strip.IsAncestorOf(trailingProbe)}, width={trailingProbe.ActualWidth}).");
            Console.WriteLine("PASS caption-row trailing actions are presented inside the measured session tab template");
            var stateChecks = ViewModelTests.Run((condition, description) => { if (!condition) throw new Exception(description); Console.WriteLine("PASS " + description); });
            if (app.Windows.Count != 0) throw new Exception("A headless resource/state check unexpectedly created a Window.");
            app.Shutdown(); Console.WriteLine($"RESULT: 9 resource/contract + {stateChecks} MVVM checks passed; no Windows were created; layout, focus and live UI are separate"); return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }
    static void MaterializeTemplates(Style style,HashSet<FrameworkTemplate> visited)
    {
        if(style.BasedOn is not null)MaterializeTemplates(style.BasedOn,visited);
        foreach(var setter in style.Setters.OfType<Setter>())Load(setter.Value);
        foreach(var trigger in style.Triggers)
        {
            if(trigger is Trigger normal)foreach(var setter in normal.Setters.OfType<Setter>())Load(setter.Value);
            else if(trigger is MultiTrigger multi)foreach(var setter in multi.Setters.OfType<Setter>())Load(setter.Value);
            else if(trigger is DataTrigger data)foreach(var setter in data.Setters.OfType<Setter>())Load(setter.Value);
            else if(trigger is MultiDataTrigger multiData)foreach(var setter in multiData.Setters.OfType<Setter>())Load(setter.Value);
        }
        void Load(object value){if(value is FrameworkTemplate template&&visited.Add(template))_ = template.LoadContent();}
    }
    static void MaterializeMonitorMetricTemplate()
    {
        using var monitor=new MonitorView(new MonitorViewModel());
        var column=monitor.ProcessTable.Columns.OfType<DataGridTemplateColumn>().Single(column=>column.SortMemberPath==nameof(ProcessItemViewModel.Cpu));
        var template=column.CellTemplate??throw new Exception("Metric cell template is missing.");
        template.Seal();
        var loaded=template.LoadContent() as Border??throw new Exception("Metric template did not materialize a Border.");
        if(loaded.Name!="MetricSurface"||loaded.Child is not TextBlock{Name:"MetricValue"})throw new Exception("Factory template names were not propagated to actual element Name properties.");
        var presenter=new ContentPresenter{ContentTemplate=template,Content=new ProcessItemViewModel(new(new(23,100,1000),"fixture","fixture","S",0,4096,0,0,null),1L<<30)};
        presenter.Measure(new Size(200,40));presenter.Arrange(new Rect(0,0,200,40));presenter.UpdateLayout();
        if(template.FindName("MetricSurface",presenter)is not Border surface||surface.Name!="MetricSurface"||template.FindName("MetricValue",presenter)is not TextBlock{Name:"MetricValue"})
            throw new Exception("Actual metric content presenter did not register its named template parts.");
        // This is a template materialization/name contract, not evidence of
        // DataGrid cell width or a visible UI; that boundary is measured in CI.
    }
    static void MaterializeInputs()
    {
        var text=new TextBox{Text="fixture"};var password=new PasswordBox{Password=" fixture "};var combo=new ComboBox{ItemsSource=new[]{"one","two"},SelectedIndex=0};var check=new CheckBox{Content="fixture",IsThreeState=true};
        foreach(var control in new Control[]{text,password,combo,check}){control.Style=(Style)Application.Current.FindResource(control.GetType());control.SetResourceReference(Control.FontSizeProperty,"UIFontSize");control.ApplyTemplate();control.Measure(new Size(280,double.PositiveInfinity));control.Arrange(new Rect(0,0,280,control.DesiredSize.Height));control.UpdateLayout();}
        if(Math.Abs(text.DesiredSize.Height-password.DesiredSize.Height)>1||Math.Abs(text.DesiredSize.Height-combo.DesiredSize.Height)>1)throw new Exception("Unshown native input views requested unequal heights; padding may be counted twice.");
        foreach(var control in new Control[]{text,password})
        {
            _=Part<ScrollViewer>(control,"PART_ContentHost");_=Part<Border>(control,"FieldChrome");_=Part<Border>(control,"FocusUnderline");
            Ui.SetHasValidationError(control,true);
            if(!Equals(Part<Border>(control,"FieldChrome").BorderBrush,control.FindResource("ValidationInk")))throw new Exception("Applied input validation trigger did not update its chrome.");
        }
        var popup=Part<Popup>(combo,"PART_Popup");_=Part<TextBox>(combo,"PART_EditableTextBox");_=Part<Border>(combo,"FieldChrome");
        if(popup.IsOpen||PresentationSource.FromVisual(combo)is not null)throw new Exception("Headless input materialization unexpectedly acquired a presentation source.");
        foreach(double width in new[]{240d,480d})
        {
            combo.Measure(new Size(width,60));combo.Arrange(new Rect(0,0,width,60));combo.UpdateLayout();var arrow=Part<System.Windows.Shapes.Path>(combo,"DropDownChevron");var comboToggle=Part<ToggleButton>(combo,"DropDownToggle");
            var bounds=arrow.TransformToAncestor(combo).TransformBounds(new Rect(arrow.RenderSize));
            if(arrow.ActualWidth<=0||Math.Abs(combo.ActualWidth-bounds.Right-13)>1||Math.Abs(comboToggle.ActualWidth-combo.ActualWidth)>1)throw new Exception("Unshown applied ComboBox template put its arrow outside the reserved right edge or shrank its hit target.");
        }
        combo.IsEditable=true;if(Part<TextBox>(combo,"PART_EditableTextBox").Visibility!=Visibility.Visible)throw new Exception("Editable input template did not expose its standard edit part.");
        check.IsChecked=true;if(Part<System.Windows.Shapes.Path>(check,"CheckGlyph").Visibility!=Visibility.Visible)throw new Exception("Applied checkbox did not render its checked state.");
        check.IsChecked=null;if(Part<Border>(check,"IndeterminateGlyph").Visibility!=Visibility.Visible)throw new Exception("Applied checkbox did not render its indeterminate state.");
        var secret=new SecretEntry();secret.ApplyTemplate();secret.Measure(new Size(280,60));secret.Arrange(new Rect(0,0,280,60));secret.UpdateLayout();secret.Password=" fixture ";int revision=secret.EditRevision;var toggle=Descendants<ToggleButton>(secret).Single();toggle.IsChecked=true;
        if(!secret.IsRevealed||secret.Password!=" fixture "||secret.EditRevision!=revision)throw new Exception("Reveal changed the secret or edit revision during headless template materialization.");
        toggle.IsChecked=false;secret.Clear();
        static T Part<T>(Control control,string name)where T:DependencyObject=>control.Template.FindName(name,control)as T??throw new Exception($"Applied {control.GetType().Name} template is missing {name}.");
    }
    static IEnumerable<T> Descendants<T>(DependencyObject root)where T:DependencyObject{for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T match)yield return match;foreach(var nested in Descendants<T>(child))yield return nested;}}
}
