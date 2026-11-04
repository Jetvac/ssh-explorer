using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using SSHExplorer.Monitoring;

/// <summary>
/// Linux-only: runs the beta.5 collector (extracted from its tagged C# source) and the current
/// protocol-2 collector against the local /proc with extra sleeping processes, and reports wall
/// time, fork/exec count (from /proc/stat "processes") and response size. Same host, same load.
/// </summary>
internal static class CollectorBenchmark
{
    public static async Task<int> RunAsync(string oldSourcePath, string? reportPath)
    {
        if (!OperatingSystem.IsLinux()) { Console.WriteLine("SKIP: collector benchmark needs a Linux /proc"); return 0; }
        var oldSource = await File.ReadAllTextAsync(oldSourcePath);
        string Extract(string name) { var m = Regex.Match(oldSource, name + @" = """"""\r?\n(.*?)\r?\n\s*"""""";", RegexOptions.Singleline); if (!m.Success) throw new InvalidDataException("cannot extract " + name); return Dedent(m.Groups[1].Value); }
        var oldScript = Extract("Script").Replace("__MAX_PROCESSES__", "2048").Replace("__ENCODER_SETUP__", Extract("EncoderSetup"));
        var sleepers = new List<Process>();
        for (int i = 0; i < 300; i++) sleepers.Add(Process.Start(new ProcessStartInfo("sleep", "600") { UseShellExecute = false })!);
        try
        {
            await Task.Delay(500);
            var calculator = new MonitorCalculator();
            var first = await Measure(CollectorCommand.Create(CollectorRequest.Full(2048)), 1);
            calculator.Calculate(first.Output, DateTimeOffset.UtcNow);
            var warm = await Measure(CollectorCommand.Create(new CollectorRequest(true, false, calculator.PendingCommandLines)), 1);
            var results = new Dictionary<string, object>
            {
                ["host"] = Environment.MachineName, ["processorCount"] = Environment.ProcessorCount, ["processes"] = Directory.GetDirectories("/proc").Count(d => int.TryParse(Path.GetFileName(d), out _)),
                ["beta5"] = await Measure(oldScript, 5),
                ["protocol2FirstSample"] = first with { Output = "" },
                ["protocol2CommandLineWarmup"] = warm with { Output = "" },
                ["protocol2Processes"] = await Measure(CollectorCommand.Create(new CollectorRequest(true, false, [])), 5),
                ["protocol2SystemOnly"] = await Measure(CollectorCommand.Create(CollectorRequest.SystemOnly), 5),
                ["serverInfo"] = await Measure(ServerInfoCollector.Command, 3),
            };
            var json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json); if (reportPath is not null) await File.WriteAllTextAsync(reportPath, json);
            return 0;
        }
        finally { foreach (var p in sleepers) { try { p.Kill(); } catch (InvalidOperationException) { } p.Dispose(); } }
    }

    public sealed record Result(double MedianMilliseconds, double MinMilliseconds, long Forks, int Bytes, int Runs) { [System.Text.Json.Serialization.JsonIgnore] public string Output { get; init; } = ""; }

    static async Task<Result> Measure(string script, int runs)
    {
        var times = new List<double>(); long forks = 0; string output = "";
        for (int i = 0; i < runs; i++)
        {
            long before = Forks(); var watch = Stopwatch.StartNew();
            var info = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }; info.ArgumentList.Add("-c"); info.ArgumentList.Add(script);
            using var process = Process.Start(info)!; var read = process.StandardOutput.ReadToEndAsync(); _ = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); output = await read;
            watch.Stop(); times.Add(watch.Elapsed.TotalMilliseconds); forks = Forks() - before - 1;
        }
        times.Sort();
        return new Result(Math.Round(times[times.Count / 2], 1), Math.Round(times[0], 1), forks, output.Length, runs) { Output = output };
    }

    static long Forks() => long.Parse(File.ReadLines("/proc/stat").First(l => l.StartsWith("processes ", StringComparison.Ordinal)).Split(' ')[1]);
    static string Dedent(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n'); int indent = lines.Where(l => l.Trim().Length > 0).Min(l => l.Length - l.TrimStart().Length);
        return string.Join('\n', lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart()));
    }
}
