#if CORE_TESTS
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

/// <summary>Development fixture only. Never ships inside the application or runs on the server.</summary>
internal sealed class TcpFaultProxy : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly string upstreamHost;
    private readonly int upstreamPort;
    private readonly TimeSpan delay;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<int, Connection> connections = new();
    private readonly ConcurrentBag<Task> handlers = [];
    private Task accepting = Task.CompletedTask;
    private int nextId, enabled = 1, accepted;
    public int ActiveConnections => connections.Count;
    public int AcceptedConnections => Volatile.Read(ref accepted);
    public int Port { get; }

    public TcpFaultProxy(int port, string targetHost, int targetPort, TimeSpan oneWayChunkDelay)
    {
        if (port is < 1024 or > 65535 || port == targetPort) throw new ArgumentOutOfRangeException(nameof(port));
        if (oneWayChunkDelay < TimeSpan.Zero || oneWayChunkDelay > TimeSpan.FromSeconds(1)) throw new ArgumentOutOfRangeException(nameof(oneWayChunkDelay));
        Port = port; upstreamHost = targetHost; upstreamPort = targetPort; delay = oneWayChunkDelay;
        listener = new TcpListener(IPAddress.Loopback, port);
    }

    public void Start()
    {
        listener.Start();
        accepting = AcceptLoop();
    }

    public void CutAll()
    {
        Interlocked.Exchange(ref enabled, 0);
        foreach (var c in connections.Values) c.Dispose();
    }

    public void Restore() => Interlocked.Exchange(ref enabled, 1);

    private async Task AcceptLoop()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                if (Volatile.Read(ref enabled) == 0) { client.Dispose(); continue; }
                Interlocked.Increment(ref accepted);
                handlers.Add(Handle(client));
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (SocketException) when (lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task Handle(TcpClient client)
    {
        using var upstream = new TcpClient { NoDelay = true };
        client.NoDelay = true;
        var id = Interlocked.Increment(ref nextId);
        using var connection = new Connection(client, upstream);
        connections[id] = connection;
        try
        {
            await upstream.ConnectAsync(upstreamHost, upstreamPort, lifetime.Token);
            if (Volatile.Read(ref enabled) == 0) return;
            var forward = Pump(client.GetStream(), upstream.GetStream());
            var reverse = Pump(upstream.GetStream(), client.GetStream());
            await Task.WhenAny(forward, reverse);
            connection.Dispose();
            await Task.WhenAll(forward, reverse);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
        finally { connections.TryRemove(id, out _); }
    }

    private async Task Pump(NetworkStream source, NetworkStream target)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, lifetime.Token);
            if (read == 0) return;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, lifetime.Token);
            await target.WriteAsync(buffer.AsMemory(0, read), lifetime.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); listener.Stop(); CutAll();
        await accepting;
        await Task.WhenAll(handlers.ToArray()).WaitAsync(TimeSpan.FromSeconds(3));
        lifetime.Dispose();
    }

    private sealed class Connection(TcpClient client, TcpClient upstream) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            client.Dispose(); upstream.Dispose();
        }
    }
}
#endif
