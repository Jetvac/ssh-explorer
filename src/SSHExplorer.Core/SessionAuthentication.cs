using Renci.SshNet;
using Renci.SshNet.Common;

namespace SSHExplorer.Core;

internal static class SessionAuthentication
{
    internal static bool HasPrompt(SessionCredentials credentials) => credentials.CancellablePrompt is not null || credentials.Prompt is not null;

    internal static async Task<string> RespondAsync(SessionCredentials credentials, InteractivePrompt prompt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Task<string?> response;
        if (credentials.CancellablePrompt is { } cancellable) response = cancellable(prompt, ct);
        else if (credentials.Prompt is { } legacy) response = legacy(prompt);
        else throw new SshAuthenticationException("Для keyboard-interactive требуется явный интерактивный ответ.");
        var result = await response.WaitAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return result ?? throw new OperationCanceledException("Интерактивный вход отменён.", ct);
    }

    /// <summary>Replaceable for tests and diagnostics; defaults to SSH_AUTH_SOCK or the Windows OpenSSH agent.</summary>
    internal static Func<SshAgentClient> AgentFactory { get; set; } = () => SshAgentEndpoint.Default();
    /// <summary>Optional restriction of agent identities (tests offer one key type at a time).</summary>
    internal static Func<AgentIdentity, bool>? AgentFilter { get; set; }

    internal static AuthenticationMethod[] CreateMethods(ServerProfile profile, SessionCredentials credentials,
        Action<IDisposable> register, Func<InteractivePrompt, string> respond)
    {
        List<AuthenticationMethod> methods = [];
        if (profile.Authentication == AuthenticationKind.PrivateKey)
        {
            var key = string.IsNullOrEmpty(credentials.Secret) ? new PrivateKeyFile(profile.PrivateKeyPath) : new PrivateKeyFile(profile.PrivateKeyPath, credentials.Secret);
            register(key);
            methods.Add(new PrivateKeyAuthenticationMethod(profile.Username, key));
        }
        else if (profile.Authentication == AuthenticationKind.Agent)
        {
            // Identities are listed now; each signature is requested from the agent during authentication.
            var source = new AgentKeySource(AgentFactory(), AgentFilter);
            if (source.HostKeyAlgorithms.Count == 0) throw new SshAgentException("В SSH-агенте нет подходящих ключей. Добавьте ключ (ssh-add) или выберите другой способ входа.");
            methods.Add(new PrivateKeyAuthenticationMethod(profile.Username, source));
        }
        else if (profile.Authentication == AuthenticationKind.Password)
            methods.Add(new PasswordAuthenticationMethod(profile.Username, credentials.Secret));

        // The callback, never Secret or a guess based on prompt text, supplies every PAM/MFA answer.
        // Include it after password/key to support servers requiring an additional authentication method.
        if (HasPrompt(credentials) || profile.Authentication == AuthenticationKind.KeyboardInteractive)
        {
            var interactive = new KeyboardInteractiveAuthenticationMethod(profile.Username);
            interactive.AuthenticationPrompt += (_, args) =>
            {
                foreach (var prompt in args.Prompts)
                    prompt.Response = respond(new InteractivePrompt(args.Instruction, prompt.Request, prompt.IsEchoed));
            };
            methods.Add(interactive);
        }
        return methods.ToArray();
    }
}
