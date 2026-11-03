using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Renci.SshNet;
using Renci.SshNet.Common;
using SSHExplorer.Core;

internal static class AuthenticationScenarios
{
    private static int passed;
    private static void Check(bool condition, string label)
    { if (!condition) throw new Exception("FAIL " + label); Console.WriteLine("PASS " + label); passed++; }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static AppStore Store() => new(Path.Combine(Environment.CurrentDirectory, ".tools", "auth-tests", Guid.NewGuid().ToString("N")));
    private static ServerProfile Profile() => new() { Name = "isolated auth regression", Host = "127.0.0.1", Port = 1, Username = "test", TimeoutSeconds = 3 };

    public static async Task RunUnitAsync()
    {
        passed = 0;
        var profile = Profile();
        const string secret = "  пароль ' \" Ω  ";
        var methods = SessionAuthentication.CreateMethods(profile, new SessionCredentials(secret), _ => throw new Exception("Unexpected key"), _ => throw new Exception("Unexpected prompt"));
        Check(methods.Length == 1 && methods[0] is PasswordAuthenticationMethod,
            "password protocol has no implicit PAM fallback without explicit callback");
        foreach (var method in methods) method.Dispose();
        methods = SessionAuthentication.CreateMethods(profile, new SessionCredentials(secret, CancellablePrompt: (_, _) => Task.FromResult<string?>("OTP")), _ => { }, _ => "OTP");
        Check(methods.Select(x => x.Name).SequenceEqual(["password", "keyboard-interactive"]), "explicit callback permits password plus keyboard-interactive MFA");
        foreach (var method in methods) method.Dispose();
        profile.Authentication = AuthenticationKind.KeyboardInteractive;
        methods = SessionAuthentication.CreateMethods(profile, new SessionCredentials(secret), _ => { }, _ => "OTP");
        Check(methods.Length == 1 && methods[0] is KeyboardInteractiveAuthenticationMethod, "keyboard-interactive profile never adds password authentication");
        foreach (var method in methods) method.Dispose();
        foreach (var text in new[] { "Password:", "One-time password:", "Verification code:", "Пароль:" })
            await ThrowsAsync<SshAuthenticationException>(() => SessionAuthentication.RespondAsync(new SessionCredentials(secret), new("", text, false), default));
        Check(true, "every PAM prompt requires explicit response and never guesses saved secret from text");
        string? promptText = null;
        var answer = await SessionAuthentication.RespondAsync(new SessionCredentials(secret, _ => throw new Exception("Legacy callback should not run"),
            (prompt, _) => { promptText = prompt.Prompt; return Task.FromResult<string?>(" 0'Ω "); }), new("", "OTP:", false), default);
        Check(answer == " 0'Ω " && promptText == "OTP:", "cancellable callback has priority and response is unmodified");
        Check(!new SessionCredentials(secret).ToString().Contains(secret, StringComparison.Ordinal), "credentials diagnostic string redacts password");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); int calls = 0;
            await ThrowsAsync<OperationCanceledException>(() => SessionAuthentication.RespondAsync(new SessionCredentials(secret, CancellablePrompt: (_, _) => { calls++; return Task.FromResult<string?>("late"); }), new("", "OTP:", false), cancelled.Token));
            Check(calls == 0, "cancelled attempt never dispatches a queued interactive callback");
        }
        using (var cancelled = new CancellationTokenSource())
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = SessionAuthentication.RespondAsync(new SessionCredentials(secret, CancellablePrompt: async (_, ct) =>
            { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return "never"; }), new("", "OTP:", false), cancelled.Token);
            await entered.Task; cancelled.Cancel(); await ThrowsAsync<OperationCanceledException>(() => pending);
            Check(pending.IsCanceled, "operation cancellation reaches pending interactive callback");
        }
        using (var cancelled = new CancellationTokenSource())
        {
            var never = new TaskCompletionSource<string?>();
            var pending = SessionAuthentication.RespondAsync(new SessionCredentials(secret, _ => never.Task), new("", "OTP:", false), cancelled.Token);
            cancelled.Cancel(); await ThrowsAsync<OperationCanceledException>(() => pending);
            never.SetResult("late");
            Check(pending.IsCanceled, "legacy callback cannot deliver a late answer after operation cancellation");
        }
        await ThrowsAsync<OperationCanceledException>(() => SessionAuthentication.RespondAsync(new SessionCredentials(secret, CancellablePrompt: (_, _) => Task.FromResult<string?>(null)), new("", "OTP:", false), default));
        Check(true, "interactive dialog cancellation remains cancellation instead of empty OTP");
        Exception caught; try { throw new InvalidOperationException(secret); } catch (Exception ex) { caught = ex; }
        var diagnostic = ConnectionStageEvent.Create(Guid.NewGuid(), Guid.NewGuid(), null, ConnectionStage.Authentication, ConnectionStageOutcome.Failed, caught);
        Check(!JsonSerializer.Serialize(diagnostic).Contains(secret, StringComparison.Ordinal) && diagnostic.ExceptionType == typeof(InvalidOperationException).FullName && diagnostic.HResult == caught.HResult && diagnostic.Stack?.Contains(nameof(RunUnitAsync)) == true,
            "diagnostic event retains exception type HRESULT and methods without exception message");
        await using (var session = new ServerSession(Profile(), Store()))
        {
            List<ConnectionStageEvent> events = []; session.StageChanged += (_, value) => events.Add(value);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); var operation = Guid.NewGuid();
            await ThrowsAsync<OperationCanceledException>(() => session.ConnectAsync(new(secret), _ => Task.FromResult(TrustDecision.Reject), cancellation.Token, operation));
            Check(events.Count == 2 && events[0].Outcome == ConnectionStageOutcome.Started && events[1].Outcome == ConnectionStageOutcome.Cancelled && events.All(x => x.OperationId == operation), "pre-cancelled connection has one correlated terminal diagnostic and no network");
        }
        await using (var session = new ServerSession(Profile(), Store()))
        {
            var one = session.DisposeAsync().AsTask(); var two = session.DisposeAsync().AsTask(); await Task.WhenAll(one, two);
            Check(ReferenceEquals(one, two) && session.State == SessionState.Closed && session.Lifetime.IsCancellationRequested, "concurrent session disposal shares cleanup task and closes once");
        }
        Console.WriteLine($"{passed} auth component checks passed (no windows, no network)");
    }
}
