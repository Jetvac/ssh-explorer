using System.Reflection;
using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SSHExplorer.App;
using SSHExplorer.App.ViewModels;
using SSHExplorer.Core;

internal static class FormTabTests
{
    public static int Run(string[] args)
    {
        CiVisualEvidence.RequireCi();
        try
        {
            var app=TestApplication.Create();SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));Theme.Apply("Light");CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("ru-RU");CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("ru-RU");
            var store=new AppStore(Path.Combine(Environment.CurrentDirectory,".tools","form-tab-ui-store",Guid.NewGuid().ToString("N")));typeof(App).GetProperty(nameof(App.Store))!.SetValue(null,store);
            int index=Array.IndexOf(args,"--render-dir");var evidence=new CiVisualEvidence(index<0?"artifacts/ui-beta4-forms":args[index+1]);
            var main=new MainWindow{Width=1320,Height=850,ShowInTaskbar=false,ShowActivated=false,Left=-10000,Top=-10000,WindowStartupLocation=WindowStartupLocation.Manual};main.Show();Pump();int count=0;
            Run(main,store,evidence,(value,label)=>{if(!value)throw new InvalidOperationException(label);count++;Console.WriteLine("PASS "+label);});
            bool closed=false;main.Closed+=(_,_)=>closed=true;main.Close();var watch=System.Diagnostics.Stopwatch.StartNew();
            while(!closed||app.Windows.Cast<Window>().Contains(main)){if(watch.Elapsed>TimeSpan.FromSeconds(15))throw new TimeoutException("Form/tab fixture main window did not close.");Pump();}
            app.Shutdown();Console.WriteLine($"RESULT: {count} actual form/tab/overflow checks passed; CI-only HWNDs; fresh independent component fixture, no SSH or physical keyboard input");return 0;
        }
        catch(Exception ex){Console.WriteLine(ex);return 1;}
    }
    // Uses a fresh guarded real-window component fixture, independent of file mutations.
    public static void Run(MainWindow main,AppStore store,CiVisualEvidence evidence,Action<bool,string> assert)
    {
        CiVisualEvidence.RequireCi();
        var dialog=new ProfileDialog(new ProfilePersistence(store),owner:main,allowConnect:true){Width=540};dialog.Show();bool activationAccepted=dialog.Activate();Pump();
        try
        {
            var name=Descendants<TextBox>(dialog).Single(field=>AutomationProperties.GetName(field)=="Название");
            var host=Descendants<TextBox>(dialog).Single(field=>AutomationProperties.GetName(field)=="Адрес сервера");
            var combo=Descendants<ComboBox>(dialog).Single(field=>AutomationProperties.GetName(field)=="Способ входа");
            var masked=Descendants<PasswordBox>(dialog).Single();var remember=Descendants<CheckBox>(dialog).Single();
            var tabsBefore=(TabControl)Field(main,"tabs");var selectedBefore=tabsBefore.SelectedItem;int tabCountBefore=tabsBefore.Items.Count;
            bool SourceBlocks(Key key,ModifierKeys modifiers)=>!main.TryHandleTabShortcut(key,modifiers,name,null);
            assert(SourceBlocks(Key.Tab,ModifierKeys.Control)&&SourceBlocks(Key.F4,ModifierKeys.Control)&&SourceBlocks(Key.A,ModifierKeys.Control|ModifierKeys.Shift)&&ReferenceEquals(tabsBefore.SelectedItem,selectedBefore)&&tabsBefore.Items.Count==tabCountBefore,"production tab-shortcut branch leaves tab switch, close and all-tabs commands unhandled for a real text input source");
            bool FocusBlocks(Key key,ModifierKeys modifiers)=>!main.TryHandleTabShortcut(key,modifiers,main,masked);
            assert(FocusBlocks(Key.Tab,ModifierKeys.Control)&&FocusBlocks(Key.F4,ModifierKeys.Control)&&FocusBlocks(Key.A,ModifierKeys.Control|ModifierKeys.Shift)&&ReferenceEquals(tabsBefore.SelectedItem,selectedBefore)&&tabsBefore.Items.Count==tabCountBefore,"production tab-shortcut branch also guards a focused PasswordBox when the routed source is the window");
            bool focusReturn=name.Focus();Pump();dialog.UpdateLayout();bool focusObserved=name.IsKeyboardFocusWithin;
            var nameChrome=Part<Border>(name,"FieldChrome");var passwordChrome=Part<Border>(masked,"FieldChrome");var comboChrome=Part<Border>(combo,"FieldChrome");
            assert(nameChrome.ActualHeight>30&&passwordChrome.ActualHeight>30&&comboChrome.ActualHeight>30&&nameChrome.CornerRadius==passwordChrome.CornerRadius&&comboChrome.CornerRadius==nameChrome.CornerRadius,"actual text/password/combo templates use measured consistent field chrome");
            var chevron=Part<System.Windows.Shapes.Path>(combo,"DropDownChevron");var selection=Part<ContentPresenter>(combo,"SelectionPresenter");var toggle=Part<ToggleButton>(combo,"DropDownToggle");
            var arrowBounds=chevron.TransformToAncestor(combo).TransformBounds(new Rect(chevron.RenderSize));var selectionBounds=selection.TransformToAncestor(combo).TransformBounds(new Rect(selection.RenderSize));
            assert(chevron.ActualWidth>0&&Math.Abs(combo.ActualWidth-arrowBounds.Right-13)<=2&&selectionBounds.Right<=arrowBounds.Left-8&&Math.Abs(toggle.ActualWidth-comboChrome.ActualWidth)<=1&&Math.Abs(toggle.ActualHeight-comboChrome.ActualHeight)<=1,"actual closed ComboBox keeps its chevron at the right edge, reserves selection space and fills the whole field with its hit target");
            if(focusObserved)assert(Part<Border>(name,"FocusUnderline").Visibility==Visibility.Visible,"focused real profile field exposes its applied focus underline");
            else Console.WriteLine("NOT RUN actual profile focus underline: CI did not grant keyboard focus; no product focus state forced");
            Descendants<Button>(dialog).Single(button=>Equals(button.Content,"Сохранить и подключиться")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();dialog.UpdateLayout();
            assert(Ui.GetHasValidationError(name)&&AutomationProperties.GetHelpText(name).Length>0&&Equals(nameChrome.BorderBrush,dialog.FindResource("ValidationInk")),"failed ordinary profile submit applies the actual inline validation brush and accessible reason");
            name.Text="Форма · проверка шаблонов";host.Text="form.example.invalid";name.Focus();Pump();
            masked.Password=" synthetic password ";var reveal=Descendants<ToggleButton>(dialog).Single(button=>AutomationProperties.GetName(button)=="Показать пароль");reveal.IsChecked=true;Pump();
            var secret=Descendants<SecretEntry>(dialog).Single();var revealed=Descendants<TextBox>(secret).Single();
            assert(secret.IsRevealed&&revealed.IsVisible&&revealed.Text==" synthetic password "&&secret.Password==" synthetic password ","real reveal control changes presentation while preserving significant secret whitespace");
            reveal.IsChecked=false;remember.IsChecked=true;Pump();
            assert(!revealed.IsVisible&&masked.IsVisible&&Part<System.Windows.Shapes.Path>(remember,"CheckGlyph").Visibility==Visibility.Visible,"masking restores PasswordBox while the applied checkbox renders its checked glyph");
            remember.IsEnabled=false;name.IsEnabled=false;Pump();
            assert(Part<Border>(name,"FocusUnderline").Visibility==Visibility.Collapsed&&Equals(remember.Foreground,remember.FindResource("DisabledInk")),"disabled field and checkbox use actual disabled template states");
            evidence.Capture((FrameworkElement)dialog.Content,"profile-template-states",new{theme="Light",fixtureOnly=true,syntheticSecretMasked=true,fieldState="filled disabled",rememberState="checked disabled",focusProbe=new{activationAccepted,focusReturn,focusObserved,status=focusObserved?"PASS":"NOT RUN; CI focus denied"}});
            name.IsEnabled=true;remember.IsEnabled=true;combo.IsEditable=true;combo.Focus();Pump();
            assert(Part<TextBox>(combo,"PART_EditableTextBox").IsVisible,"actual editable ComboBox preserves its standard PART_EditableTextBox contract");
            combo.IsEditable=false;combo.Focus();SendKey(combo,Key.F4);Pump();
            var popup=Part<Popup>(combo,"PART_Popup");var child=(FrameworkElement)popup.Child;
            assert(combo.IsDropDownOpen&&Part<Border>(combo,"DropDownSurface").ActualWidth+1>=combo.ActualWidth&&PresentationSource.FromVisual(child) is not null,"real authentication popup has its own presentation source and measured field-width surface");
            int previous=combo.SelectedIndex;SendKey(combo,Key.Down);SendKey(combo,Key.Enter);Pump();
            assert(!combo.IsDropDownOpen&&combo.SelectedIndex!=previous&&dialog.IsVisible,"routed Down/Enter select an actual ComboBox item and close its popup without submitting the dialog");
        }
        finally{dialog.Close();Pump();}
        var tabs=(TabControl)Field(main,"tabs");
        var created=new List<(TabItem Tab,ServerSession Session)>();
        var duplicateProfile=new ServerProfile{Name="Одинаковое очень длинное имя сервера — полное название доступно в списке",Host="fixture.example.invalid",Username="fixture"};store.Profiles.Add(duplicateProfile);
        for(int i=0;i<9;i++)
        {
            var profile=i<2?duplicateProfile:new ServerProfile{Name=$"Узел {i} — длинное имя для проверки переполнения вкладок",Host=$"node-{i}.example.invalid",Username="fixture"};
            if(i>=2)store.Profiles.Add(profile);
            var session=new ServerSession(profile,store);var files=new FileBrowserView(session,main);var monitor=new MonitorView(new MonitorViewModel());var view=new SessionView(session,main,files,monitor);((SessionViewModel)Field(view,"viewModel")).StatusText="Данные стенда · без подключения";view.SetActive(false);
            typeof(MainWindow).GetMethod("AddSessionTab",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(main,[profile,session,view]);view.SetActive(false);created.Add(((TabItem)tabs.SelectedItem,session));
        }
        main.Width=760;main.UpdateLayout();Pump();
        assert(SessionTabInfo.GetTitle(created[0].Tab)==duplicateProfile.Name&&SessionTabInfo.GetTitle(created[1].Tab)==duplicateProfile.Name+" (2)","duplicate long profile tabs retain their full identity and independent-session suffix");
        assert(created.All(pair=>AutomationProperties.GetName(pair.Tab)==SessionTabInfo.GetTitle(pair.Tab)&&((FrameworkElement)pair.Tab.Header).ToolTip?.ToString()?.Contains(SessionTabInfo.GetDescription(pair.Tab),StringComparison.Ordinal)==true),"actual session tab headers expose the full title and endpoint through automation and tooltip");
        var headerScroller=Descendants<ScrollViewer>(tabs).Single(viewer=>viewer.Name=="HeaderScroller");var left=Descendants<Button>(tabs).Single(button=>button.Name=="PART_TabScrollLeft");var right=Descendants<Button>(tabs).Single(button=>button.Name=="PART_TabScrollRight");
        assert(headerScroller.ScrollableWidth>0&&left.IsVisible&&right.IsVisible,"many tabs expose actual header scrolling and both visible navigation arrows at narrow width");
        var lastClose=Descendants<Button>((FrameworkElement)created[^1].Tab.Header).Single(button=>button.Name=="SessionTabCloseButton");var lastCloseBounds=lastClose.TransformToAncestor(headerScroller).TransformBounds(new Rect(lastClose.RenderSize));
        assert(lastClose.ActualWidth>10&&lastCloseBounds.Left>=-.5&&lastCloseBounds.Right<=headerScroller.ViewportWidth+.5,"deferred tab reveal keeps the selected close target inside the actual clipped header viewport after adding and resizing");
        headerScroller.ScrollToHorizontalOffset(0);Pump();double before=headerScroller.HorizontalOffset;
        if(right.Command is RoutedCommand command)command.Execute(right.CommandParameter,right.CommandTarget);else right.Command!.Execute(right.CommandParameter);Pump();
        assert(headerScroller.HorizontalOffset>before,"the visible tab-scroll arrow command moves the actual header viewport");
        var all=main.CreateTabListMenu();var allButton=Descendants<Button>((DependencyObject)main.Content).Single(button=>button.Name=="AllTabsButton");Ui.OpenFlyout(allButton,all);Pump();
        var tabItems=all.Items.OfType<MenuItem>().Where(item=>item.IsCheckable).ToArray();
        assert(tabItems.Length==tabs.Items.Count&&tabItems.Count(item=>item.IsChecked)==1&&tabItems.Any(item=>AutomationProperties.GetName(item)==duplicateProfile.Name+" (2)"),"actual all-tabs flyout lists every full tab identity and exactly one selected tab");
        evidence.Capture(all,"tabs-overflow-menu",new{theme="Light",fixtureOnly=true,tabCount=tabs.Items.Count,headerViewportDip=headerScroller.ViewportWidth,headerExtentDip=headerScroller.ExtentWidth});
        tabItems.Single(item=>AutomationProperties.GetName(item)==duplicateProfile.Name).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));all.IsOpen=false;Pump();
        assert(ReferenceEquals(tabs.SelectedItem,created[0].Tab),"choosing a long full name from the actual flyout selects that exact session");
        evidence.Capture((FrameworkElement)main.Content,"tabs-many-narrow",new{theme="Light",fixtureOnly=true,selectedTitle=SessionTabInfo.GetTitle(created[0].Tab),tabCount=tabs.Items.Count});
        var close=Descendants<Button>((FrameworkElement)created[0].Tab.Header).Single(button=>button.Name=="SessionTabCloseButton");
        assert(close.IsVisible&&close.ActualWidth>10&&AutomationProperties.GetName(close).Contains(duplicateProfile.Name,StringComparison.Ordinal),"selected tab exposes a measured close target with its full accessible name");
        Complete(main.CloseSessionTabAsync(created[0].Tab));Pump();
        assert(!tabs.Items.Contains(created[0].Tab)&&ReferenceEquals(tabs.SelectedItem,created[1].Tab)&&created[0].Session.State==SessionState.Closed&&created.Skip(1).All(pair=>pair.Session.State!=SessionState.Closed),"closing the selected tab disposes only its session and selects the adjacent right tab");
        var active=tabs.SelectedItem;Complete(main.CloseSessionTabAsync(created[^1].Tab));Pump();
        assert(ReferenceEquals(tabs.SelectedItem,active)&&created[^1].Session.State==SessionState.Closed,"closing a background tab preserves the active session selection");
        tabs.SelectedIndex=0;Pump();var homeBar=Descendants<ResponsiveCommandBar>((DependencyObject)main.Content).Single(bar=>bar.Name=="HomeCommandBar");homeBar.Width=300;main.UpdateLayout();Pump();
        ((DataGrid)Field(main,"servers")).UnselectAll();Pump();var homeMenu=homeBar.CreateOverflowMenu();
        assert(homeBar.VisibleCommandKeys.Contains("add")&&homeBar.OverflowCommandKeys.Contains("remove")&&homeMenu.Items.OfType<MenuItem>().Where(item=>Equals(item.Header,"Удалить профиль")||Equals(item.Header,"Изменить")).All(item=>!item.IsEnabled),"narrow Home keeps Add visible and propagates disabled profile actions into overflow");
        ((DataGrid)Field(main,"servers")).SelectedIndex=0;Pump();homeMenu=homeBar.CreateOverflowMenu();
        assert(homeMenu.Items.OfType<MenuItem>().Single(item=>Equals(item.Header,"Удалить профиль")).IsEnabled,"Home overflow is rebuilt from the current selected-profile availability");
        var homeOverflow=Descendants<Button>(homeBar).Single(button=>button.Name=="CommandOverflowButton");Ui.OpenFlyout(homeOverflow,homeMenu);Pump();evidence.Capture(homeMenu,"home-overflow-menu",new{theme="Light",fixtureOnly=true,barWidthDip=homeBar.ActualWidth,visibleKeys=homeBar.VisibleCommandKeys,overflowKeys=homeBar.OverflowCommandKeys});homeMenu.IsOpen=false;homeBar.Width=double.NaN;
    }
    static T Part<T>(Control owner,string name)where T:DependencyObject{owner.ApplyTemplate();return owner.Template.FindName(name,owner) as T??throw new InvalidOperationException($"Applied {owner.GetType().Name} template is missing {name}.");}
    static object Field(object target,string name)=>target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(target)!;
    static void SendKey(UIElement target,Key key)=>target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(target)!,Environment.TickCount,key){RoutedEvent=Keyboard.KeyDownEvent});
    static void Complete(Task task){var watch=System.Diagnostics.Stopwatch.StartNew();while(!task.IsCompleted){if(watch.Elapsed>TimeSpan.FromSeconds(15))throw new TimeoutException("Tab close did not settle.");Pump();}task.GetAwaiter().GetResult();}
    static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,()=>frame.Continue=false);Dispatcher.PushFrame(frame);}
    static IEnumerable<T> Descendants<T>(DependencyObject root)where T:DependencyObject{for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T match)yield return match;foreach(var nested in Descendants<T>(child))yield return nested;}}
}
