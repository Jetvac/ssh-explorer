using System.Globalization;

namespace SSHExplorer.Monitoring;

/// <summary>Observable stages of one power request. Each stage states only what is actually known.</summary>
public enum PowerStage
{
    /// <summary>The single power command is being sent.</summary>
    Sending,
    /// <summary>The server answered with exit code 0: the operation was accepted (not yet performed).</summary>
    Accepted,
    /// <summary>The server refused (non-zero exit code); nothing else is tried.</summary>
    Rejected,
    /// <summary>The connection broke or timed out before an exit code arrived: acceptance is unknown.</summary>
    ResultUnknown,
    /// <summary>Accepted; waiting for the SSH connection to drop.</summary>
    WaitingForDisconnect,
    /// <summary>The SSH connection dropped. For power-off this is the final, unconfirmed state.</summary>
    Disconnected,
    /// <summary>Reboot: SSH port does not answer yet; bounded probing continues.</summary>
    WaitingForService,
    /// <summary>Reboot: SSH accepts TCP connections again; a normal reconnect (host key checked) verifies the boot.</summary>
    ServiceReachable,
    /// <summary>Reboot verified: boot_id changed (or uptime restarted).</summary>
    Confirmed,
    /// <summary>Reconnected, but boot_id and uptime show no new boot.</summary>
    NotConfirmed,
    /// <summary>The server stayed reachable after acceptance for the whole wait (e.g. inhibited or delayed shutdown).</summary>
    StillReachable,
    /// <summary>Probing gave up: the outcome is unknown.</summary>
    Unreachable,
    /// <summary>The user stopped waiting. An accepted command is NOT cancelled by this.</summary>
    Abandoned,
}

public sealed record PowerProgress(PowerStage Stage, string Message, DateTimeOffset Timestamp);

/// <summary>Boot identity read before the request and after the reconnect.</summary>
public sealed record BootMarker(string? BootId, TimeSpan? Uptime, DateTimeOffset ReadAt);

/// <summary>Timeouts of one power operation; small values are used by tests.</summary>
public sealed record PowerTimings(TimeSpan SendTimeout, TimeSpan DisconnectWait, TimeSpan ProbeInterval, TimeSpan ServiceWait)
{
    public static PowerTimings Default { get; } = new(TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5));
}

/// <summary>
/// Drives one reboot/power-off request. Guarantees: exactly one command is sent; it is never
/// repeated after a timeout; no force flags and no alternative commands; a dropped connection is
/// not taken as proof; waiting can be abandoned without pretending the command was cancelled.
/// </summary>
public sealed class PowerOperation
{
    readonly Func<string, CancellationToken, Task<ExecResult>> execute;
    readonly Func<CancellationToken, Task<bool>> sessionAlive;
    readonly Func<CancellationToken, Task<bool>> serviceReachable;
    readonly Func<DateTimeOffset> clock;
    readonly PowerTimings timings;
    readonly List<PowerProgress> log = [];
    int sent;
    public PowerAction Action { get; }
    public PowerCapability Capability { get; }
    public BootMarker? Before { get; }
    public PowerStage Stage { get; private set; } = PowerStage.Sending;
    public IReadOnlyList<PowerProgress> Log { get { lock (log) return log.ToArray(); } }
    public event Action<PowerProgress>? Progress;
    /// <summary>Number of power commands actually sent (0 or 1).</summary>
    public int CommandsSent => Volatile.Read(ref sent);

    public PowerOperation(PowerCapability capability, BootMarker? before,
        Func<string, CancellationToken, Task<ExecResult>> execute, Func<CancellationToken, Task<bool>> sessionAlive,
        Func<CancellationToken, Task<bool>> serviceReachable, PowerTimings? timings = null, Func<DateTimeOffset>? clock = null)
    {
        if (!capability.Available) throw new InvalidOperationException(capability.Explanation);
        Capability = capability; Action = capability.Action; Before = before;
        this.execute = execute; this.sessionAlive = sessionAlive; this.serviceReachable = serviceReachable;
        this.timings = timings ?? PowerTimings.Default; this.clock = clock ?? (() => SteadyClock.Now);
    }

    void Report(PowerStage stage, string message)
    {
        Stage = stage; var entry = new PowerProgress(stage, message, clock());
        lock (log) log.Add(entry);
        Progress?.Invoke(entry);
    }

