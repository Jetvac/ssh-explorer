using System.Diagnostics;

namespace SSHExplorer.Core;

/// <summary>An async gate whose capacity can change while jobs wait: raising it starts waiting jobs at once.</summary>
public sealed class ParallelGate(int capacity)
{
    readonly object sync = new();
    readonly LinkedList<TaskCompletionSource> waiters = new();
    int running, limit = Math.Clamp(capacity, 1, 8);

    public int Capacity { get { lock (sync) return limit; } set { lock (sync) { limit = Math.Clamp(value, 1, 8); Pump(); } } }
    public int Running { get { lock (sync) return running; } }

    public async Task WaitAsync(CancellationToken ct)
    {
        TaskCompletionSource waiter;
        lock (sync)
        {
            if (running < limit && waiters.Count == 0) { running++; return; }
            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); waiters.AddLast(waiter);
        }
        using (ct.Register(() => { lock (sync) { if (waiters.Remove(waiter)) waiter.TrySetCanceled(ct); } }))
            await waiter.Task.ConfigureAwait(false);
    }

    public void Release() { lock (sync) { running--; Pump(); } }

    void Pump()
    {
        while (running < limit && waiters.First is { } next) { waiters.RemoveFirst(); running++; next.Value.TrySetResult(); }
    }
}

/// <summary>
/// A shared token bucket for every running transfer (bytes per second; 0 = unlimited). Bursts are bounded to a quarter
/// of a second of traffic so a limit is respected within a few hundred milliseconds. Changing it applies immediately.
/// </summary>
public sealed class RateLimiter
{
    readonly object sync = new();
    readonly Stopwatch clock = Stopwatch.StartNew();
    long bytesPerSecond; double tokens; double last;

    public long BytesPerSecond { get { lock (sync) return bytesPerSecond; } set { lock (sync) { bytesPerSecond = Math.Max(0, value); tokens = 0; last = clock.Elapsed.TotalSeconds; } } }

    public async ValueTask WaitAsync(int bytes, CancellationToken ct)
    {
        while (true)
        {
            TimeSpan delay;
            lock (sync)
            {
                if (bytesPerSecond <= 0) return;
                double now = clock.Elapsed.TotalSeconds; tokens = Math.Min(bytesPerSecond / 4.0, tokens + (now - last) * bytesPerSecond); last = now;
                // A chunk larger than the burst is admitted once the bucket is full, then repaid as debt.
                if (tokens >= Math.Min(bytes, bytesPerSecond / 4.0)) { tokens -= bytes; return; }
                delay = TimeSpan.FromSeconds(Math.Max(0.005, (Math.Min(bytes, bytesPerSecond / 4.0) - tokens) / bytesPerSecond));
            }
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }
}
