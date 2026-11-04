#if CORE_TESTS
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Renci.SshNet.Security;
using SSHExplorer.Core;

/// <summary>SSH agent protocol and SSH.NET key-source checks against an in-memory agent with real RSA and ECDSA keys.</summary>
internal static class AgentTests
{
    public static async Task RunAsync(TestSuite t)
    {
        using var rsa = RSA.Create(2048); using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var agent = new FakeAgent(rsa, ecdsa);
        var client = new SshAgentClient(agent.Connect, "Тестовый агент");

        await t.Run("agent: identities list RSA and ECDSA with comments and SHA256 fingerprints; certificates are listed but skipped as keys", () =>
        {
            var ids = client.ListIdentities();
            TestSuite.True(ids.Select(i => i.Algorithm).SequenceEqual(["ssh-rsa", "ecdsa-sha2-nistp256", "ssh-ed25519-cert-v01@openssh.com"]), string.Join(",", ids.Select(i => i.Algorithm)));
            TestSuite.True(ids[0].Comment == "rsa@fixture" && ids[0].Fingerprint.StartsWith("SHA256:", StringComparison.Ordinal) && !ids[0].Fingerprint.EndsWith('='), ids[0].Fingerprint);
            var source = new AgentKeySource(client);
            TestSuite.True(source.HostKeyAlgorithms.Select(a => a.Name).SequenceEqual(["rsa-sha2-512", "rsa-sha2-256", "ssh-rsa", "ecdsa-sha2-nistp256"]), string.Join(",", source.HostKeyAlgorithms.Select(a => a.Name)));
        });
        await t.Run("agent: rsa-sha2-512, rsa-sha2-256 and ECDSA signatures from the agent verify with the public key", () =>
        {
            var source = new AgentKeySource(client); var data = Encoding.UTF8.GetBytes("session-id + userauth request");
            foreach (var algorithm in source.HostKeyAlgorithms.OfType<KeyHostAlgorithm>())
            {
                var (name, signature) = SplitSignature(algorithm.Sign(data));
                TestSuite.Equal(algorithm.Name, name);
                bool ok = name switch
                {
                    "rsa-sha2-512" => rsa.VerifyData(data, signature, HashAlgorithmName.SHA512, RSASignaturePadding.Pkcs1),
                    "rsa-sha2-256" => rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
                    "ssh-rsa" => rsa.VerifyData(data, signature, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1),
                    _ => ecdsa.VerifyData(data, EcdsaFromSsh(signature), HashAlgorithmName.SHA256),
                };
                TestSuite.True(ok, "signature verifies: " + name);
            }
            TestSuite.True(agent.Flags.SequenceEqual([4u, 2u, 0u, 0u]), "RSA requests carry SHA-512/SHA-256 flags: " + string.Join(",", agent.Flags));
        });
        await t.Run("agent: refusal, wrong algorithm, truncated reply and unavailable agent are explicit errors", () =>
        {
            var source = new AgentKeySource(client); var first = (KeyHostAlgorithm)source.HostKeyAlgorithms.First();
            agent.Refuse = true; TestSuite.Throws<SshAgentException>(() => first.Sign([1])); agent.Refuse = false;
            agent.LieAlgorithm = true; TestSuite.Throws<SshAgentException>(() => first.Sign([1])); agent.LieAlgorithm = false;
            agent.Truncate = true; TestSuite.Throws<SshAgentException>(() => client.ListIdentities()); agent.Truncate = false;
            var missing = new SshAgentClient(() => throw new IOException("pipe not found"), "Агент OpenSSH Windows");
            try { missing.ListIdentities(); throw new Exception("expected failure"); }
            catch (SshAgentException ex) { TestSuite.True(ex.Message.Contains("недоступен", StringComparison.Ordinal) && ErrorMessages.Describe(ex) == ex.Message, ex.Message); }
        });
        await t.Run("agent: SSH_AUTH_SOCK selects a named pipe or a Unix socket; profile kind Agent keeps old indexes", () =>
        {
            var previous = Environment.GetEnvironmentVariable("SSH_AUTH_SOCK");
            try
            {
                Environment.SetEnvironmentVariable("SSH_AUTH_SOCK", @"\\.\pipe\custom-agent");
                TestSuite.True(SshAgentEndpoint.Default().Description.Contains("custom-agent", StringComparison.Ordinal));
                Environment.SetEnvironmentVariable("SSH_AUTH_SOCK", "/tmp/agent.sock");
                TestSuite.True(SshAgentEndpoint.Default().Description.Contains("/tmp/agent.sock", StringComparison.Ordinal));
            }
            finally { Environment.SetEnvironmentVariable("SSH_AUTH_SOCK", previous); }
            TestSuite.True((int)AuthenticationKind.Password == 0 && (int)AuthenticationKind.PrivateKey == 1 && (int)AuthenticationKind.KeyboardInteractive == 2 && (int)AuthenticationKind.Agent == 3, "enum order is stable");
        });
        await Task.CompletedTask;
    }

