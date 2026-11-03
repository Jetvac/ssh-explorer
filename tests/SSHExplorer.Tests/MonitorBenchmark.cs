#if CORE_TESTS
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using SSHExplorer.Core;
using SSHExplorer.Monitoring;

internal static class MonitorBenchmark
{
    public static async Task<int> RunAsync(string configurationFile,string? reportFile=null)
    {
        object report;int result;
        try
        {
            await using var session=await OpenSessionAsync(configurationFile);
            // Fixed collector/probe only: no commands, names or credentials are written to the report.
            var monitor=new LinuxMonitor(async(command,ct)=>{var value=await session.ExecuteAsync(command,ct);return new ExecResult(value.Output,value.Error,value.ExitStatus);})
                {ExpectedInterval=TimeSpan.FromSeconds(1)};
            var measurements=new List<object>();var durations=new List<double>();var timer=Stopwatch.StartNew();
            for(int index=0;index<8;index++)
            {
                var sample=await monitor.SampleAsync();durations.Add(sample.CollectionDuration.TotalMilliseconds);
                measurements.Add(new{sample=index+1,timestampUtc=sample.Timestamp,durationMilliseconds=Math.Round(sample.CollectionDuration.TotalMilliseconds,3),
                    processCount=sample.Processes.Count,cpuState=sample.System.CpuStatus.State.ToString(),memoryState=sample.System.MemoryStatus.State.ToString(),
                    uptimeAvailable=sample.System.Uptime.HasValue,networkSourceAvailable=sample.Networks.Count>0,diskSourceAvailable=sample.Disks.Count>0,
                    processIoAvailable=sample.Processes.Count(p=>p.ReadStatus.State==MetricState.Available || p.WriteStatus.State==MetricState.Available),
                    processIoUnavailable=sample.Processes.Count(p=>p.ReadStatus.State==MetricState.Unavailable || p.WriteStatus.State==MetricState.Unavailable)});
                if(index<7)await Task.Delay(TimeSpan.FromSeconds(1));
            }
            timer.Stop();
            report=new{label="isolated-loopback-linux-monitor",utc=DateTimeOffset.UtcNow,
                build=Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                runtime=RuntimeInformation.FrameworkDescription,operatingSystem=RuntimeInformation.OSDescription,
                remoteOperations="read-only existing /proc, /etc/passwd, df and fixed SSH probe; no writes, signals, installations or configuration changes",
                sampleCount=8,intervalAfterCompletionSeconds=1,elapsedSeconds=Math.Round(timer.Elapsed.TotalSeconds,3),
                meanDurationMilliseconds=Math.Round(durations.Average(),3),maxDurationMilliseconds=Math.Round(durations.Max(),3),measurements};
            result=0;
        }
        catch(Exception error)
        {
            // Exception.Message and SSH responses can contain endpoints; expose safe failure metadata only.
            report=new{label="isolated-loopback-linux-monitor",utc=DateTimeOffset.UtcNow,result="FAIL",exception=error.GetType().FullName,hresult=error.HResult.ToString("X8")};result=1;
        }
        string json=JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true});
        if(reportFile is not null){string path=Path.GetFullPath(reportFile);Directory.CreateDirectory(Path.GetDirectoryName(path)!);await File.WriteAllTextAsync(path,json);}
        Console.WriteLine(json);return result;
    }

    internal static async Task<ServerSession> OpenSessionAsync(string configurationFile)
    {
        var config=await LinuxIntegration.LoadConfiguration(configurationFile);
        if(config.Host!="localhost"&&(!IPAddress.TryParse(config.Host,out var host)||!IPAddress.IsLoopback(host)))
            throw new InvalidDataException("Read-only monitor checks require independently verified isolated loopback.");
        var profile=new ServerProfile{Name="Read-only monitor fixture",Host=config.Host,Port=config.Port,Username=config.Username,
            Authentication=config.Authentication,PrivateKeyPath=config.PrivateKeyPath,TimeoutSeconds=20};
        var store=new AppStore(Path.Combine(Environment.CurrentDirectory,".tools","monitor-benchmark",Guid.NewGuid().ToString("N")));
        var session=new ServerSession(profile,store);
        try
        {
            await session.ConnectAsync(new SessionCredentials(config.Secret),(key,ct)=>
            {
                ct.ThrowIfCancellationRequested();
                if(key.Changed||key.Fingerprint!=config.ExpectedHostFingerprint)throw new InvalidDataException("Independent fixture fingerprint mismatch.");
                return Task.FromResult(TrustDecision.Session);
            });
            return session;
        }
        catch { await session.DisposeAsync();throw; }
    }
}
#endif
