using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using SSHExplorer.App;
using SSHExplorer.App.ViewModels;
using SSHExplorer.Core;
using SSHExplorer.Monitoring;

internal static class MonitorRefinementTests
{
    public static int Run(Action<bool,string> assert)
    {
        int count=0;void Check(bool value,string name){assert(value,name);count++;}
        foreach(var cultureName in new[]{"ru-RU","en-US"})
        {
            var original=CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo(cultureName);bool ru=cultureName=="ru-RU";
                Check(MetricFormatter.AggregatePercent(12.34)=="12%"&&MetricFormatter.ProcessPercent(12.34)==(ru?"12,3%":"12.3%"),cultureName+" production formatter distinguishes aggregate and process percentage without modifying precision");
                Check(MetricFormatter.Size(1536)==(ru?"1,5 КБ":"1.5 KB")&&MetricFormatter.Speed(1536)==(ru?"1,5 КБ/с":"1.5 KB/s"),cultureName+" production size and rate use culture-aware decimal and unit roles");
                Check(MetricFormatter.ProcessPercent(null)=="—"&&MetricFormatter.Speed(double.NaN)=="—"&&MetricFormatter.Speed(0)==(ru?"0 Б/с":"0 B/s"),cultureName+" unknown values remain distinct from a genuine zero");
                var time=new DateTimeOffset(2026,11,3,13,24,25,TimeSpan.Zero);
                Check(MetricFormatter.Time(time)==time.LocalDateTime.ToString("T",CultureInfo.CurrentCulture)&&MetricFormatter.Number(.25)==(ru?"0,25":"0.25"),cultureName+" time and scalar metrics follow regional settings");
                var model=Model();Check(model.CpuHeader=="12%"&&model.Processes[0].CpuText==(ru?"10,1%":"10.1%")&&model.Processes[0].Cpu==10.12345,cultureName+" production view-model uses display roles while preserving raw measurements");
            }
            finally{CultureInfo.CurrentCulture=original;}
        }
        var rows=new[]{Row(1,null,1000,null),Row(2,0,2_000_000,0),Row(3,13.15,100_000,5000),Row(4,13.14,null,30)};
        Check(Order(rows,"Cpu",ListSortDirection.Ascending).SequenceEqual(new[]{2,4,3,1})&&Order(rows,"Cpu",ListSortDirection.Descending).SequenceEqual(new[]{3,4,2,1}),"numeric CPU sorting keeps full precision and puts unknown last in both directions");
        Check(Order(rows,"Read",ListSortDirection.Ascending).SequenceEqual(new[]{2,4,3,1})&&Order(rows,"Memory",ListSortDirection.Descending).SequenceEqual(new[]{2,3,1,4}),"rate and memory sorting compare numeric values instead of displayed units or unknown placeholders");
        var pref=new MonitorTablePreferences();Check(pref["Pid"].Visible&&!pref.EffectiveVisible("Pid",500)&&pref.EffectiveVisible("Pid",1000),"responsive hiding leaves the preferred visibility intact and restores it when the viewport widens");
        pref.SetVisible("User",true);pref.SetWidth("Name",231.5,MonitorColumnWidthMode.Pixel);pref.SetWidth("Read",137,MonitorColumnWidthMode.Pixel);
        pref.SetOrder(new[]{"Name","Memory","Cpu","User","Read","Pid","State","Write"});pref.SetSort("Read",ListSortDirection.Descending);
        string directory=System.IO.Path.Combine(Environment.CurrentDirectory,".tools","monitor-pref-tests",Guid.NewGuid().ToString("N"));
        var store=new AppStore(directory);store.Settings.Monitor=pref.Snapshot();store.SaveAsync().GetAwaiter().GetResult();
        var restored=new MonitorTablePreferences(new AppStore(directory).Settings.Monitor);
        Check(restored["User"].Visible&&restored["User"].ExplicitVisibility&&restored["Name"].Width==231.5&&restored["Name"].WidthMode==MonitorColumnWidthMode.Pixel
            &&restored["Memory"].Order==1&&restored.SortColumn=="Read"&&restored.SortDirection==ListSortDirection.Descending,"new AppStore restores explicit visibility order width mode width and sorting from the normal versioned settings format");
        Check(restored["Pid"].Visible&&!restored.EffectiveVisible("Pid",500)&&restored.EffectiveVisible("Pid",1000)&&restored.EffectiveVisible("User",500),"restored preferences distinguish temporary responsive hiding from explicit user visibility");
        var bad=new MonitorPreferences{Columns=[new(){Key="Name",Visible=false,Width=double.NaN,Order=999},new(){Key="unknown",Visible=true,Width=10}],SortColumn="unknown"};
        var safe=new MonitorTablePreferences(bad);
        Check(safe["Name"].Visible&&safe["Name"].WidthMode==MonitorColumnWidthMode.Star&&safe.SortColumn is null&&safe.Snapshot().Columns.Count==8,"malformed or future monitor preferences fall back without hiding mandatory columns or serializing invalid widths");
        Check(MetricFormatter.ProcessState("R")=="Выполняется"&&MetricFormatter.ProcessState("S")=="Ожидание"&&MetricFormatter.ProcessState("D")=="Ожидание I/O","representative process states use the same production conversion as ProcParser");
        CommandChecks(Check);
        return count;
    }
    static void CommandChecks(Action<bool,string> check)
    {
        var model=Model();model.IsBusy=true;var first=model.Processes[0].Metric.Identity;model.SelectedIdentity=first;
        check(model.CanSignalSelected,"background polling leaves a verified selected process command available");
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var confirm=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int sent=0;ProcessIdentity? received=null;
        var operation=Task.Run(()=>model.SignalSelectedAsync(15,(_,_)=>{entered.SetResult();return confirm.Task;},(identity,_,_)=>{received=identity;Interlocked.Increment(ref sent);return Task.CompletedTask;}));
        entered.Task.GetAwaiter().GetResult();
        check(model.IsCommandRunning&&!model.CanSignalSelected,"a pending process command has its own gate independent of background collection");
        bool second=model.SignalSelectedAsync(15,(_,_)=>Task.FromResult(true),(_,_,_)=>{Interlocked.Increment(ref sent);return Task.CompletedTask;}).GetAwaiter().GetResult();
        model.SelectedIdentity=model.Processes[1].Metric.Identity;confirm.SetResult(true);operation.GetAwaiter().GetResult();
        check(!second&&sent==1&&received==first&&!model.IsCommandRunning,"repeated command is ignored and changing selection cannot retarget the already confirmed process identity");
        var changed=Model();changed.SelectedIdentity=changed.Processes[0].Metric.Identity;
        var waiting=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var approval=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);int rejectedSent=0;
        var stale=Task.Run(()=>changed.SignalSelectedAsync(15,(_,_)=>{waiting.SetResult();return approval.Task;},(_,_,_)=>{rejectedSent++;return Task.CompletedTask;}));
        waiting.Task.GetAwaiter().GetResult();var original=changed.Snapshot!;
        changed.ApplySnapshot(original with{Processes=[original.Processes[0] with{Identity=new(42,999,1000)},original.Processes[1]]},[]);approval.SetResult(true);
        bool rejected=false;try{stale.GetAwaiter().GetResult();}catch(ProcessChangedException){rejected=true;}
        check(rejected&&rejectedSent==0&&!changed.IsCommandRunning,"identity is rechecked after confirmation and a reused PID never reaches the signal executor");
        var failed=Model();failed.SelectedIdentity=failed.Processes[0].Metric.Identity;failed.ReportFailure("test unavailable source");
        bool result=failed.SignalSelectedAsync(15,(_,_)=>Task.FromResult(true),(_,_,_)=>throw new Exception("Stale data must not dispatch")).GetAwaiter().GetResult();
        check(!result&&!failed.IsCommandRunning,"stale snapshots still block commands even though routine polling no longer does");
        var cancelled=Model();cancelled.SelectedIdentity=cancelled.Processes[0].Metric.Identity;using var lifetime=new CancellationTokenSource();
        var prompted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var late=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);int lateSent=0;
        var pending=Task.Run(()=>cancelled.SignalSelectedAsync(15,(_,_)=>{prompted.SetResult();return late.Task;},(_,_,_)=>{lateSent++;return Task.CompletedTask;},lifetime.Token));
        prompted.Task.GetAwaiter().GetResult();lifetime.Cancel();bool wasCancelled=false;try{pending.GetAwaiter().GetResult();}catch(OperationCanceledException){wasCancelled=true;}late.SetResult(true);
        check(wasCancelled&&!cancelled.IsCommandRunning&&lateSent==0,"cancelled confirmation releases its command gate and a late approval cannot dispatch a signal");
    }
    public static int RunUiWindow(Window host,Action<bool,string> assert)
    {
        CiVisualEvidence.RequireCi();int count=0;void Check(bool value,string name){assert(value,name);count++;}
        object? previous=host.Content;MonitorPreferences saved=new();var model=Model();
        using var monitor=new MonitorView(model,saved,value=>saved=value);using var unchanged=new MonitorView(Model(),new MonitorPreferences(),value=>saved=value);host.Content=monitor;host.Width=1000;host.Height=700;Layout(host);
        var table=monitor.ProcessTable;var cpu=table.Columns.Single(c=>c.SortMemberPath==nameof(ProcessItemViewModel.Cpu));
        var header=Children<DataGridColumnHeader>(table).Single(h=>ReferenceEquals(h.Column,cpu));
        typeof(DataGridColumnHeader).GetMethod("OnClick",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(header,null);Layout(host);
        Check(cpu.SortDirection==ListSortDirection.Ascending&&saved.SortColumn=="Cpu"&&saved.SortDirection==ListSortDirection.Ascending,"actual WPF column-header OnClick applies and persists ascending numeric sort with its indicator");
        typeof(DataGridColumnHeader).GetMethod("OnClick",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(header,null);Layout(host);
        Check(cpu.SortDirection==ListSortDirection.Descending&&saved.SortDirection==ListSortDirection.Descending,"actual WPF column-header second invocation toggles descending and the matching sort indicator");
        var sortedView=(INotifyCollectionChanged)CollectionViewSource.GetDefaultView(table.ItemsSource);int resets=0;
        NotifyCollectionChangedEventHandler countReset=(_,change)=>{if(change.Action==NotifyCollectionChangedAction.Reset)resets++;};sortedView.CollectionChanged+=countReset;
        table.SelectedItem=model.Processes[0];Layout(host);table.SelectedItem=null;Layout(host);sortedView.CollectionChanged-=countReset;
        Check(resets==0,"selecting and deselecting a process does not reset its sorted collection or replace the metric cell visuals");
        string beforeResize=System.Text.Json.JsonSerializer.Serialize(saved);host.Width=760;Layout(host);host.Width=1000;Layout(host);
        Check(System.Text.Json.JsonSerializer.Serialize(saved)==beforeResize&&saved.Columns.Single(c=>c.Key=="Name").WidthMode==MonitorColumnWidthMode.Star,"viewport resize and deferred star-name reflow cannot overwrite user visibility or persist measured pixels as width intent");
        var selected=model.Processes[0];model.SelectedIdentity=selected.Metric.Identity;var updated=model.Snapshot!;
        model.ApplySnapshot(updated with{Processes=[updated.Processes[0] with{CpuPercent=90},updated.Processes[1]]},[]);Layout(host);
        Check(ReferenceEquals(table.SelectedItem,selected)&&CollectionViewSource.GetDefaultView(table.ItemsSource).Cast<ProcessItemViewModel>().First().Pid==42,"sorted updates retain the selected row object and refresh numeric order");
        var memory=table.Columns.Single(c=>c.SortMemberPath==nameof(ProcessItemViewModel.MemoryBytes));memory.Width=173.5;memory.DisplayIndex=1;Layout(host);
        Check(saved.Columns.Single(c=>c.Key=="Memory").Width==173.5&&saved.Columns.Single(c=>c.Key=="Memory").Order==1,"actual DataGrid width and display-index changes update persistent preferences");
        var menu=(ContextMenu)typeof(MonitorView).GetMethod("BuildOverflow",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(monitor,null)!;
        var choose=menu.Items.OfType<MenuItem>().Single(i=>Equals(i.Header,"Выбрать столбцы"));var user=choose.Items.OfType<MenuItem>().Single(i=>Equals(i.Header,"Пользователь"));
        user.IsChecked=true;user.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(saved.Columns.Single(c=>c.Key=="User").Visible&&saved.Columns.Single(c=>c.Key=="User").ExplicitVisibility,"the real Choose columns menu records explicit visibility independently of responsive hiding");
        unchanged.Dispose();Check(saved.SortColumn=="Cpu"&&saved.SortDirection==ListSortDirection.Descending&&saved.Columns.Single(c=>c.Key=="User").Visible,"closing an untouched older session cannot overwrite newer shared monitor preferences");
        using var reopened=new MonitorView(Model(),saved,_=>{});host.Content=reopened;Layout(host);
        var restoredMemory=reopened.ProcessTable.Columns.Single(c=>c.SortMemberPath==nameof(ProcessItemViewModel.MemoryBytes));
        Check(restoredMemory.Width.IsAbsolute&&restoredMemory.Width.Value==173.5&&restoredMemory.DisplayIndex==1&&reopened.ProcessTable.Columns.Single(c=>c.SortMemberPath==nameof(ProcessItemViewModel.Cpu)).SortDirection==ListSortDirection.Descending,"a new MonitorView restores width order and sort instead of resetting the column layout");
        host.Content=previous;return count;
    }
    static MonitorViewModel Model()
    {
        var model=new MonitorViewModel{IsActive=true};var identity=new ProcessIdentity(42,123,1000);
        model.ApplySnapshot(new(DateTimeOffset.UnixEpoch,new SystemMetric(12.34,2,1000,600,400,0,0,TimeSpan.FromMinutes(2),.25,0,0,100,false),
            [new(identity,"worker","1000",MetricFormatter.ProcessState("S"),10.12345,100,0,4000,null),new(new(43,124,1000),"running","1000",MetricFormatter.ProcessState("R"),20,200,null,0,null)],[],[],[],[]),[]);
        return model;
    }
    static ProcessItemViewModel Row(int pid,double? cpu,long? memory,double? rate)=>new(new(new(pid,123,1000),"p"+pid,"1000","Ожидание",cpu,memory,rate,rate,null),1000);
    static int[] Order(ProcessItemViewModel[] rows,string key,ListSortDirection direction){object[] values=rows;values=(object[])values.Clone();Array.Sort(values,new ProcessSortComparer(key,direction));return values.Cast<ProcessItemViewModel>().Select(r=>r.Pid).ToArray();}
    static void Layout(Window host){host.UpdateLayout();var frame=new DispatcherFrame();host.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,()=>frame.Continue=false);Dispatcher.PushFrame(frame);host.UpdateLayout();}
    static IEnumerable<T> Children<T>(DependencyObject root)where T:DependencyObject{for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T item)yield return item;foreach(var nested in Children<T>(child))yield return nested;}}
}
