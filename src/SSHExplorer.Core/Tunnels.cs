using System.Globalization;
using System.Net;
using Renci.SshNet;

namespace SSHExplorer.Core;

/// <summary>Local (-L): a port on this computer leads to an address seen from the server. Remote (-R): a port on the server leads to an address seen from this computer.</summary>
public enum TunnelKind { Local, Remote }

/// <summary>A saved tunnel of one server profile.</summary>
public sealed class TunnelSpec
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public TunnelKind Kind { get; set; } = TunnelKind.Local;
    /// <summary>Where the listening side binds: this computer for Local, the server for Remote. Loopback by default.</summary>
    public string BindAddress { get; set; } = "127.0.0.1";
    public int BindPort { get; set; }
    public string TargetHost { get; set; } = "127.0.0.1";
    public int TargetPort { get; set; }
    /// <summary>Open automatically when the server tab connects.</summary>
    public bool StartOnConnect { get; set; }
    public TunnelSpec Clone() => (TunnelSpec)MemberwiseClone();
}

public enum TunnelState { Stopped, Starting, Running, Failed }

public sealed record TunnelStatus(TunnelState State, int Connections, int? BoundPort, string? Error, DateTime? Since)
{
    public static readonly TunnelStatus Stopped = new(TunnelState.Stopped, 0, null, null, null);
}

public static class Tunnels
{
    /// <summary>Null when the tunnel can be opened; otherwise the reason.</summary>
    public static string? Validate(TunnelSpec spec)
    {
        if (!IsHost(spec.BindAddress)) return "Адрес прослушивания: укажите IP-адрес или имя без пробелов (обычно 127.0.0.1).";
        if (spec.BindPort is < 0 or > 65535 || spec.Kind == TunnelKind.Remote && spec.BindPort == 0) return spec.Kind == TunnelKind.Remote ? "Порт на сервере должен быть от 1 до 65535." : "Порт на компьютере должен быть от 0 до 65535 (0 — выбрать свободный).";
        if (!IsHost(spec.TargetHost)) return "Адрес назначения: укажите IP-адрес или имя без пробелов.";
        if (spec.TargetPort is < 1 or > 65535) return "Порт назначения должен быть от 1 до 65535.";
        return null;
    }

    static bool IsHost(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 253 && !value.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or '\0' or '@');

    /// <summary>Listening on every interface makes the port reachable by other machines: the editor warns about it.</summary>
    public static bool IsExposed(TunnelSpec spec) => !IsLoopback(spec.BindAddress);

    public static bool IsLoopback(string address) => address is "localhost" or "::1" || address.StartsWith("127.", StringComparison.Ordinal);

    static string Endpoint(string host, int port) => (host.Contains(':') ? $"[{host}]" : host) + ":" + port.ToString(CultureInfo.InvariantCulture);

    /// <summary>«127.0.0.1:8080 на компьютере → db:5432 со стороны сервера».</summary>
    public static string Describe(TunnelSpec spec, int? boundPort = null)
    {
        var bind = Endpoint(spec.BindAddress, boundPort ?? spec.BindPort);
        var target = Endpoint(spec.TargetHost, spec.TargetPort);
        return spec.Kind == TunnelKind.Local ? $"{bind} на компьютере → {target} со стороны сервера" : $"{bind} на сервере → {target} со стороны компьютера";
    }
}

/// <summary>
/// Open tunnels of one session. Each tunnel has its own SSH connection (through the same jump hosts and host-key check),
/// so a busy tunnel never slows the file manager or the terminal, and stopping one does not touch the others.
/// </summary>
public sealed class TunnelManager(ServerSession session) : IAsyncDisposable
{
    sealed class Active
    {
        public required SshClient Client;
        public required ForwardedPort Port;
        public int Connections;
        public int? BoundPort;
        public string? Error;
        public DateTime Since = DateTime.Now;
        public TunnelState State = TunnelState.Running;
    }
    readonly Dictionary<Guid, Active> active = [];
    readonly Dictionary<Guid, string> failures = [];
    readonly HashSet<Guid> starting = [];
    readonly object gate = new();
    bool disposed;

    /// <summary>Raised (on any thread) when a tunnel starts, stops, fails or accepts a connection.</summary>
    public event Action<Guid>? Changed;

