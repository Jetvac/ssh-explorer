using System.Collections.Specialized;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using SSHExplorer.App.ViewModels;
using SSHExplorer.Monitoring;

internal static class MonitorStateTests
{
    public static int Run(Action<bool,string> assert)
    {
        int count=0;void Check(bool value,string label){assert(value,label);count++;}
        var now=DateTimeOffset.UnixEpoch;
        var model=new MonitorViewModel(()=>now){IsActive=true,IsBusy=true};
        Check(model.StatusText.Contains("Собираем")&&model.CpuText=="—"&&model.MemoryText=="—","MVVM initial collection has a short status and unknown values rather than zero");
        model.IsBusy=false;
        var identity=new ProcessIdentity(42,123,1000);
        var first=Snapshot(identity,now) with { System=System( null) with { CpuStatus=new(MetricState.Initial,"Первый читаемый CPU-счётчик.") } };
        model.ApplySnapshot(first,[new(now,null,40,null,null)]);
        Check(model.StatusText.Contains("Собираем")&&model.MemoryText!="—"&&model.CpuReason.Contains("Первый"),"MVVM first delta baseline preserves independently measured memory and accessible CPU reason");
        var missing=first with { Timestamp=now.AddSeconds(2),System=System(null) with { CpuStatus=new(MetricState.Unavailable,"/proc/stat: источник недоступен.") } };
        model.ApplySnapshot(missing,[]);
        Check(!model.StatusText.Contains("Собираем")&&model.CpuText=="—"&&model.CpuReason.Contains("/proc/stat")&&model.MemoryText!="—","MVVM unavailable source stops promising an endless second sample");
        var measured=Snapshot(identity,now.AddSeconds(4));model.ApplySnapshot(measured,[new(now.AddSeconds(4),25,40,100,200)]);model.SelectedIdentity=identity;
        var row=model.Processes.Single();var collection=model.Processes;int resets=0;
        collection.CollectionChanged+=(_,change)=>{if(change.Action==NotifyCollectionChangedAction.Reset)resets++;};
        Check(model.CanSignalSelected&&row.ReadBytesPerSecond==0&&row.WriteBytesPerSecond==4000&&row.ReadText!="—"&&row.ReadState==MetricState.Available,"MVVM genuine zero I/O remains numeric and signal eligibility follows a valid backend identity");
        var reasonLabel=new TextBlock{DataContext=row};reasonLabel.SetBinding(TextBlock.TextProperty,new Binding(nameof(ProcessItemViewModel.ReadReason)));
        reasonLabel.SetBinding(AutomationProperties.HelpTextProperty,new Binding(nameof(ProcessItemViewModel.ReadReason)));
        var denied=measured with { Processes=[measured.Processes[0] with {ReadBytesPerSecond=null,ReadStatus=new(MetricState.Unavailable,"/proc/PID/io: нет прав чтения.")}] };
        model.ApplySnapshot(denied,model.History);
        Check(ReferenceEquals(row,model.Processes.Single())&&row.ReadText=="—"&&reasonLabel.Text.Contains("нет прав")&&AutomationProperties.GetHelpText(reasonLabel).Contains("нет прав"),"MVVM metric reason updates real unshown WPF and automation bindings without replacing the process row");
        now=now.AddSeconds(6);model.ReportFailure("Истекло время ожидания запроса.");
        Check(model.IsStale&&model.HasError&&model.StatusText.Contains("устарели")&&model.StatusDetails.Contains("Истекло")&&!model.CanSignalSelected&&row.CpuState==MetricState.Stale&&row.Cpu==10,"MVVM failed sampling retains values and disables signals while explicitly marking stale data");
        Check(model.History[^1].IsGap&&model.History[^1].Timestamp==now&&model.History[^1].CpuPercent is null,"MVVM failure inserts a timestamped gap instead of a fabricated zero");
        var recovered=Snapshot(identity,now.AddSeconds(2));model.ApplySnapshot(recovered,[new(now.AddSeconds(2),25,40,100,200)]);
        Check(!model.IsStale&&!model.HasError&&model.CanSignalSelected&&ReferenceEquals(collection,model.Processes)&&ReferenceEquals(row,model.Processes.Single())&&model.SelectedIdentity==identity&&resets==0,"MVVM recovery preserves collection row identity and selection without table reset");
        now=now.AddSeconds(3);model.IsActive=false;
        Check(model.StatusText.StartsWith("Пауза")&&model.History[^1].IsGap&&model.History[^1].Timestamp==now&&!model.CanSignalSelected,"MVVM pause keeps actual time and disables actions against paused data");
        model.IsActive=true;Check(model.StatusText.Contains("Собираем"),"MVVM resumed section requests a new baseline instead of calling paused data current");
        model.IsBusy=true;Check(!model.CanSignalSelected&&model.SignalDisabledReason.Contains("замера"),"MVVM busy collection explains why signalling is temporarily disabled");model.IsBusy=false;
        foreach(var blocked in new[]{new ProcessIdentity(1,123,1000),new ProcessIdentity(42,0,1000),new ProcessIdentity(42,123,uint.MaxValue)})
        { model.ApplySnapshot(Snapshot(blocked,now),[]);model.SelectedIdentity=blocked;Check(!model.CanSignalSelected&&!string.IsNullOrWhiteSpace(model.SignalDisabledReason),"MVVM signal policy rejects protected or unverifiable identity "+blocked.Pid+"/"+blocked.StartTimeTicks+"/"+blocked.UserId); }
        var history=new HistoryPoint[]{new(now,10,40,null,null),new(now.AddSeconds(2),null,null,null,null){IsGap=true,Reason="пауза"},new(now.AddSeconds(23),20,45,null,null)};
        model.ApplySnapshot(Snapshot(identity,now.AddSeconds(23)),history);
        Check(model.History.Select(p=>p.Timestamp).SequenceEqual(history.Select(p=>p.Timestamp))&&model.History[1].IsGap&&model.CpuHistory[1] is null,"MVVM forwards real timestamps and null discontinuities without index-based resampling");
        Check(model.DataDetails.Contains("/proc/uptime")&&model.DataDetails.Contains("Длительность")&&!model.StatusText.Contains("/proc"),"MVVM compact status keeps technical sources and collection duration in separate details");
        return count;
    }
    static SystemMetric System(double? cpu)=>new(cpu,2,1000,600,400,0,0,TimeSpan.FromMinutes(5),.1,.2,.3,100,false);
    static MonitorSnapshot Snapshot(ProcessIdentity identity,DateTimeOffset timestamp)=>new(timestamp,System(25),
        [new ProcessMetric(identity,"worker","1000","S",10,100,0,4000,null)],[],[],[],[]){CollectionDuration=TimeSpan.FromMilliseconds(123)};
}
