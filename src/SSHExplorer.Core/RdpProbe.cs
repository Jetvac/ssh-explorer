using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace SSHExplorer.Core;

/// <summary>
/// Reads the TLS key of a remote desktop server before the Windows control connects: an X.224 connection request
/// asking for TLS/NLA, then a TLS handshake whose certificate is only recorded. Nothing is sent after it, so no
/// credentials leave. The control then connects to the same address and port.
/// </summary>
public static class RdpProbe
{
    /// <summary>TPKT + X.224 Connection Request + RDP_NEG_REQ (PROTOCOL_SSL | PROTOCOL_HYBRID).</summary>
    static readonly byte[] Request = [0x03, 0x00, 0x00, 0x13, 0x0E, 0xE0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x08, 0x00, 0x03, 0x00, 0x00, 0x00];

    public sealed record Result(byte[]? PublicKey, string? Subject, string? Problem);

    public static async Task<Result> ReadKeyAsync(string host, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct); limit.CancelAfter(timeout);
        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host.Trim('[', ']'), port, limit.Token).ConfigureAwait(false);
            var stream = tcp.GetStream();
            await stream.WriteAsync(Request, limit.Token).ConfigureAwait(false);
            var header = new byte[4]; await stream.ReadExactlyAsync(header, limit.Token).ConfigureAwait(false);
            int length = header[2] << 8 | header[3];
            if (header[0] != 3 || length is < 11 or > 512) return new(null, null, "Сервер ответил не как удалённый рабочий стол.");
            var body = new byte[length - 4]; await stream.ReadExactlyAsync(body, limit.Token).ConfigureAwait(false);
            // X.224 CC (7 bytes) then RDP_NEG_RSP (type 2) or RDP_NEG_FAILURE (type 3).
            if (body.Length >= 15 && body[7] == 3) return new(null, null, "Сервер не поддерживает TLS: сертификат проверит Windows.");
            if (body.Length < 15 || body[7] != 2 || (body[11] & 0x03) == 0) return new(null, null, "Сервер не согласился на TLS: сертификат проверит Windows.");
            X509Certificate2? seen = null;
            using var tls = new SslStream(stream, false, (_, certificate, _, _) => { if (certificate is not null) seen = new X509Certificate2(certificate); return true; });
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host.Trim('[', ']'), EnabledSslProtocols = System.Security.Authentication.SslProtocols.None }, limit.Token).ConfigureAwait(false);
            return seen is null ? new(null, null, "Сервер не предъявил сертификат.") : new(seen.GetPublicKey(), seen.Subject, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(null, null, "Сервер не ответил вовремя."); }
        catch (Exception ex) when (ex is SocketException or IOException or System.Security.Authentication.AuthenticationException or EndOfStreamException) { return new(null, null, ErrorMessages.Describe(ex)); }
    }
}