    public TunnelStatus Status(Guid id)
    {
        lock (gate)
        {
            if (starting.Contains(id)) return new TunnelStatus(TunnelState.Starting, 0, null, null, null);
            if (active.TryGetValue(id, out var a)) return new TunnelStatus(a.State, a.Connections, a.BoundPort, a.Error, a.Since);
            return failures.TryGetValue(id, out var error) ? new TunnelStatus(TunnelState.Failed, 0, null, error, null) : TunnelStatus.Stopped;
        }
    }

    public bool IsRunning(Guid id) { lock (gate) return active.TryGetValue(id, out var a) && a.State == TunnelState.Running; }

    public async Task StartAsync(TunnelSpec spec, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Tunnels.Validate(spec) is { } problem) throw new ArgumentException(problem);
        lock (gate)
        {
            if (active.ContainsKey(spec.Id) || !starting.Add(spec.Id)) return;
            failures.Remove(spec.Id);
        }
        Changed?.Invoke(spec.Id);
        SshClient? client = null;
        try
        {
            client = await session.CreateSshClientAsync(ct).ConfigureAwait(false);
            ForwardedPort port = spec.Kind == TunnelKind.Local
                ? new ForwardedPortLocal(spec.BindAddress, (uint)spec.BindPort, spec.TargetHost, (uint)spec.TargetPort)
                : new ForwardedPortRemote(spec.BindAddress, (uint)spec.BindPort, spec.TargetHost, (uint)spec.TargetPort);
            var entry = new Active { Client = client, Port = port };
            port.RequestReceived += (_, _) => { Interlocked.Increment(ref entry.Connections); Changed?.Invoke(spec.Id); };
            port.Exception += (_, e) => { entry.Error = ErrorMessages.Describe(e.Exception); Changed?.Invoke(spec.Id); };
            client.ErrorOccurred += (_, e) => { entry.State = TunnelState.Failed; entry.Error = "Соединение туннеля прервано: " + ErrorMessages.Describe(e.Exception); Changed?.Invoke(spec.Id); };
            client.AddForwardedPort(port);
            // Start binds the listener (local) or asks the server to (remote): a taken port fails here.
            await Task.Run(port.Start, ct).ConfigureAwait(false);
            if (port is ForwardedPortLocal local) entry.BoundPort = (int)local.BoundPort;
            lock (gate) { starting.Remove(spec.Id); active[spec.Id] = entry; }
            client = null;
        }
        catch (Exception ex)
        {
            lock (gate) { starting.Remove(spec.Id); if (ex is not OperationCanceledException) failures[spec.Id] = Explain(spec, ex); }
            if (client is not null) session.ReleaseClient(client);
            Changed?.Invoke(spec.Id);
            if (ex is OperationCanceledException) throw;
            throw new IOException(Explain(spec, ex), ex);
        }
        Changed?.Invoke(spec.Id);
    }

    static string Explain(TunnelSpec spec, Exception ex)
    {
        var text = ErrorMessages.Describe(ex);
        if (ex is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse or System.Net.Sockets.SocketError.AccessDenied })
            return $"Порт {spec.BindPort} на компьютере занят или недоступен. Выберите другой.";
        if (spec.Kind == TunnelKind.Remote && ex is Renci.SshNet.Common.SshException)
            return $"Сервер отказал в открытии порта {spec.BindPort}: он может быть занят, ниже 1024 без root или запрещён настройкой sshd (AllowTcpForwarding). {text}";
        return text;
    }

    public Task StopAsync(Guid id)
    {
        Active? entry;
        lock (gate) { if (!active.Remove(id, out entry)) { failures.Remove(id); entry = null; } }
        if (entry is null) { Changed?.Invoke(id); return Task.CompletedTask; }
        return Task.Run(() =>
        {
            try { if (entry.Port.IsStarted) entry.Port.Stop(); } catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or Renci.SshNet.Common.SshException or IOException) { }
            session.ReleaseClient(entry.Client);
            Changed?.Invoke(id);
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true;
        Guid[] ids; lock (gate) ids = [.. active.Keys];
        foreach (var id in ids) await StopAsync(id).ConfigureAwait(false);
    }
}
