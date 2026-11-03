using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Renci.SshNet.Common;
using SSHExplorer.Core;

internal static class AuthenticationNetworkScenarios
{
    private static int passed;
    private static void Check(bool value, string label)
    { if (!value) throw new Exception("FAIL " + label); passed++; Console.WriteLine("PASS " + label); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }

    public static async Task RunScriptedAsync(string python)
    {
        passed = 0;
        string root = Path.Combine(Environment.CurrentDirectory, ".tools", "auth-scripted", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.GetFullPath("scripts/testing/auth_fixture.py")); start.ArgumentList.Add(root);
        using var fixture = Process.Start(start) ?? throw new IOException("Unable to start isolated headless auth fixture");
        // Observe output without publishing server responses or secrets.
        var stderr = fixture.StandardError.ReadToEndAsync();
        try
        {
            string? ready = await fixture.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            if (ready != "READY") throw new IOException("Isolated auth fixture did not become ready");
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "fixture.json")));
            var cfg = doc.RootElement;
            string secret = await File.ReadAllTextAsync(cfg.GetProperty("SecretFile").GetString()!);
            string otp = await File.ReadAllTextAsync(cfg.GetProperty("OtpFile").GetString()!);
            string fingerprint = cfg.GetProperty("ExpectedHostFingerprint").GetString()!;
            ServerProfile Profile(string mode, AuthenticationKind authentication = AuthenticationKind.Password) => new()
            { Name = "generated loopback auth test", Host = "127.0.0.1", Port = cfg.GetProperty("Ports").GetProperty(mode).GetInt32(), Username = "fixture", Authentication = authentication, PrivateKeyPath = cfg.GetProperty("PrivateKeyPath").GetString()!, TimeoutSeconds = 4 };
            ServerSession Session(ServerProfile profile) => new(profile, new AppStore(Path.Combine(root, Guid.NewGuid().ToString("N"))));
            Task<TrustDecision> Trust(HostKeyPrompt key, CancellationToken ct)
            { ct.ThrowIfCancellationRequested(); if (key.Fingerprint != fingerprint) throw new IOException("Independent generated fingerprint mismatch"); return Task.FromResult(TrustDecision.Session); }
            SessionCredentials Answer(Action<InteractivePrompt>? onPrompt = null) => new(secret, CancellablePrompt: (prompt, ct) =>
            { ct.ThrowIfCancellationRequested(); onPrompt?.Invoke(prompt); return Task.FromResult<string?>(otp); });

            await using (var session = Session(Profile("password")))
            {
                var stages = new ConcurrentQueue<ConnectionStageEvent>(); session.StageChanged += (_, value) => stages.Enqueue(value);
                session.StageChanged += (_, _) => throw new InvalidOperationException("Expected diagnostic sink failure");
                int prompts = 0; var operation = Guid.NewGuid();
                await session.ConnectAsync(Answer(_ => prompts++), Trust, operationId: operation);
                Check(session.State == SessionState.Connected && session.SftpAvailable && session.ShellAvailable && prompts == 0,
                    "real password protocol preserves Unicode spaces quotes and never opens unnecessary PAM prompt");
                var events = stages.ToArray();
                Check(events.Any(x => x.OperationId == operation && x.Stage == ConnectionStage.Connect && x.Outcome == ConnectionStageOutcome.Succeeded)
                    && events.Any(x => x.Stage == ConnectionStage.ExecProbe && x.Outcome == ConnectionStageOutcome.Succeeded)
                    && events.Any(x => x.Stage == ConnectionStage.Sftp && x.Outcome == ConnectionStageOutcome.Succeeded), "real initial transport authentication probe SFTP stages are correlated");
                Check(!JsonSerializer.Serialize(events).Contains(secret, StringComparison.Ordinal) && !JsonSerializer.Serialize(events).Contains(otp, StringComparison.Ordinal), "real diagnostic events contain neither password nor OTP");
                using var shell = await session.CreateShellAsync();
                await using var exportLifetime = new AsyncExport(await session.OpenExportSourceAsync());
                Check(session.State == SessionState.Connected && (await session.ExecuteAsync("fixture alive")).Output == "alive",
                    "independent PTY and native-export SFTP authentication preserve connected session");
            }
            await using (var session = Session(Profile("password")))
            {
                await ThrowsAsync<SshAuthenticationException>(() => session.ConnectAsync(new(secret + "wrong"), Trust));
                Check(session.State == SessionState.Disconnected, "wrong password returns authentication failure without process termination");
                await session.ConnectAsync(new(secret), Trust);
                Check(session.State == SessionState.Connected && session.SftpAvailable, "same failed session permits correct-password retry with clean transports");
            }
            async Task WaitStatistic(string category, string mode, Func<int, bool> predicate)
            {
                var timer = Stopwatch.StartNew();
                while (timer.Elapsed < TimeSpan.FromSeconds(5))
                {
                    string path = Path.Combine(root, "statistics.json");
                    if (File.Exists(path))
                    {
                        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var statistics = await JsonDocument.ParseAsync(input);
                        if (statistics.RootElement.GetProperty(category).TryGetProperty(mode, out var count) && predicate(count.GetInt32())) return;
                    }
                    await Task.Delay(20);
                }
                throw new TimeoutException("Isolated auth fixture did not reach expected lifecycle state");
            }
            int delayedAttempt = 0;
            foreach (var dispose in new[] { false, true })
            {
                var delayedProfile = Profile("password-delay"); delayedProfile.TimeoutSeconds = 10;
                await using var session = Session(delayedProfile);
                using var cancellation = new CancellationTokenSource();
                var pending = session.ConnectAsync(new(secret), Trust, cancellation.Token);
                var observedActive = WaitStatistic("active", "password-delay", count => count == 1);
                if (await Task.WhenAny(pending, observedActive) == pending) await pending;
                await observedActive;
                // The server blocks inside password authentication, after its local SSH session exists.
                delayedAttempt++;
                await WaitStatistic("passwordReceived", "password-delay", count => count >= delayedAttempt);
                if (dispose) await session.DisposeAsync(); else cancellation.Cancel();
                await ThrowsAsync<OperationCanceledException>(() => pending);
                await File.WriteAllTextAsync(Path.Combine(root, "release-password-" + delayedAttempt), "release own fixture authentication");
                await WaitStatistic("active", "password-delay", count => count == 0);
                Check(dispose ? session.State == SessionState.Closed : session.State == SessionState.Disconnected,
                    dispose ? "session close during delayed password authentication disposes late vendor transport" : "caller cancellation during delayed password authentication disposes late vendor transport");
            }
            await using (var session = Session(Profile("password")))
            {
                var stages = new ConcurrentQueue<ConnectionStageEvent>(); session.StageChanged += (_, value) => stages.Enqueue(value);
                await ThrowsAsync<OperationCanceledException>(() => session.ConnectAsync(new(secret), (_, _) => Task.FromResult(TrustDecision.Reject)));
                Check(!stages.Any(x => x.Stage == ConnectionStage.Authentication && x.Outcome == ConnectionStageOutcome.Started), "real rejected host key prevents authentication stage");
            }
            await using (var session = Session(Profile("password")))
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var cancellation = new CancellationTokenSource();
                var first = session.ConnectAsync(new(secret), async (_, ct) =>
                { entered.SetResult(); try { await Task.Delay(Timeout.Infinite, ct); return TrustDecision.Reject; } finally { ended.SetResult(); } }, cancellation.Token);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await ThrowsAsync<InvalidOperationException>(() => session.ConnectAsync(new(secret), Trust));
                cancellation.Cancel(); await ThrowsAsync<OperationCanceledException>(() => first); await ended.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Check(session.State == SessionState.Disconnected, "duplicate Connect is rejected while cancellable host trust is pending and cancellation finishes original attempt");
            }
            foreach (var mode in new[] { "password-mfa", "key-mfa" })
            {
                await using var session = Session(Profile(mode, mode == "key-mfa" ? AuthenticationKind.PrivateKey : AuthenticationKind.Password));
                int prompts = 0;
                var credentials = new SessionCredentials(mode == "key-mfa" ? "" : secret, CancellablePrompt: (prompt, ct) =>
                { ct.ThrowIfCancellationRequested(); if (prompt.Prompt != "One-time password:") throw new InvalidOperationException("Unexpected fixture challenge"); Interlocked.Increment(ref prompts); return Task.FromResult<string?>(otp); });
                await session.ConnectAsync(credentials, Trust);
                Check(session.State == SessionState.Connected && prompts == 2, "real " + mode + " requires explicit OTP on independent exec and SFTP transports");
                using var shell = await session.CreateShellAsync();
                using var export = await session.OpenExportSourceAsync();
                Check(prompts == 4 && session.State == SessionState.Connected, "real " + mode + " repeats legitimate MFA for PTY and transfer connections");
            }
            await using (var session = Session(Profile("interactive", AuthenticationKind.KeyboardInteractive)))
            {
                await session.ConnectAsync(Answer(), Trust);
                Check(session.State == SessionState.Connected, "explicit keyboard-interactive profile authenticates through actual OTP callback");
            }
            await using (var session = Session(Profile("interactive", AuthenticationKind.KeyboardInteractive)))
            {
                await ThrowsAsync<SshAuthenticationException>(() => session.ConnectAsync(new(secret), Trust));
                Check(session.State == SessionState.Disconnected, "one-time password prompt never receives saved secret when no callback exists");
            }
            await using (var session = Session(Profile("interactive", AuthenticationKind.KeyboardInteractive)))
            {
                await ThrowsAsync<InvalidOperationException>(() => session.ConnectAsync(new(secret, CancellablePrompt: (_, _) => throw new InvalidOperationException("fixture callback failed")), Trust));
                Check(session.State == SessionState.Disconnected, "SSH.NET synchronous callback exception returns to Connect task without fatal worker exception");
            }
            foreach (var legacy in new[] { false, true })
            {
                await using var session = Session(Profile("interactive", AuthenticationKind.KeyboardInteractive));
                var credentials = legacy ? new SessionCredentials(secret, _ => Task.FromResult<string?>(null)) : new SessionCredentials(secret, CancellablePrompt: (_, _) => Task.FromResult<string?>(null));
                await ThrowsAsync<OperationCanceledException>(() => session.ConnectAsync(credentials, Trust));
                Check(session.State == SessionState.Disconnected, "real " + (legacy ? "legacy" : "cancellable") + " prompt cancellation exits only its connection attempt");
            }
            await using (var session = Session(Profile("interactive", AuthenticationKind.KeyboardInteractive)))
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var stages = new ConcurrentQueue<ConnectionStageEvent>(); session.StageChanged += (_, value) => stages.Enqueue(value);
                using var cancellation = new CancellationTokenSource(); var operation = Guid.NewGuid();
                var pending = session.ConnectAsync(new(secret, CancellablePrompt: async (_, ct) =>
                { entered.SetResult(); try { await Task.Delay(Timeout.Infinite, ct); return null; } finally { ended.SetResult(); } }), Trust, cancellation.Token, operation);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancellation.Cancel();
                await ThrowsAsync<OperationCanceledException>(() => pending); await ended.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Check(session.State == SessionState.Disconnected && stages.Count(x => x.OperationId == operation && x.Stage == ConnectionStage.Connect && x.Outcome == ConnectionStageOutcome.Cancelled) == 1
                    && !stages.Any(x => x.Stage == ConnectionStage.Sftp && x.Outcome == ConnectionStageOutcome.Succeeded), "initial OTP cancellation emits one correlated terminal result and never starts usable SFTP");
            }
            foreach (var dispose in new[] { false, true })
            {
                await using var session = Session(Profile("password-mfa"));
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var cancelledPrompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                int prompts = 0;
                var credentials = new SessionCredentials(secret, CancellablePrompt: async (_, ct) =>
                {
                    if (Interlocked.Increment(ref prompts) <= 2) return otp;
                    entered.TrySetResult();
                    try { await Task.Delay(Timeout.Infinite, ct); return null; }
                    finally { cancelledPrompt.TrySetResult(); }
                });
                await session.ConnectAsync(credentials, Trust);
                using var cancellation = new CancellationTokenSource();
                var pending = session.OpenExportSourceAsync(cancellation.Token);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                if (dispose) await session.DisposeAsync(); else cancellation.Cancel();
                await ThrowsAsync<OperationCanceledException>(async () => { using var unexpected = await pending; });
                await cancelledPrompt.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Check(dispose ? session.State == SessionState.Closed : session.State == SessionState.Connected && (await session.ExecuteAsync("fixture alive")).Output == "alive" && session.SftpAvailable,
                    dispose ? "session disposal cancels pending auxiliary MFA and cannot resurrect closed state" : "auxiliary MFA cancellation preserves established exec SFTP and connected state");
            }
            await using (var session = Session(Profile("interactive", AuthenticationKind.KeyboardInteractive)))
            {
                var observedCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var timer = Stopwatch.StartNew();
                int pendingCallbacks=0;Exception? timeoutFailure=null;
                try
                {
                    await session.ConnectAsync(new(secret,CancellablePrompt:async(_,ct)=>
                    {
                        Interlocked.Increment(ref pendingCallbacks);
                        try { await Task.Delay(Timeout.Infinite,ct);return null; }
                        finally { Interlocked.Decrement(ref pendingCallbacks);if(ct.IsCancellationRequested)observedCancellation.TrySetResult(); }
                    }),Trust);
                }
                catch(Exception error)when(error.GetType()==typeof(TimeoutException)||error.GetType()==typeof(SshOperationTimeoutException)) { timeoutFailure=error; }
                if(timeoutFailure is null)throw new Exception("Expected a bounded authentication timeout.");
                await observedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Check(Volatile.Read(ref pendingCallbacks)==0&&timer.Elapsed < TimeSpan.FromSeconds(7), "transport authentication timeout cancels pending OTP callback and returns bounded timeout");
                Console.WriteLine("INFO expected authentication timeout type: "+timeoutFailure.GetType().Name+"; active callbacks: "+Volatile.Read(ref pendingCallbacks));
            }
            foreach (var mode in new[] { "sftp-denied", "exec-denied", "both-denied" })
            {
                await using var session = Session(Profile(mode));
                var stages = new ConcurrentQueue<ConnectionStageEvent>(); session.StageChanged += (_, value) => stages.Enqueue(value);
                if (mode == "both-denied") await ThrowsAsync<IOException>(() => session.ConnectAsync(new(secret), Trust));
                else await session.ConnectAsync(new(secret), Trust);
                Check(mode switch
                {
                    "sftp-denied" => session.State == SessionState.Connected && session.ShellAvailable && !session.SftpAvailable && stages.Any(x => x.Stage == ConnectionStage.Sftp && x.Outcome == ConnectionStageOutcome.Failed),
                    "exec-denied" => session.State == SessionState.Connected && !session.ShellAvailable && session.SftpAvailable && stages.Any(x => x.Stage == ConnectionStage.ExecProbe && x.Outcome == ConnectionStageOutcome.Failed),
                    _ => session.State == SessionState.Disconnected && !session.SftpAvailable
                }, "real " + mode + " is distinguished from authentication failure and preserves usable subsystem");
            }
            Console.WriteLine($"{passed} generated loopback SSH auth checks passed (headless component/network, no application windows)");
        }
        finally
        {
            if (!fixture.HasExited) fixture.Kill(entireProcessTree: true);
            await fixture.WaitForExitAsync();
            string fixtureErrors = await stderr;
            await File.WriteAllTextAsync(Path.Combine(root, "fixture-stderr.txt"), fixtureErrors.Length > 32 * 1024 ? fixtureErrors[..(32 * 1024)] : fixtureErrors);
        }
    }

    public static async Task RunLinuxAsync(string file)
    {
        passed = 0;
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file)); var cfg = doc.RootElement;
        string Read(string key) => cfg.GetProperty(key).GetString()!;
        if (Read("Host") != "127.0.0.1" || string.IsNullOrEmpty(Read("ExpectedHostFingerprint"))) throw new InvalidDataException("Only independently verified isolated loopback fixture is allowed");
        var profile = new ServerProfile { Name = "verified Linux auth smoke", Host = Read("Host"), Port = cfg.GetProperty("Port").GetInt32(), Username = Read("Username"), TimeoutSeconds = 10 };
        var store = new AppStore(Path.Combine(Environment.CurrentDirectory, ".tools", "auth-linux", Guid.NewGuid().ToString("N")));
        await using var session = new ServerSession(profile, store);
        var stages = new ConcurrentQueue<ConnectionStageEvent>(); session.StageChanged += (_, value) => stages.Enqueue(value);
        string secret = (await File.ReadAllTextAsync(Read("SecretFile"))).TrimEnd('\r', '\n');
        await session.ConnectAsync(new(secret), (key, ct) =>
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(key.Fingerprint == Read("ExpectedHostFingerprint") ? TrustDecision.Session : throw new IOException("Independent fixture fingerprint mismatch")); });
        Check(session.State == SessionState.Connected && session.SftpAvailable && session.ShellAvailable, "verified fresh Alpine password login exec probe and SFTP connection succeed");
        await session.Files.ListAsync("~");
        Check(stages.Any(x => x.Stage == ConnectionStage.Sftp && x.Outcome == ConnectionStageOutcome.Succeeded), "real initial folder listing succeeds after separate SFTP stage");
        using var shell = await session.CreateShellAsync(); using var export = await session.OpenExportSourceAsync();
        Check(session.State == SessionState.Connected && (await session.ExecuteAsync("printf alive")).Output == "alive", "real Alpine independent PTY and export transports preserve main exec and state");
        string fixtureRoot = Path.GetDirectoryName(Path.GetFullPath(file))!;
        string key = Path.Combine(fixtureRoot, "client-key"), passphrase = Path.Combine(fixtureRoot, "key-passphrase.txt");
        if (File.Exists(key) && File.Exists(passphrase))
        {
            var keyProfile = new ServerProfile { Name = "verified encrypted key smoke", Host = profile.Host, Port = profile.Port, Username = profile.Username,
                TimeoutSeconds = 10, Authentication = AuthenticationKind.PrivateKey, PrivateKeyPath = key };
            await using var keySession = new ServerSession(keyProfile, new AppStore(Path.Combine(Environment.CurrentDirectory, ".tools", "auth-linux", Guid.NewGuid().ToString("N"))));
            await keySession.ConnectAsync(new((await File.ReadAllTextAsync(passphrase)).TrimEnd('\r', '\n')), (host, ct) =>
            { ct.ThrowIfCancellationRequested(); return Task.FromResult(host.Fingerprint == Read("ExpectedHostFingerprint") ? TrustDecision.Session : throw new IOException("Independent fixture fingerprint mismatch")); });
            Check(keySession.State == SessionState.Connected && keySession.SftpAvailable && keySession.ShellAvailable, "real Alpine encrypted Ed25519 passphrase authenticates independent exec and SFTP");
            using var keyShell = await keySession.CreateShellAsync(); using var keyExport = await keySession.OpenExportSourceAsync();
            Check((await keySession.ExecuteAsync("printf key-alive")).Output == "key-alive" && keySession.State == SessionState.Connected, "encrypted Ed25519 authentication also supports separate PTY and native export transports");
        }
        Console.WriteLine($"{passed} verified Alpine auth checks passed (headless component/network)");
    }

    private sealed class AsyncExport(RemoteExportSource source) : IAsyncDisposable
    { public ValueTask DisposeAsync() { source.Dispose(); return ValueTask.CompletedTask; } }
}

