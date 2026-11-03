using SSHExplorer.Monitoring;

internal static class MetricStateTests
{
    public static async Task RunAsync(TestSuite t)
    {
        static MonitorSnapshot Calculate(MonitorCalculator calc,int n,Action<Dictionary<string,string?>>? change=null,ulong start=777)
        { var frames=MonitorTests.SampleFrames(n,start);change?.Invoke(frames);return calc.Calculate(MonitorTests.Protocol(frames),DateTimeOffset.UnixEpoch.AddSeconds(n*2)); }
        await t.Run("metric states distinguish first sample, measured CPU and genuine zero throughput",()=>
        {
            var calc=new MonitorCalculator();var first=Calculate(calc,0);
            TestSuite.Equal(MetricState.Initial,first.System.CpuStatus.State);TestSuite.Equal(MetricState.Available,first.System.MemoryStatus.State);
            TestSuite.Equal(MetricState.Initial,first.Processes[0].ReadStatus.State);
            var second=Calculate(calc,1,f=>f["p/42/io"]="read_bytes: 100\nwrite_bytes: 200\n");
            TestSuite.Near(50,second.System.CpuPercent);TestSuite.Equal(MetricState.Available,second.System.CpuStatus.State);
            TestSuite.Near(0,second.Processes[0].ReadBytesPerSecond);TestSuite.Equal(MetricState.Available,second.Processes[0].ReadStatus.State);
        });
        await t.Run("persistently missing uptime preserves CPU and memory while rates explain unavailable clock",()=>
        {
            var calc=new MonitorCalculator();Calculate(calc,0,f=>f["sys/uptime"]=null);
            for(int n=1;n<=3;n++)
            {
                var value=Calculate(calc,n,f=>f["sys/uptime"]=null);
                TestSuite.Near(50,value.System.CpuPercent);TestSuite.Near(5,value.Processes[0].CpuPercent);
                TestSuite.Equal(MetricState.Available,value.System.MemoryStatus.State);
                TestSuite.Equal<double?>(null,value.Processes[0].ReadBytesPerSecond);
                TestSuite.Equal(MetricState.Unavailable,value.Processes[0].ReadStatus.State);
                TestSuite.True(value.Processes[0].ReadStatus.Reason.Contains("uptime"));
                TestSuite.Equal(MetricState.Unavailable,value.Networks[0].ReceiveStatus.State);
            }
        });
        await t.Run("unreadable CPU source preserves independent memory and elapsed-time throughput",()=>
        {
            var calc=new MonitorCalculator();Calculate(calc,0);
            var value=Calculate(calc,1,f=>f["sys/stat"]=null);
            TestSuite.Equal(MetricState.Unavailable,value.System.CpuStatus.State);TestSuite.Equal<double?>(null,value.Processes[0].CpuPercent);
            TestSuite.Equal(MetricState.Unavailable,value.Processes[0].CpuStatus.State);TestSuite.Near(1000,value.Networks[0].ReceiveBytesPerSecond);
            TestSuite.Equal(MetricState.Available,value.System.MemoryStatus.State);TestSuite.Near(1000,value.Processes[0].ReadBytesPerSecond);
        });
        await t.Run("I/O denied does not promise second sample and readable source recovers through its own baseline",()=>
        {
            var calc=new MonitorCalculator();Calculate(calc,0,f=>f["p/42/io"]=null);
            var denied=Calculate(calc,1,f=>f["p/42/io"]=null);
            TestSuite.Equal(MetricState.Unavailable,denied.Processes[0].ReadStatus.State);TestSuite.True(denied.Processes[0].ReadStatus.Reason.Contains("/proc/PID/io"));
            TestSuite.Near(5,denied.Processes[0].CpuPercent);
            var readable=Calculate(calc,2);TestSuite.Equal(MetricState.Initial,readable.Processes[0].ReadStatus.State);
            var measured=Calculate(calc,3);TestSuite.Equal(MetricState.Available,measured.Processes[0].ReadStatus.State);TestSuite.Near(1000,measured.Processes[0].ReadBytesPerSecond);
        });
        await t.Run("new process differs from missing source then transitions to a measured delta",()=>
        {
            var calc=new MonitorCalculator();Calculate(calc,0);
            var next=Calculate(calc,1,start:999);
            TestSuite.Equal(MetricState.NewProcess,next.Processes[0].CpuStatus.State);TestSuite.Equal(MetricState.NewProcess,next.Processes[0].ReadStatus.State);
            TestSuite.Equal(MetricState.Available,next.System.CpuStatus.State);
            var measured=Calculate(calc,2,start:999);TestSuite.Equal(MetricState.Available,measured.Processes[0].CpuStatus.State);TestSuite.Near(5,measured.Processes[0].CpuPercent);
        });
        await t.Run("reboot states are reset rather than unavailable and recover after a new baseline",()=>
        {
            var calc=new MonitorCalculator();Calculate(calc,0);
            var reset=Calculate(calc,1,f=>f["sys/boot_id"]="new-boot");
            TestSuite.Equal(MetricState.Reset,reset.System.CpuStatus.State);TestSuite.Equal(MetricState.Reset,reset.Processes[0].ReadStatus.State);
            TestSuite.Equal(MetricState.Available,reset.System.MemoryStatus.State);
            var next=Calculate(calc,2,f=>f["sys/boot_id"]="new-boot");TestSuite.Equal(MetricState.Available,next.System.CpuStatus.State);
        });
        await t.Run("CPU counter reset does not erase independent network counters",()=>
        {
            var calc=new MonitorCalculator();Calculate(calc,1);
            var sources=MonitorTests.SampleFrames(2);
            var reset=Calculate(calc,2,f=>f["sys/stat"]=MonitorTests.SampleFrames(0)["sys/stat"]);
            TestSuite.Equal(MetricState.Reset,reset.System.CpuStatus.State);TestSuite.Equal<double?>(null,reset.Processes[0].CpuPercent);
            TestSuite.Equal(MetricState.Available,reset.Networks[0].ReceiveStatus.State);TestSuite.Near(1000,reset.Networks[0].ReceiveBytesPerSecond);
        });
        await t.Run("unavailable owner disables signal without losing independently readable CPU",()=>
        {
            var calc=new MonitorCalculator();Calculate(calc,0);
            var next=Calculate(calc,1,f=>f["p/42/status"]=null);
            TestSuite.Near(5,next.Processes[0].CpuPercent);TestSuite.True(!next.Processes[0].CanSignal);
        });
        await t.Run("backend and process metric use one signal identity policy and invalid identities never execute",async()=>
        {
            int calls=0;var monitor=new LinuxMonitor((_,_)=>{calls++;return Task.FromResult(new ExecResult("","",0));});
            foreach(var identity in new[]{new ProcessIdentity(1,777,1000),new ProcessIdentity(42,0,1000),new ProcessIdentity(42,777,uint.MaxValue)})
            {
                TestSuite.True(!ProcessSignalPolicy.CanSignal(identity));
                await TestSuite.ThrowsAsync<InvalidOperationException>(()=>monitor.SignalAsync(identity,15));
            }
            TestSuite.Equal(0,calls);
        });
        await t.Run("failed sampling retains actual timestamps and a gap instead of zero then resets baseline",async()=>
        {
            var now=DateTimeOffset.UnixEpoch;int call=0;
            var monitor=new LinuxMonitor((_,_)=>++call==2?throw new IOException("simulated read failure"):Task.FromResult(new ExecResult(MonitorTests.Sample(call-1),"",0)),clock:()=>now);
            await monitor.SampleAsync();now=now.AddSeconds(2);await TestSuite.ThrowsAsync<IOException>(()=>monitor.SampleAsync());now=now.AddSeconds(2);
            var restored=await monitor.SampleAsync();var history=monitor.History;
            TestSuite.Equal(3,history.Count);TestSuite.Equal(DateTimeOffset.UnixEpoch.AddSeconds(2),history[1].Timestamp);
            TestSuite.True(history[1].IsGap && history[1].MemoryPercent is null);TestSuite.Equal(MetricState.Reset,restored.System.CpuStatus.State);
            TestSuite.Near(40,restored.System.MemoryPercent);
        });
        await t.Run("pause invalidates an in-flight history point and next collection starts a new baseline",async()=>
        {
            var now=DateTimeOffset.UnixEpoch;int call=0;
            var pending=new TaskCompletionSource<ExecResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var monitor=new LinuxMonitor((_,_)=>++call==2?pending.Task:Task.FromResult(new ExecResult(MonitorTests.Sample(call-1),"",0)),clock:()=>now);
            await monitor.SampleAsync();now=now.AddSeconds(2);var sample=monitor.SampleAsync();now=now.AddSeconds(1);monitor.MarkGap(now,"paused");
            now=now.AddSeconds(1);pending.SetResult(new ExecResult(MonitorTests.Sample(1),"",0));await sample;
            TestSuite.Equal(2,monitor.History.Count);TestSuite.True(monitor.History[^1].IsGap);
            now=now.AddSeconds(2);var resumed=await monitor.SampleAsync();TestSuite.Equal(MetricState.Reset,resumed.System.CpuStatus.State);
            TestSuite.Equal(DateTimeOffset.UnixEpoch.AddSeconds(6),monitor.History[^1].Timestamp);
        });
        await t.Run("slow SSH sample records actual collection duration and a timestamped history discontinuity",async()=>
        {
            var now=DateTimeOffset.UnixEpoch;int call=0;
            var monitor=new LinuxMonitor((_,_)=>{call++;if(call==2)now=now.AddSeconds(7);return Task.FromResult(new ExecResult(MonitorTests.Sample(call-1),"",0));},clock:()=>now);
            await monitor.SampleAsync();now=now.AddSeconds(2);var next=await monitor.SampleAsync();var history=monitor.History;
            TestSuite.Equal(TimeSpan.FromSeconds(7),next.CollectionDuration);TestSuite.Equal(3,history.Count);
            TestSuite.Equal(DateTimeOffset.UnixEpoch.AddSeconds(2),history[1].Timestamp);TestSuite.True(history[1].IsGap);
            TestSuite.Equal(DateTimeOffset.UnixEpoch.AddSeconds(9),history[2].Timestamp);TestSuite.Near(50,next.System.CpuPercent);
        });
        await t.Run("one missed polling interval is a gap even below the previous two-interval threshold",async()=>
        {
            var now=DateTimeOffset.UnixEpoch;int call=0;
            var monitor=new LinuxMonitor((_,_)=>{call++;if(call==2)now=now.AddMilliseconds(2100);return Task.FromResult(new ExecResult(MonitorTests.Sample(call-1),"",0));},clock:()=>now)
                {ExpectedInterval=TimeSpan.FromSeconds(2)};
            await monitor.SampleAsync();now=now.AddSeconds(2);await monitor.SampleAsync();
            var history=monitor.History;TestSuite.Equal(3,history.Count);TestSuite.True(history[1].IsGap);
            TestSuite.Equal(DateTimeOffset.UnixEpoch.AddSeconds(2),history[1].Timestamp);
            TestSuite.Equal(DateTimeOffset.UnixEpoch.AddMilliseconds(4100),history[2].Timestamp);
        });
    }
}
