using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Security;
using Renci.SshNet.Security.Cryptography;

namespace SSHExplorer.Core;

/// <summary>A public key held by an SSH agent. The private key never leaves the agent.</summary>
public sealed record AgentIdentity(string Algorithm, byte[] Blob, string Comment)
{
    public string Fingerprint => "SHA256:" + Convert.ToBase64String(SHA256.HashData(Blob)).TrimEnd('=');
}

public sealed class SshAgentException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Minimal client of the SSH agent protocol (draft-miller-ssh-agent): list identities and sign.
/// Each request opens its own connection, so a restarted agent or a locked Pageant is reported per request.
/// </summary>
public sealed class SshAgentClient(Func<Stream> connect, string description)
{
    const byte Failure = 5, RequestIdentities = 11, IdentitiesAnswer = 12, SignRequest = 13, SignResponse = 14;
    public const uint RsaSha256 = 2, RsaSha512 = 4;
    const int MaxMessage = 256 * 1024;

    public string Description => description;

    public IReadOnlyList<AgentIdentity> ListIdentities()
    {
        var reply = Request([RequestIdentities]);
        if (reply.Length == 0 || reply[0] != IdentitiesAnswer) throw new SshAgentException($"{description} отклонил запрос списка ключей.");
        var reader = new Reader(reply, 1); uint count = reader.UInt32();
        if (count > 1024) throw new SshAgentException($"{description} вернул некорректный список ключей.");
        var result = new List<AgentIdentity>((int)count);
        for (uint i = 0; i < count; i++)
        {
            var blob = reader.Bytes(); var comment = Encoding.UTF8.GetString(reader.Bytes());
            result.Add(new AgentIdentity(new Reader(blob, 0).String(), blob, comment));
        }
        return result;
    }

    /// <summary>Returns the agent's SSH signature blob: string algorithm, string signature.</summary>
    public byte[] Sign(byte[] keyBlob, byte[] data, uint flags)
    {
        var message = new MemoryStream(); message.WriteByte(SignRequest); WriteBytes(message, keyBlob); WriteBytes(message, data); WriteUInt32(message, flags);
        var reply = Request(message.ToArray());
        if (reply.Length > 0 && reply[0] == Failure) throw new SshAgentException($"{description} отказал в подписи (ключ заблокирован, подтверждение отклонено или алгоритм не поддерживается).");
        if (reply.Length == 0 || reply[0] != SignResponse) throw new SshAgentException($"{description} вернул неожиданный ответ на запрос подписи.");
        return new Reader(reply, 1).Bytes();
    }

    byte[] Request(byte[] payload)
    {
        Stream stream;
        try { stream = connect(); }
        catch (Exception ex) when (ex is IOException or TimeoutException or SocketException or UnauthorizedAccessException)
        { throw new SshAgentException($"{description} недоступен: {ex.Message}", ex); }
        using (stream)
        {
            try
            {
                var frame = new byte[4 + payload.Length]; BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length); payload.CopyTo(frame, 4);
                stream.Write(frame); stream.Flush();
                var header = new byte[4]; stream.ReadExactly(header);
                var length = BinaryPrimitives.ReadUInt32BigEndian(header);
                if (length == 0 || length > MaxMessage) throw new SshAgentException($"{description} вернул сообщение недопустимой длины {length}.");
                var reply = new byte[length]; stream.ReadExactly(reply); return reply;
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException) { throw new SshAgentException($"{description} закрыл соединение: {ex.Message}", ex); }
        }
    }

    static void WriteUInt32(Stream s, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); s.Write(b); }
    static void WriteBytes(Stream s, byte[] v) { WriteUInt32(s, (uint)v.Length); s.Write(v); }

    internal ref struct Reader(byte[] data, int offset)
    {
        int position = offset;
        public uint UInt32()
        {
            if (position + 4 > data.Length) throw new SshAgentException("Ответ агента обрезан.");
            var v = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position)); position += 4; return v;
        }
        public byte[] Bytes()
        {
            var length = UInt32(); if (length > data.Length - position) throw new SshAgentException("Ответ агента обрезан.");
            var v = data.AsSpan(position, (int)length).ToArray(); position += (int)length; return v;
        }
        public string String() => Encoding.ASCII.GetString(Bytes());
    }
}

