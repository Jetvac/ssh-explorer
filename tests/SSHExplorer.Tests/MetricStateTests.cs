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
        await t.Run("slow but successful samples form one continuous series with real timestamps",async()=>
        {
            // Reference server: collection takes 2.8 s while the requested interval is 2 s.
            var now=DateTimeOffset.UnixEpoch;int call=0;
            var monitor=new LinuxMonitor((_,_)=>{call++;now=now.AddMilliseconds(2800);return Task.FromResult(new ExecResult(MonitorTests.Sample(call-1),"",0));},clock:()=>now){ExpectedInterval=TimeSpan.FromSeconds(2)};
            for(int i=0;i<6;i++){await monitor.SampleAsync();now=now.AddMilliseconds(250);}
            var history=monitor.History;
            TestSuite.Equal(6,history.Count);TestSuite.True(history.All(p=>!p.IsGap));
            TestSuite.True(history[0].ContinuesSeries==false&&history.Skip(1).All(p=>p.ContinuesSeries==true));
            TestSuite.Equal(DateTimeOffset.UnixEpoch.AddMilliseconds(2800),history[0].Timestamp);
            TestSuite.Equal(TimeSpan.FromMilliseconds(2800),history[^1].CollectionDuration);
        });
        await t.Run("a real failure in a slow series keeps an explicit gap and recovery starts a new segment",async()=>
        {
            var now=DateTimeOffset.UnixEpoch;int call=0;
            var monitor=new LinuxMonitor((_,_)=>{call++;now=now.AddMilliseconds(2500);if(call==3)throw new IOException("channel lost");return Task.FromResult(new ExecResult(MonitorTests.Sample(call),"",0));},clock:()=>now){ExpectedInterval=TimeSpan.FromSeconds(1)};
            await monitor.SampleAsync();await monitor.SampleAsync();await TestSuite.ThrowsAsync<IOException>(()=>monitor.SampleAsync());await monitor.SampleAsync();await monitor.SampleAsync();
            var history=monitor.History;
            TestSuite.Equal(5,history.Count);TestSuite.True(history[2].IsGap);
            TestSuite.True(history[1].ContinuesSeries==true&&history[3].ContinuesSeries==false&&history[4].ContinuesSeries==true);
        });
        await t.Run("silence longer than the continuity limit inserts a reasoned gap without a fabricated value",async()=>
        {
            var now=DateTimeOffset.UnixEpoch;int call=0;
            var monitor=new LinuxMonitor((_,_)=>{call++;return Task.FromResult(new ExecResult(MonitorTests.Sample(call),"",0));},clock:()=>now){ExpectedInterval=TimeSpan.FromSeconds(2)};
            await monitor.SampleAsync();now=now.AddSeconds(2);await monitor.SampleAsync();now=now.AddSeconds(30);await monitor.SampleAsync();
            var history=monitor.History;
            TestSuite.Equal(4,history.Count);TestSuite.True(history[2].IsGap&&history[2].CpuPercent is null&&history[2].Reason is not null);
            TestSuite.Equal(DateTimeOffset.UnixEpoch.AddSeconds(2)+monitor.ContinuityLimit,history[2].Timestamp);
            TestSuite.True(history[3].ContinuesSeries==false);
        });
        await t.Run("history covers the time window at one-second polling instead of a fixed 120 points",async()=>
        {
            var now=DateTimeOffset.UnixEpoch;int call=0;
            var monitor=new LinuxMonitor((_,_)=>{call++;return Task.FromResult(new ExecResult(MonitorTests.Sample(call),"",0));},clock:()=>now){ExpectedInterval=TimeSpan.FromSeconds(1),HistoryWindow=TimeSpan.FromMinutes(5)};
            for(int i=0;i<400;i++){await monitor.SampleAsync();now=now.AddSeconds(1);}
            var history=monitor.History;
            TestSuite.True(history.Count>=300&&history.Count<=302);
            TestSuite.True(history[^1].Timestamp-history[0].Timestamp>=TimeSpan.FromMinutes(4.9));
        });
        await t.Run("system scope sends no per-process collection and process scope reads command lines once",async()=>
        {
            var commands=new List<string>();int call=0;var now=DateTimeOffset.UnixEpoch;
            // The fixture answers like the real collector: only the requested blocks are present.
            string Answer(string command,int n){var frames=MonitorTests.SampleFrames(n);if(!command.Contains("/etc/passwd",StringComparison.Ordinal))frames.Remove("sys/passwd");if(!command.Contains("/proc/[0-9]*",StringComparison.Ordinal))foreach(var key in frames.Keys.Where(k=>k.StartsWith("p/",StringComparison.Ordinal)||k is "meta/count" or "meta/limited").ToArray())frames.Remove(key);return MonitorTests.Protocol(frames);}
            var monitor=new LinuxMonitor((command,_)=>{commands.Add(command);call++;return Task.FromResult(new ExecResult(Answer(command,call),"",0));},clock:()=>now){Scope=MonitorScope.System};
            await monitor.SampleAsync();now=now.AddSeconds(2);
            TestSuite.True(!commands[0].Contains("/proc/[0-9]*",StringComparison.Ordinal)&&!commands[0].Contains("/etc/passwd",StringComparison.Ordinal));
            monitor.Scope=MonitorScope.Processes;await monitor.SampleAsync();now=now.AddSeconds(2);await monitor.SampleAsync();
            TestSuite.True(commands[1].Contains("/proc/[0-9]*",StringComparison.Ordinal)&&commands[1].Contains("/etc/passwd",StringComparison.Ordinal));
            TestSuite.True(!commands[2].Contains("/etc/passwd",StringComparison.Ordinal));
        });
        await t.Run("protocol 2 collector runs on a real Linux /proc and keeps process identity (Linux hosts only)",async()=>
        {
            if(!OperatingSystem.IsLinux()||!File.Exists("/bin/sh")){Console.WriteLine("SKIP real /proc collector: not a Linux host");return;}
            async Task<ExecResult> Run(string command,CancellationToken ct)
            {
                var info=new System.Diagnostics.ProcessStartInfo("/bin/sh"){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false};info.ArgumentList.Add("-c");info.ArgumentList.Add(command);
                using var process=System.Diagnostics.Process.Start(info)!;var output=process.StandardOutput.ReadToEndAsync(ct);var error=process.StandardError.ReadToEndAsync(ct);await process.WaitForExitAsync(ct);
                return new ExecResult(await output,await error,process.ExitCode);
            }
            var monitor=new LinuxMonitor(Run);
            var first=await monitor.SampleAsync();MonitorSnapshot second=first;
            for(int i=0;i<4&&!second.Processes.Any(p=>p.Identity.Pid==Environment.ProcessId&&p.CommandLine is not null);i++){await Task.Delay(200);second=await monitor.SampleAsync();}
            var self=Environment.ProcessId;
            TestSuite.True(first.Processes.Count>0&&second.Processes.Any(p=>p.Identity.Pid==self));
            var me=second.Processes.Single(p=>p.Identity.Pid==self);
            TestSuite.True(me.MemoryBytes>0&&me.CommandLine is not null&&me.User.Length>0);
            TestSuite.True(second.System.CpuPercent is not null&&second.System.MemoryPercent is not null);
            Console.WriteLine($"INFO real collector: {second.Processes.Count} processes, {second.CollectionDuration.TotalMilliseconds:0} ms");
        });
    }
}
