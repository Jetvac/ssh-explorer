using System.Diagnostics;

namespace SSHExplorer.Core;

public enum ConnectionStage { Connect, Transport, HostTrust, Authentication, InteractivePrompt, ExecProbe, Sftp, Pty }
public enum ConnectionStageOutcome { Started, Succeeded, Failed, Cancelled }

/// <summary>Contains no endpoint, username, exception message, prompt text, command or response.</summary>
public sealed record ConnectionStageEvent(Guid SessionId, Guid OperationId, Guid? ParentOperationId,
    ConnectionStage Stage, ConnectionStageOutcome Outcome, string? ExceptionType = null, int? HResult = null, string? Stack = null)
{
    internal static ConnectionStageEvent Create(Guid session, Guid operation, Guid? parent, ConnectionStage stage,
        ConnectionStageOutcome outcome, Exception? error = null) => new(session, operation, parent, stage, outcome,
            error?.GetType().FullName, error?.HResult, error is null ? null : new StackTrace(error, false).ToString());
}