/// <summary>Where the agent lives: SSH_AUTH_SOCK (pipe or Unix socket), else the Windows OpenSSH agent pipe.</summary>
public static class SshAgentEndpoint
{
    public const string OpenSshPipe = "openssh-ssh-agent";

    public static SshAgentClient Default(int timeoutMilliseconds = 3000)
    {
        var sock = Environment.GetEnvironmentVariable("SSH_AUTH_SOCK");
        if (!string.IsNullOrWhiteSpace(sock))
        {
            if (TryPipeName(sock, out var pipe)) return Pipe(pipe, timeoutMilliseconds, $"SSH-агент ({sock})");
            return Unix(sock, $"SSH-агент ({sock})");
        }
        if (OperatingSystem.IsWindows()) return Pipe(OpenSshPipe, timeoutMilliseconds, "Агент OpenSSH Windows");
        throw new SshAgentException("SSH-агент не найден: переменная SSH_AUTH_SOCK не задана.");
    }

    public static SshAgentClient Pipe(string name, int timeoutMilliseconds, string description) => new(() =>
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None);
        try { pipe.Connect(timeoutMilliseconds); return pipe; }
        catch { pipe.Dispose(); throw; }
    }, description);

    public static SshAgentClient Unix(string path, string description) => new(() =>
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try { socket.Connect(new UnixDomainSocketEndPoint(path)); return new NetworkStream(socket, ownsSocket: true); }
        catch { socket.Dispose(); throw; }
    }, description);

    static bool TryPipeName(string value, out string name)
    {
        foreach (var prefix in new[] { @"\\.\pipe\", "//./pipe/" })
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { name = value[prefix.Length..]; return true; }
        name = ""; return false;
    }
}

/// <summary>
/// Exposes agent identities to SSH.NET public-key authentication. Signing is delegated to the agent;
/// RSA keys are offered as rsa-sha2-512, rsa-sha2-256 and ssh-rsa, in that order.
/// </summary>
public sealed class AgentKeySource : IPrivateKeySource
{
    public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms { get; }
    public IReadOnlyList<AgentIdentity> Identities { get; }

    public AgentKeySource(SshAgentClient agent, Func<AgentIdentity, bool>? filter = null)
    {
        Identities = agent.ListIdentities().Where(i => filter?.Invoke(i) ?? true).ToArray();
        var algorithms = new List<HostAlgorithm>();
        foreach (var identity in Identities)
        {
            Key key;
            try
            {
                var data = new SshKeyData(identity.Blob);
                key = identity.Algorithm switch
                {
                    "ssh-rsa" => new RsaKey(data),
                    "ssh-ed25519" => new ED25519Key(data),
                    "ecdsa-sha2-nistp256" or "ecdsa-sha2-nistp384" or "ecdsa-sha2-nistp521" => new EcdsaKey(data),
                    _ => throw new NotSupportedException(identity.Algorithm),
                };
            }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException or FormatException or InvalidOperationException) { continue; } // e.g. certificates, sk-* hardware keys
            if (identity.Algorithm == "ssh-rsa")
            {
                algorithms.Add(new KeyHostAlgorithm("rsa-sha2-512", key, new AgentSignature(agent, identity, "rsa-sha2-512", SshAgentClient.RsaSha512, key)));
                algorithms.Add(new KeyHostAlgorithm("rsa-sha2-256", key, new AgentSignature(agent, identity, "rsa-sha2-256", SshAgentClient.RsaSha256, key)));
            }
            algorithms.Add(new KeyHostAlgorithm(identity.Algorithm, key, new AgentSignature(agent, identity, identity.Algorithm, 0, key)));
        }
        HostKeyAlgorithms = algorithms;
    }

    sealed class AgentSignature(SshAgentClient agent, AgentIdentity identity, string algorithm, uint flags, Key key) : DigitalSignature
    {
        public override byte[] Sign(byte[] input)
        {
            var blob = agent.Sign(identity.Blob, input, flags);
            var reader = new SshAgentClient.Reader(blob, 0);
            var returned = reader.String();
            if (!string.Equals(returned, algorithm, StringComparison.Ordinal)) throw new SshAgentException($"{agent.Description} подписал алгоритмом {returned} вместо {algorithm}.");
            return reader.Bytes();
        }
        // Client authentication never verifies its own signature; delegate for completeness.
        public override bool Verify(byte[] input, byte[] signature) => key.VerifySignature(input, signature);
    }
}