    static (string Name, byte[] Signature) SplitSignature(byte[] blob)
    {
        int nameLength = BinaryPrimitives.ReadInt32BigEndian(blob); var name = Encoding.ASCII.GetString(blob, 4, nameLength);
        int sigLength = BinaryPrimitives.ReadInt32BigEndian(blob.AsSpan(4 + nameLength)); return (name, blob.AsSpan(8 + nameLength, sigLength).ToArray());
    }

    static byte[] EcdsaFromSsh(byte[] sshSignature)
    {
        int offset = 0; byte[] Mpint() { int l = BinaryPrimitives.ReadInt32BigEndian(sshSignature.AsSpan(offset)); var v = sshSignature.AsSpan(offset + 4, l).ToArray(); offset += 4 + l; return v; }
        static byte[] Fixed(byte[] v) { var trimmed = v.SkipWhile((b, i) => b == 0 && v.Length - i > 32).ToArray(); var r = new byte[32]; trimmed.CopyTo(r, 32 - trimmed.Length); return r; }
        return [.. Fixed(Mpint()), .. Fixed(Mpint())];
    }

    /// <summary>Protocol-level agent: answers each request on its own stream, like an agent pipe.</summary>
    sealed class FakeAgent(RSA rsa, ECDsa ecdsa)
    {
        public bool Refuse, LieAlgorithm, Truncate;
        public List<uint> Flags { get; } = [];
        readonly byte[] rsaBlob = Blob(w => { w.Str("ssh-rsa"); var p = rsa.ExportParameters(false); w.Mpint(p.Exponent!); w.Mpint(p.Modulus!); });
        readonly byte[] ecBlob = Blob(w => { w.Str("ecdsa-sha2-nistp256"); w.Str("nistp256"); var p = ecdsa.ExportParameters(false); w.Bytes([0x04, .. p.Q.X!, .. p.Q.Y!]); });

        public Stream Connect() => new Exchange(this);

        byte[] Handle(byte[] request)
        {
            if (request[0] == 11)
                return Message(w =>
                {
                    w.Byte(12); w.UInt(3);
                    w.Bytes(rsaBlob); w.Str("rsa@fixture"); w.Bytes(ecBlob); w.Str("ec@fixture");
                    w.Bytes(Blob(c => { c.Str("ssh-ed25519-cert-v01@openssh.com"); c.Bytes(new byte[32]); })); w.Str("cert@fixture");
                });
            if (request[0] == 13)
            {
                int o = 1; byte[] Read() { int l = BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(o)); var v = request.AsSpan(o + 4, l).ToArray(); o += 4 + l; return v; }
                var key = Read(); var data = Read(); uint flags = BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(o)); Flags.Add(flags);
                if (Refuse) return [5];
                string name; byte[] signature;
                if (key.SequenceEqual(rsaBlob))
                {
                    (name, var hash) = flags == 4 ? ("rsa-sha2-512", HashAlgorithmName.SHA512) : flags == 2 ? ("rsa-sha2-256", HashAlgorithmName.SHA256) : ("ssh-rsa", HashAlgorithmName.SHA1);
                    signature = rsa.SignData(data, hash, RSASignaturePadding.Pkcs1);
                }
                else
                {
                    name = "ecdsa-sha2-nistp256"; var raw = ecdsa.SignData(data, HashAlgorithmName.SHA256);
                    signature = Blob(w => { w.Mpint(raw[..32]); w.Mpint(raw[32..]); });
                }
                if (LieAlgorithm) name = "ssh-dss";
                return Message(w => { w.Byte(14); w.Bytes(Blob(s => { s.Str(name); s.Bytes(signature); })); });
            }
            return [5];
        }

        static byte[] Blob(Action<Writer> build) { var w = new Writer(); build(w); return w.ToArray(); }
        static byte[] Message(Action<Writer> build) => Blob(build);

        sealed class Exchange(FakeAgent agent) : Stream
        {
            readonly MemoryStream written = new(); MemoryStream? reply;
            public override void Write(byte[] buffer, int offset, int count) => written.Write(buffer, offset, count);
            public override void Flush()
            {
                var all = written.ToArray(); int length = BinaryPrimitives.ReadInt32BigEndian(all);
                var body = agent.Handle(all.AsSpan(4, length).ToArray());
                var frame = new byte[4 + body.Length]; BinaryPrimitives.WriteInt32BigEndian(frame, body.Length); body.CopyTo(frame, 4);
                if (agent.Truncate) frame = frame[..Math.Min(frame.Length, 9)];
                reply = new MemoryStream(frame);
            }
            public override int Read(byte[] buffer, int offset, int count) => reply?.Read(buffer, offset, count) ?? 0;
            public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException(); public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        }
    }

    sealed class Writer
    {
        readonly MemoryStream s = new();
        public void Byte(byte b) => s.WriteByte(b);
        public void UInt(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); s.Write(b); }
        public void Bytes(byte[] v) { UInt((uint)v.Length); s.Write(v); }
        public void Str(string v) => Bytes(Encoding.UTF8.GetBytes(v));
        public void Mpint(byte[] unsignedBigEndian) { var v = unsignedBigEndian.SkipWhile(b => b == 0).ToArray(); if (v.Length > 0 && (v[0] & 0x80) != 0) v = [0, .. v]; Bytes(v); }
        public byte[] ToArray() => s.ToArray();
    }
}
#endif