    /// <summary>Runs until a terminal stage. Cancelling <paramref name="ct"/> means "stop waiting".</summary>
    public async Task<PowerStage> RunAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref sent, 1) != 0) throw new InvalidOperationException("Команда питания уже отправлена; повтор не выполняется.");
        Report(PowerStage.Sending, "Отправка: " + Capability.Command);
        ExecResult result;
        using (var send = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            send.CancelAfter(timings.SendTimeout);
            try { result = await execute(Capability.Command, send.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            { Report(PowerStage.Abandoned, "Ожидание прекращено до ответа сервера. Если команда дошла, она будет выполнена; повторная отправка не производится."); return Stage; }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException or IOException or System.Net.Sockets.SocketException or InvalidOperationException || error.GetType().Name.Contains("Ssh", StringComparison.Ordinal))
            {
                Report(PowerStage.ResultUnknown, "Соединение прервалось до ответа сервера. Неизвестно, принята ли команда; повторная отправка не производится.");
                return await AfterUnknownAsync(ct).ConfigureAwait(false);
            }
        }
        if (result.ExitStatus != 0)
        {
            var detail = (result.Error + " " + result.Output).Trim();
            if (detail.Length > 300) detail = detail[..300] + "…";
            Report(PowerStage.Rejected, $"Сервер отказал (код {result.ExitStatus.ToString(CultureInfo.InvariantCulture)})" + (detail.Length > 0 ? ": " + detail : ".") + " Другие способы не пробуются.");
            return Stage;
        }
        Report(PowerStage.Accepted, (Action == PowerAction.Reboot ? "Перезагрузка" : "Выключение") + " принято сервером (код 0). Это подтверждает приём, а не выполнение.");
        return await WaitForDisconnectAsync(ct).ConfigureAwait(false);
    }

    async Task<PowerStage> AfterUnknownAsync(CancellationToken ct)
    {
        if (Action == PowerAction.PowerOff) return Stage;
        return await WaitForServiceAsync(ct).ConfigureAwait(false);
    }

    async Task<PowerStage> WaitForDisconnectAsync(CancellationToken ct)
    {
        Report(PowerStage.WaitingForDisconnect, "Ожидание отключения SSH…");
        var deadline = clock() + timings.DisconnectWait;
        try
        {
            while (clock() < deadline)
            {
                if (!await sessionAlive(ct).ConfigureAwait(false))
                {
                    if (Action == PowerAction.PowerOff)
                    { Report(PowerStage.Disconnected, "SSH-соединение потеряно после принятой команды выключения. Состояние питания по SSH подтвердить нельзя."); return Stage; }
                    Report(PowerStage.Disconnected, "SSH-соединение потеряно. Обрыв сам по себе не подтверждает перезагрузку.");
                    return await WaitForServiceAsync(ct).ConfigureAwait(false);
                }
                await Task.Delay(timings.ProbeInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Abandon(); }
        Report(PowerStage.StillReachable, "Команда принята, но сервер продолжает отвечать дольше " + (int)timings.DisconnectWait.TotalSeconds + " с. Возможно, операция отложена или заблокирована (inhibitor). Повторная команда не отправляется.");
        return Stage;
    }

    async Task<PowerStage> WaitForServiceAsync(CancellationToken ct)
    {
        Report(PowerStage.WaitingForService, "Ожидание, когда SSH снова начнёт принимать подключения…");
        var deadline = clock() + timings.ServiceWait;
        try
        {
            // Give the server time to actually go down before treating an open port as "back".
            await Task.Delay(timings.ProbeInterval, ct).ConfigureAwait(false);
            while (clock() < deadline)
            {
                if (await serviceReachable(ct).ConfigureAwait(false))
                { Report(PowerStage.ServiceReachable, "SSH снова доступен. Переподключитесь (ключ сервера будет проверен), чтобы подтвердить новую загрузку по boot_id."); return Stage; }
                await Task.Delay(timings.ProbeInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Abandon(); }
        Report(PowerStage.Unreachable, "SSH не стал доступен за " + (int)timings.ServiceWait.TotalMinutes + " мин. Результат перезагрузки неизвестен.");
        return Stage;
    }

    PowerStage Abandon()
    {
        Report(PowerStage.Abandoned, "Ожидание прекращено. Принятая сервером команда этим не отменяется.");
        return Stage;
    }

    /// <summary>Compares the boot read after reconnecting with the boot before the request.</summary>
    public static (PowerStage Stage, string Message) Verify(BootMarker before, BootMarker after)
    {
        if (before.BootId is { } oldId && after.BootId is { } newId)
            return oldId != newId
                ? (PowerStage.Confirmed, $"Перезагрузка подтверждена: boot_id изменился" + (after.Uptime is { } up ? $", время работы {Format(up)}." : "."))
                : (PowerStage.NotConfirmed, "boot_id не изменился: новая загрузка не подтверждена.");
        if (before.Uptime is { } oldUp && after.Uptime is { } newUp)
        {
            var expected = oldUp + (after.ReadAt - before.ReadAt);
            return newUp + TimeSpan.FromSeconds(5) < expected
                ? (PowerStage.Confirmed, $"Перезагрузка подтверждена по uptime ({Format(newUp)}); boot_id недоступен.")
                : (PowerStage.NotConfirmed, "Uptime продолжился без сброса: перезагрузка не подтверждена.");
        }
        return (PowerStage.NotConfirmed, "Нет boot_id и uptime: подтвердить перезагрузку нельзя.");
    }

    static string Format(TimeSpan value) => value.TotalHours >= 1 ? $"{(int)value.TotalHours} ч {value.Minutes} мин" : value.TotalMinutes >= 1 ? $"{(int)value.TotalMinutes} мин {value.Seconds} с" : $"{value.Seconds} с";
}

/// <summary>
/// Pending reboot verifications by profile and endpoint: the next normal connection to the same
/// server (with its host key checked as usual) compares boot identity once.
/// </summary>
public static class PowerVerificationRegistry
{
    sealed record Pending(Guid ProfileId, string Endpoint, BootMarker Before, DateTimeOffset Created);
    static readonly Dictionary<(Guid, string), Pending> pending = [];
    static readonly object gate = new();
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    public static void Register(Guid profileId, string endpoint, BootMarker before)
    { lock (gate) pending[(profileId, endpoint)] = new(profileId, endpoint, before, before.ReadAt); }

    public static bool IsPending(Guid profileId, string endpoint) { lock (gate) return pending.ContainsKey((profileId, endpoint)); }

    /// <summary>Consumes the pending entry and returns the verification result, or null if none.</summary>
    public static (PowerStage Stage, string Message)? Complete(Guid profileId, string endpoint, BootMarker after)
    {
        Pending? entry;
        lock (gate) { if (!pending.Remove((profileId, endpoint), out entry)) return null; }
        if (after.ReadAt - entry.Created > Lifetime) return (PowerStage.NotConfirmed, "Проверка просрочена: с момента команды прошло больше 30 минут.");
        return PowerOperation.Verify(entry.Before, after);
    }
}
