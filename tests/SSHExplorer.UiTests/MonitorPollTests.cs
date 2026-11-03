using System.Collections.Concurrent;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using SSHExplorer.App;
using SSHExplorer.App.ViewModels;
using SSHExplorer.Monitoring;

internal static class MonitorPollTests
{
    public static int Run()
    {
        App? app=null;MonitorView? view=null;
        try
        {
            app=TestApplication.Create();Theme.Apply("Light");
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
            var requests=new ConcurrentQueue<Request>();int active=0,maxActive=0,calls=0,checks=0;
            void Check(bool ok,string text){if(!ok)throw new Exception(text);checks++;Console.WriteLine("PASS "+text);}
            var backend=new LinuxMonitor(async(_,ct)=>
            {
                int id=Interlocked.Increment(ref calls);int executing=Interlocked.Increment(ref active);int previous;
                do{previous=Volatile.Read(ref maxActive);if(previous>=executing)break;}while(Interlocked.CompareExchange(ref maxActive,executing,previous)!=previous);
                var request=new Request(id,ct);requests.Enqueue(request);
                using var cancellation=ct.Register(()=>request.Cancelled.TrySetResult());
                try{return id==2?await request.Complete.Task.ConfigureAwait(false):await request.Complete.Task.WaitAsync(ct).ConfigureAwait(false);}
                finally{Interlocked.Decrement(ref active);}
            }){ExpectedInterval=TimeSpan.FromMilliseconds(50)};
            var model=new MonitorViewModel();view=new MonitorView(model,backend);
            view.SetActive(true);Until(()=>calls==1);
            var first=requests.Single();Check(model.IsBusy&&active==1,"visible monitor begins one controlled collector request without SSH");
            view.SetActive(false);Until(()=>first.Cancelled.Task.IsCompleted&&!model.IsBusy&&active==0);
            Check(first.Token.IsCancellationRequested&&model.Snapshot is null,"hiding the monitor cancels its active request and releases busy state without accepting a sample");
            PumpFor(180);Check(calls==1,"hidden monitor performs no further collector calls across multiple poll intervals");
            view.SetActive(true);Until(()=>calls==2);var late=requests.Last();
            view.SetActive(false);view.SetActive(true);Until(()=>late.Cancelled.Task.IsCompleted);
            Check(calls==2&&active==1&&model.IsBusy,"rapid resume waits for an outstanding cancellation-ignoring executor instead of overlapping it");
            late.Complete.TrySetResult(new(Frame(2),"",0));Until(()=>calls>=3);var resumed=requests.Last();
            Check(model.Snapshot is null&&maxActive==1,"late response from the hidden epoch cannot replace data and resumed execution remains serialized");
            resumed.Complete.TrySetResult(new(Frame(resumed.Id),"",0));Until(()=>model.Snapshot is not null);
            Check(model.Snapshot!.System.MemoryUsedBytes==(400+resumed.Id)*1024L,"resumed monitor accepts the current real parser result rather than its discarded predecessor");
            view.SetActive(false);Until(()=>!model.IsBusy&&active==0);view.SetActive(true);Until(()=>active==1);
            var closing=requests.Last();view.Dispose();Until(()=>closing.Cancelled.Task.IsCompleted&&!model.IsBusy&&active==0);
            int closedCalls=calls;PumpFor(180);
            Check(calls==closedCalls&&closing.Token.IsCancellationRequested,"disposing an active monitor cancels collection, drains callbacks and never restarts a timer");
            Check(app.DesktopStartupCount==0&&app.Windows.Count==0&&App.Store is null,"controlled polling leaves no Window, desktop startup or ordinary settings store");
            app.Shutdown();Console.WriteLine($"RESULT: {checks} actual MonitorView hide/cancel/resume/dispose checks passed; unshown view, controlled executor, no SSH or signals");return 0;
        }
        catch(Exception ex){Console.WriteLine(ex);view?.Dispose();app?.Shutdown();return 1;}
    }
    sealed record Request(int Id,CancellationToken Token)
    {
        public TaskCompletionSource<ExecResult> Complete{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    static string Frame(int sample)
    {
        var values=new Dictionary<string,string>
        {
            ["sys/stat"]=$"cpu {100+sample*50} 0 {100+sample*50} {800+sample*100} 0 0 0 0\ncpu0 1 0 1 1\n",
            ["sys/meminfo"]=$"MemTotal: 1000 kB\nMemAvailable: {600-sample} kB\nSwapTotal: 0 kB\nSwapFree: 0 kB\n",
            ["sys/uptime"]=$"{100+sample*2} 0\n",["sys/boot_id"]="controlled-ui-poll-fixture",["meta/ticks"]="100",["meta/pages"]="4096",["meta/count"]="0",["meta/limited"]="0"
        };
        return "SSHXMON1\n"+string.Join("\n",values.Select(p=>p.Key+"\t"+Convert.ToBase64String(Encoding.UTF8.GetBytes(p.Value))))+"\nEND\n";
    }
    static void Until(Func<bool> complete)
    {
        var watch=System.Diagnostics.Stopwatch.StartNew();while(!complete()){if(watch.Elapsed>TimeSpan.FromSeconds(5))throw new TimeoutException("Controlled polling did not reach expected state");PumpFor(10);}
    }
    static void PumpFor(int milliseconds)
    {
        var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(milliseconds)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
    }
}
