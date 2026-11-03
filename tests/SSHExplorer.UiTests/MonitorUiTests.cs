using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SSHExplorer.App;
using SSHExplorer.App.ViewModels;
using SSHExplorer.Core;
using SSHExplorer.Monitoring;

internal static class MonitorUiTests
{
    static readonly DateTimeOffset Now=CompositionFixtures.Timestamp;
    public static int GraphOnly()
    {
        try{int count=GraphChecks(Check);Console.WriteLine($"RESULT: {count} timestamp/gap geometry checks passed; no Application, Window, HWND or SSH created");return 0;}
        catch(Exception ex){Console.WriteLine(ex);return 1;}
    }
    public static int Run(string[] args)
    {
        CiVisualEvidence.RequireCi();
        try
        {
            var app=TestApplication.Create();Theme.Apply("Light");int checks=0;
            CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("ru-RU");CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("ru-RU");
            void Assert(bool value,string text){Check(value,text);checks++;}
            var evidence=new CiVisualEvidence(Value(args,"--render-dir")??"artifacts/ui-beta4-after");
            checks+=GraphChecks(Check);
            var store=new AppStore(Path.Combine(Environment.CurrentDirectory,".tools","monitor-ui-store",Guid.NewGuid().ToString("N")));
            typeof(App).GetProperty(nameof(App.Store))!.SetValue(null,store);store.Settings.ShowHidden=true;
            var profile=new ServerProfile{Name="Linux · UI fixture",Host="127.0.0.1",Username="fixture"};
            store.Profiles.Add(profile);var session=new ServerSession(profile,store);var main=new MainWindow{Width=1320,Height=850,ShowActivated=false,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000};
            using var files=new FileBrowserView(session,main);var fileModel=(FileBrowserViewModel)files.DataContext;
            var entries=CompositionFixtures.Files();
            fileModel.CommitNavigation(fileModel.BeginNavigation(),"/srv/fixture",entries,true,[],0);fileModel.ApplyFilter(true,true);Invoke(files,"ApplyItems");Invoke(files,"UpdateBreadcrumbs");
            var tree=(TreeView)Field(files,"tree");tree.Items.Clear();var root=Node(files,"Корень /","/");var folder=Node(files,"fixture","/srv/fixture");root.Items.Clear();folder.Items.Clear();Ui.SetIsCurrentFolder(folder,true);root.Items.Add(folder);tree.Items.Add(root);root.IsExpanded=folder.IsExpanded=true;
            var model=new MonitorViewModel(()=>Now);using var accentProbe=new AccentProbe();using var monitor=new MonitorView(model);model.ApplySnapshot(Sample(),History());
            var view=new SessionView(session,main,files,monitor);((SessionViewModel)Field(view,"viewModel")).StatusText="Данные стенда · без подключения";view.SetActive(false);
            typeof(MainWindow).GetMethod("AddSessionTab",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(main,[profile,session,view]);
            main.Show();view.SetActive(false);view.ShowSection("processes");Layout(main);SetGraphTime(monitor);
            Assert(!Descendants<TabControl>(monitor).Any(),"process/performance pages do not introduce a second server-style TabControl");
            Assert(!Ui.GetIsFileSectionActive(files.Navigation)&&Ui.GetIsCurrentFolder(folder),"monitor navigation keeps the remembered folder while marking file section inactive");
            var navigation=(Dictionary<string,Button>)Field(view,"navigationButtons");
            Assert(Ui.GetIsNavigationActive(navigation["processes"])&&!Ui.GetIsNavigationActive(navigation["files"]),"exact process subpage is active while files remain inactive");
            Assert(navigation.Values.All(button=>!Ancestors<ScrollViewer>(button).Any()),"long tree scrolling cannot carry fixed section navigation out of view");
            Assert(Descendants<ScrollViewer>(tree).Count()==1,"folder tree has one native scrolling region without an outer competing ScrollViewer");
            CheckHeader(monitor,Assert);
            var table=monitor.ProcessTable;Layout(main);
            var warmup=Sample() with{System=Sample().System with{CpuPercent=null},Processes=Sample().Processes.Select(p=>p with{CpuPercent=null,ReadBytesPerSecond=null,WriteBytesPerSecond=null}).ToArray()};
            model.IsActive=true;model.IsBusy=true;model.ApplySnapshot(warmup,[]);Layout(main);
            evidence.Capture((FrameworkElement)main.Content,"processes-initial",new{theme="Light",fixtureOnly=true,phase="initial delta baseline, independently available memory"});
            var firstRow=(DataGridRow)table.ItemContainerGenerator.ContainerFromIndex(0)!;var unknownCpu=Descendants<DataGridCell>(firstRow).Single(c=>c.Column.SortMemberPath==nameof(ProcessItemViewModel.Cpu));
            var unknownSurface=NamedPart<Border>(unknownCpu,"MetricSurface");var unknownValue=NamedPart<TextBlock>(unknownSurface,"MetricValue");
            Assert(unknownValue.Text=="—"&&AutomationProperties.GetHelpText(unknownValue).Length>0&&unknownSurface.Background is SolidColorBrush{Color.A:0},"first-sample unknown CPU has an uncolored dash and an accessible concrete reason");
            model.ApplySnapshot(Sample(),History());model.IsBusy=false;Layout(main);
            var selected=model.Processes[3];table.SelectedItem=selected;table.ScrollIntoView(selected);Layout(main);
            Assert(model.SelectedIdentity==selected.Metric.Identity,"actual process table selection updates the exact process identity");
            var row=(DataGridRow)table.ItemContainerGenerator.ContainerFromItem(selected)!;
            var cell=Descendants<DataGridCell>(row).First(c=>c.Column.SortMemberPath==nameof(ProcessItemViewModel.MemoryBytes));
            var surface=NamedPart<Border>(cell,"MetricSurface");var label=NamedPart<TextBlock>(surface,"MetricValue");
            Assert(surface.ActualWidth>40&&surface.ActualHeight>0&&Math.Abs(cell.ActualWidth-surface.ActualWidth)<=2&&Math.Abs(cell.ActualHeight-surface.ActualHeight)<=2,"metric background occupies both measured cell axes rather than only its text bounds");
            Assert(label.TextAlignment==TextAlignment.Right&&surface.Padding.Left==surface.Padding.Right&&surface.Padding.Right>0,"metric value is right aligned inside fixed symmetric cell padding");
            Assert(surface.Background is SolidColorBrush {Color.A:0},"selected process heat surface yields to the whole-row selection cue");
            table.SelectedItem=null;Layout(main);accentProbe.Enabled=true;Theme.Apply("Dark");Layout(main);
            Assert(surface.Background is SolidColorBrush palette&&palette.Color.R==AccentProbe.ProbeColor.R&&palette.Color.G==AccentProbe.ProbeColor.G&&palette.Color.B==AccentProbe.ProbeColor.B,"theme event repaints the existing metric surface with a changed resource palette without a new sample");
            accentProbe.Enabled=false;Theme.Apply("Light");table.SelectedItem=selected;Layout(main);
            var nameCell=Descendants<DataGridCell>(row).First(c=>c.Column.SortMemberPath==nameof(ProcessItemViewModel.Name));
            Assert(Descendants<TextBlock>(nameCell).Any(t=>t.TextTrimming==TextTrimming.CharacterEllipsis&&Equals(t.ToolTip,selected.Name)&&AutomationProperties.GetName(t)==selected.Name),"long process name is ellipsized with full tooltip and automation name");
            var cellsBefore=table.ItemsSource;var scroll=Descendants<ScrollViewer>(table).First();scroll.ScrollToVerticalOffset(18);Layout(main);double offset=scroll.VerticalOffset;
            var next=Sample() with{Timestamp=Now.AddSeconds(1),Processes=Sample().Processes.Select(p=>p with{CpuPercent=p.CpuPercent+1}).ToArray()};
            model.ApplySnapshot(next,History());Layout(main);
            Assert(offset>0&&ReferenceEquals(cellsBefore,table.ItemsSource)&&ReferenceEquals(selected,table.SelectedItem)&&Math.Abs(scroll.VerticalOffset-offset)<.1,"actual refresh preserves collection, selected row and nonzero process viewport offset");
            var collection=CollectionViewSource.GetDefaultView(table.ItemsSource);collection.SortDescriptions.Clear();collection.SortDescriptions.Add(new SortDescription(nameof(ProcessItemViewModel.Cpu),ListSortDirection.Descending));Layout(main);
            var values=collection.Cast<ProcessItemViewModel>().Select(p=>p.Cpu).Where(v=>v.HasValue).Select(v=>v!.Value).ToArray();
            Assert(values.Zip(values.Skip(1),(a,b)=>a>=b).All(x=>x),"process CPU ordering is numeric in the actual collection view");
            // Restore the exact published-baseline fixture before comparable renders:
            // original timestamp/values, original ordering, top viewport, long-name row selected.
            monitor.SetSort(null,ListSortDirection.Ascending);collection.SortDescriptions.Clear();model.ApplySnapshot(Sample(),History());model.IsBusy=false;
            table.SelectedItem=model.Processes[3];scroll.ScrollToVerticalOffset(0);Layout(main);SetGraphTime(monitor);
            var captureRow=(DataGridRow)table.ItemContainerGenerator.ContainerFromItem(model.Processes[3])!;
            var captureCell=Descendants<DataGridCell>(captureRow).Single(c=>c.Column.SortMemberPath==nameof(ProcessItemViewModel.MemoryBytes));
            var captureSurface=NamedPart<Border>(captureCell,"MetricSurface");
            evidence.Capture((FrameworkElement)main.Content,"processes-wide",new{theme="Light",fixtureOnly=true,processCount=160,sampleTimestamp=Now,selectedPid=103,verticalOffset=scroll.VerticalOffset,sort="fixture order",metricCellDip=captureCell.ActualWidth,metricSurfaceDip=captureSurface.ActualWidth,metricCellHeightDip=captureCell.ActualHeight,metricSurfaceHeightDip=captureSurface.ActualHeight,metricPadding=captureSurface.Padding.Right});
            main.Width=760;Layout(main);CheckHeader(monitor,Assert);
            Assert(table.Columns.Where(c=>c.Visibility==Visibility.Visible).Any(c=>c.SortMemberPath==nameof(ProcessItemViewModel.Name))&&table.Columns.Where(c=>c.Visibility==Visibility.Visible).Any(c=>c.SortMemberPath==nameof(ProcessItemViewModel.Cpu)),"narrow viewport retains process name and CPU instead of only secondary columns");
            evidence.Capture((FrameworkElement)main.Content,"processes-narrow",new{theme="Light",fixtureOnly=true});
            CheckNarrowColumns(table,Assert);
            main.Width=1320;Layout(main);
            object originalFont=Application.Current.Resources["UIFontSize"]??throw new Exception("UI font role is unavailable.");
            try
            {
                Application.Current.Resources["UIFontSize"]=22d;Layout(main);CheckHeader(monitor,Assert);
                var largeRow=(DataGridRow)table.ItemContainerGenerator.ContainerFromItem(model.Processes[3])!;
                var largeLabels=Descendants<DataGridCell>(largeRow).Where(c=>c.IsVisible).SelectMany(c=>Descendants<TextBlock>(c)).Where(t=>t.IsVisible&&t.FontSize>=22).ToArray();
                Assert(largeLabels.Length>0&&largeLabels.All(t=>t.ActualHeight+.5>=NaturalTextHeight(t)),"large UI font role keeps measured process cell text within an expanded row");
                CheckStateHeader(table,Assert);
                evidence.Capture((FrameworkElement)main.Content,"processes-large-text",new{theme="Light",fixtureOnly=true,uiFontRoleDip=22,osTextScaleChanged=false,dpiChanged=false});
            }
            finally{Application.Current.Resources["UIFontSize"]=originalFont;Layout(main);}
            double fileOffset=0;var fileTable=(DataGrid)Field(files,"table");
            view.ShowSection("files");Layout(main);fileTable.SelectedItem=fileModel.Entries[5];var fileScroll=Descendants<ScrollViewer>(fileTable).First();fileScroll.ScrollToVerticalOffset(20);Layout(main);fileOffset=fileScroll.VerticalOffset;var fileSelection=fileModel.SelectedPaths.ToArray();int historyCount=fileModel.History.Count;
            view.ShowSection("processes");view.ShowSection("performance");Layout(main);SetGraphTime(monitor);
            Assert(monitor.Page==MonitorPage.Performance&&Ui.GetIsNavigationActive(navigation["performance"]),"performance subpage belongs to the common sidebar and uses the same monitor instance");
            evidence.Capture((FrameworkElement)main.Content,"performance-gap",new{theme="Light",fixtureOnly=true,actualHistoryGapsSeconds=new[]{1,59,1,59}});
            view.ShowSection("files");Layout(main);
            Assert(fileOffset>0&&fileModel.CurrentPath=="/srv/fixture"&&fileModel.History.Count==historyCount&&fileModel.SelectedPaths.SequenceEqual(fileSelection)&&Math.Abs(fileScroll.VerticalOffset-fileOffset)<.1&&folder.IsExpanded,"files-processes-performance-files preserves path, history, selection, nonzero scroll and expanded tree");
            Assert(Ui.GetIsFileSectionActive(files.Navigation)&&Ui.GetIsCurrentFolder(folder)&&Ui.GetIsNavigationActive(navigation["files"]),"returning to files restores active section without discarding current folder marker");
            fileScroll.ScrollToVerticalOffset(0);Layout(main);
            evidence.Capture((FrameworkElement)main.Content,"files-tree",new{theme="Light",fixtureOnly=true,fileCount=180,selectedFile="document-005.txt",verticalOffset=fileScroll.VerticalOffset});
            view.ShowSection("processes");model.ReportFailure("Fixture: SSH sample timed out");Layout(main);
            Assert(model.IsStale&&model.Processes.Count>0&&model.StatusDetails.Contains("Fixture",StringComparison.Ordinal),"failed sample keeps actual table rows with an explicit stale reason");
            evidence.Capture((FrameworkElement)main.Content,"processes-stale",new{theme="Light",fixtureOnly=true});
            Theme.Apply("Dark");Layout(main);evidence.Capture((FrameworkElement)main.Content,"processes-dark",new{theme="Dark",fixtureOnly=true});Theme.Apply("Light");
            view.ShowSection("files");Layout(main);evidence.CaptureRelatedScenes(main,files,store,Assert,includeNarrowValidation:true);
            checks+=MonitorRefinementTests.RunUiWindow(main,Check);
            CaptureCultures(main,evidence,Assert);
            Close(main,app);app.Shutdown();Console.WriteLine($"RESULT: {checks} monitor/shell actual WPF UI and graph checks passed; CI-only HWNDs, offline fixture, no SSH or process signals");return 0;
        }
        catch(Exception ex){Console.WriteLine(ex);return 1;}
    }
    static int GraphChecks(Action<bool,string> assert)
    {
        int count=0;void CheckGraph(bool ok,string label){assert(ok,label);count++;}
        var points=new[]{new HistoryPoint(Now.AddSeconds(-9),10,30,0,0),new HistoryPoint(Now.AddSeconds(-8),20,30,0,0),new HistoryPoint(Now.AddSeconds(-2),40,30,0,0),new HistoryPoint(Now.AddSeconds(-1),50,30,0,0)};
        var segments=MetricGraph.BuildSegments(points,GraphMetric.Cpu,Now,TimeSpan.FromSeconds(10),TimeSpan.FromSeconds(3),new Size(1000,100));
        CheckGraph(segments.Count==2&&segments.All(s=>s.Length==2),"actual graph geometry separates a long sampling pause into two segments");
        CheckGraph(Math.Abs(segments[0][0].X-100)<.001&&Math.Abs(segments[1][0].X-800)<.001,"actual graph horizontal positions use elapsed timestamps instead of sample index");
        var gap=points.ToArray();gap[1]=gap[1] with{IsGap=true,Reason="pause"};
        var split=MetricGraph.BuildSegments(gap,GraphMetric.Cpu,Now,TimeSpan.FromSeconds(10),TimeSpan.FromSeconds(20),new Size(1000,100));
        CheckGraph(split.Count==2&&split[0].Length==1&&split[1].Length==2,"explicit history gap never receives a connecting line");
        var missing=points.ToArray();missing[1]=missing[1] with{CpuPercent=null};
        CheckGraph(MetricGraph.BuildSegments(missing,GraphMetric.Cpu,Now,TimeSpan.FromSeconds(10),TimeSpan.FromSeconds(20),new Size(1000,100)).Count==2,"missing CPU does not interpolate across unknown data");
        CheckGraph(MetricGraph.BuildSegments(missing,GraphMetric.Memory,Now,TimeSpan.FromSeconds(10),TimeSpan.FromSeconds(20),new Size(1000,100)).Count==1,"independent available memory history is preserved when CPU is missing");
        CheckGraph(MetricGraph.BuildSegments([new(Now.AddSeconds(-11),10,10,0,0),new(Now.AddSeconds(1),10,10,0,0)],GraphMetric.Cpu,Now,TimeSpan.FromSeconds(10),TimeSpan.FromSeconds(3),new Size(1000,100)).Count==0,"graph excludes past-window and future measurements rather than fabricating edge samples");
        return count;
    }
    static void CheckHeader(MonitorView monitor,Action<bool,string> assert)
    {
        var header=monitor.Header;var children=header.Children.OfType<FrameworkElement>().Where(x=>x.IsVisible&&x.ActualWidth>0&&x.ActualHeight>0).ToArray();
        var bounds=children.Select(x=>x.TransformToAncestor(header).TransformBounds(new Rect(x.RenderSize))).ToArray();
        bool overlap=false;for(int i=0;i<bounds.Length;i++)for(int j=i+1;j<bounds.Length;j++){var cross=Rect.Intersect(bounds[i],bounds[j]);if(!cross.IsEmpty&&cross.Width>.5&&cross.Height>.5)overlap=true;}
        assert(!overlap&&bounds.All(b=>b.Left>=-.5&&b.Right<=header.ActualWidth+.5),"measured monitor header title, search and commands stay inside the viewport without overlap");
    }
    static MonitorSnapshot Sample()
        =>CompositionFixtures.Snapshot();
    static IReadOnlyList<HistoryPoint> History()=>CompositionFixtures.History();
    static void SetGraphTime(MonitorView monitor){foreach(var graph in Descendants<MetricGraph>(monitor))graph.Now=Now;}
    static void CaptureCultures(MainWindow main,CiVisualEvidence evidence,Action<bool,string> assert)
    {
        var oldContent=main.Content;var oldCulture=CultureInfo.CurrentCulture;var oldUiCulture=CultureInfo.CurrentUICulture;
        try
        {
            foreach(string name in new[]{"ru-RU","en-US"})
            {
                CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo(name);CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(name);
                var model=new MonitorViewModel(()=>Now);using var monitor=new MonitorView(model);model.ApplySnapshot(Sample(),History());main.Content=monitor;Layout(main);SetGraphTime(monitor);
                assert(model.CpuHeader=="46%"&&model.Processes.Single(row=>row.Pid==103).CpuText==(name=="ru-RU"?"86,8%":"86.8%"),name+" actual production monitor uses whole aggregate and fractional process display roles");
                evidence.Capture(monitor,"monitor-culture-"+name,new{theme="Light",fixtureOnly=true,composition="standalone production MonitorView within existing CI HWND; not a shell comparison frame",processCount=160,sampleTimestamp=Now,sort="fixture order"});
            }
        }
        finally{main.Content=oldContent;CultureInfo.CurrentCulture=oldCulture;CultureInfo.CurrentUICulture=oldUiCulture;Layout(main);}
    }
    static double NaturalTextHeight(TextBlock actual)
    {
        var probe=new TextBlock{Text=actual.Text,FontFamily=actual.FontFamily,FontSize=actual.FontSize,FontWeight=actual.FontWeight,FontStyle=actual.FontStyle,FontStretch=actual.FontStretch};probe.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));return probe.DesiredSize.Height;
    }
    static void CheckNarrowColumns(DataGrid table,Action<bool,string> assert)
    {
        var name=table.Columns.Single(column=>column.SortMemberPath==nameof(ProcessItemViewModel.Name));
        var viewer=Descendants<ScrollViewer>(table).First();
        var viewport=Descendants<ScrollContentPresenter>(viewer).First(presenter=>presenter.Name=="PART_ScrollContentPresenter");
        double available=viewport.ActualWidth-(((int)table.HeadersVisibility&(int)DataGridHeadersVisibility.Row)!=0?table.RowHeaderActualWidth:0);
        double occupied=table.Columns.Where(column=>column.Visibility==Visibility.Visible).Sum(column=>column.ActualWidth);
        var headers=Descendants<DataGridColumnHeadersPresenter>(table).Single();
        double rawFiller=Descendants<DataGridColumnHeader>(headers).Where(header=>header.Column is null&&header.IsVisible).Select(header=>header.ActualWidth).DefaultIfEmpty(0).Max();
        // The template filler can extend behind real headers or outside the clip.
        // Its raw width is not empty space: only the uncovered viewport after the
        // sequential visible columns is relevant to the name-width regression.
        double uncovered=Math.Max(0,available-occupied);
        Console.WriteLine("NARROW_COLUMN_GEOMETRY "+System.Text.Json.JsonSerializer.Serialize(new{nameIsStar=name.Width.IsStar,viewportDip=available,visibleColumnsDip=occupied,uncoveredViewportDip=uncovered,rawTemplateFillerDip=rawFiller,verticalScrollTrackDip=Descendants<ScrollBar>(viewer).Where(bar=>bar.Orientation==Orientation.Vertical&&bar.IsVisible).Select(bar=>bar.ActualWidth).DefaultIfEmpty(0).Max()}));
        assert(!name.Width.IsStar||(available>0&&Math.Abs(available-occupied)<=1&&uncovered<=1),"default star name column consumes the measured narrow viewport without visible unused space; pixel user widths remain respected");
    }
    static void CheckStateHeader(DataGrid table,Action<bool,string> assert)
    {
        var column=table.Columns.Single(column=>column.SortMemberPath==nameof(ProcessItemViewModel.State));
        if(column.Visibility!=Visibility.Visible){assert(true,"large UI font hides an optional state column instead of displaying a clipped caption");return;}
        var header=Descendants<DataGridColumnHeader>(table).Single(element=>ReferenceEquals(element.Column,column));
        string caption=column.Header.ToString()??throw new Exception("State header caption is unavailable.");
        var text=Descendants<TextBlock>(header).Single(element=>element.Text==caption);
        var probe=new TextBlock{Text=caption,FontFamily=text.FontFamily,FontSize=text.FontSize,FontWeight=text.FontWeight,FontStyle=text.FontStyle,FontStretch=text.FontStretch};probe.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
        string? Tooltip(object? value)=>value switch{ToolTip tooltip=>tooltip.Content?.ToString(),TextBlock block=>block.Text,_=>value?.ToString()};
        bool fits=probe.DesiredSize.Width<=text.ActualWidth+.5;
        bool accessibleEllipsis=text.TextTrimming==TextTrimming.CharacterEllipsis&&(Tooltip(text.ToolTip)==caption||Tooltip(header.ToolTip)==caption);
        Console.WriteLine("STATE_HEADER_GEOMETRY "+System.Text.Json.JsonSerializer.Serialize(new{actualDip=text.ActualWidth,naturalDip=probe.DesiredSize.Width,trimming=text.TextTrimming.ToString(),fullCaptionTooltip=Tooltip(text.ToolTip)==caption||Tooltip(header.ToolTip)==caption}));
        assert(fits||accessibleEllipsis,"large UI font state caption fits its actual text area or uses ellipsis with a full-caption tooltip instead of raw clipping");
    }
    static string? Value(string[] args,string name){int i=Array.IndexOf(args,name);return i<0?null:i+1<args.Length?args[i+1]:throw new ArgumentException(name+" needs a value");}
    static void Check(bool condition,string name){if(!condition)throw new Exception("FAIL "+name);Console.WriteLine("PASS "+name);}
    static object Field(object target,string name)=>target.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(target)!;
    static TreeViewItem Node(FileBrowserView browser,string name,string path)=>(TreeViewItem)typeof(FileBrowserView).GetMethod("Node",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(browser,[name,path])!;
    static void Invoke(object target,string name)=>target.GetType().GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(target,null);
    static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,()=>frame.Continue=false);Dispatcher.PushFrame(frame);}
    static void Layout(Window window){var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(110)};var frame=new DispatcherFrame();timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};window.UpdateLayout();timer.Start();Dispatcher.PushFrame(frame);window.UpdateLayout();}
    static void Close(Window window,Application app){bool closed=false;window.Closed+=(_,_)=>closed=true;window.Close();var watch=System.Diagnostics.Stopwatch.StartNew();while(!closed||app.Windows.Cast<Window>().Contains(window)){if(watch.Elapsed>TimeSpan.FromSeconds(15))throw new TimeoutException("CI fixture did not close");Pump();}}
    static IEnumerable<T> Descendants<T>(DependencyObject root)where T:DependencyObject{for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T item)yield return item;foreach(var nested in Descendants<T>(child))yield return nested;}}
    static IEnumerable<T> Ancestors<T>(DependencyObject child)where T:DependencyObject{for(DependencyObject? parent=VisualTreeHelper.GetParent(child);parent is not null;parent=VisualTreeHelper.GetParent(parent))if(parent is T item)yield return item;}
    static T NamedPart<T>(DependencyObject root,string name)where T:FrameworkElement
    {
        var matches=Descendants<T>(root).Where(element=>element.Name==name).ToArray();
        if(matches.Length==1)return matches[0];
        var nodes=Descendants<FrameworkElement>(root).Take(80).Select(element=>new{type=element.GetType().Name,name=element.Name,width=element.ActualWidth,height=element.ActualHeight}).ToArray();
        Console.WriteLine("VISUAL_PART_DIAGNOSTIC "+System.Text.Json.JsonSerializer.Serialize(new{part=name,matches=matches.Length,nodes}));
        throw new InvalidOperationException("Expected exactly one measured template part: "+name);
    }
    sealed class AccentProbe:IDisposable
    {
        public static readonly Color ProbeColor=Color.FromRgb(145,57,193);public bool Enabled{get;set;}
        public AccentProbe()=>Theme.Changed+=Apply;
        void Apply(){if(Enabled)Application.Current.Resources["Accent"]=new SolidColorBrush(ProbeColor);}
        public void Dispose()=>Theme.Changed-=Apply;
    }
}
