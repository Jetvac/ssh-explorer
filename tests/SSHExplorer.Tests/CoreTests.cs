#if CORE_TESTS
using System.Security.Cryptography;
using SSHExplorer.Core;

internal static class CoreTests
{
    public static async Task RunAsync(TestSuite t)
    {
        await t.Run("POSIX paths preserve backslash, case and metacharacters", () =>
        {
            TestSuite.Equal("/home/Имя/a\\b/$x;z", PosixPath.Normalize("~/./a\\b/$x;z", "/home/Имя"));
            TestSuite.Equal("/a/b", PosixPath.Normalize("//a//x/../b/"));
            TestSuite.Equal("/", PosixPath.Normalize("/../../../"));
            TestSuite.True(!PosixPath.IsSameOrDescendant("/AB/c", "/ab"));
            TestSuite.True(!PosixPath.IsSameOrDescendant("/home/abc", "/home/a"));
            TestSuite.True(PosixPath.IsSameOrDescendant("/home/a/b", "/home/a"));
            TestSuite.Equal("/", PosixPath.Parent("/a"));
            TestSuite.Equal("/", PosixPath.Parent("/"));
            foreach (var name in new[] { "кириллица", "a b", "'", "-rf", "$HOME;echo bad", "a\nb", "a\\b" })
                PosixPath.ValidateName(name);
        });
        await t.Run("POSIX rejects NUL, relative paths and invalid names", () =>
        {
            foreach (var path in new[] { "relative", "~another", "/a\0b" })
                TestSuite.Throws<ArgumentException>(() => PosixPath.Normalize(path));
            foreach (var name in new[] { "", ".", "..", "a/b", "a\0b" })
                TestSuite.Throws<ArgumentException>(() => PosixPath.ValidateName(name));
            TestSuite.Throws<ArgumentException>(() => PosixPath.Quote("a\0b"));
            TestSuite.Equal("'a'\"'\"'b\n;$x'", PosixPath.Quote("a'b\n;$x"));
        });
        await t.Run("pinned SSH.NET native SFTP bindings validate before use", () => SftpProtocolAdapter.ValidateBindings());
        await t.Run("Windows names are mapped and cannot escape chosen directory", () =>
        {
            if (!OperatingSystem.IsWindows()) { Console.WriteLine("SKIP Windows file-name boundary: not a Windows host"); return; }
            TestSuite.Equal("_CON", WindowsNames.SafeName("CON"));
            TestSuite.Equal("_nul.txt", WindowsNames.SafeName("nul.txt"));
            TestSuite.Equal("a%003Ab", WindowsNames.SafeName("a:b"));
            TestSuite.Equal("trailing%002E", WindowsNames.SafeName("trailing."));
            var root = Path.Combine(Environment.CurrentDirectory, ".tools", "download-boundary");
            TestSuite.Throws<IOException>(() => WindowsNames.ContainedPath(root, "../outside"));
            TestSuite.Throws<IOException>(() => WindowsNames.ContainedPath(root, "..\\outside"));
            TestSuite.Throws<IOException>(() => WindowsNames.ContainedPath(root, ".."));
            TestSuite.True(WindowsNames.ContainedPath(root, "safe.txt").StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        });
        await t.Run("host fingerprint is SHA256 public key, endpoint normalized", () =>
        {
            var bytes = new byte[] { 0, 1, 2, 255 };
            var expected = "SHA256:" + Convert.ToBase64String(SHA256.HashData(bytes)).TrimEnd('=');
            TestSuite.Equal(expected, HostTrustStore.Fingerprint(bytes));
            TestSuite.Equal("example.org", HostTrustStore.NormalizeHost(" EXAMPLE.org. "));
            TestSuite.Equal("2001:db8::1", HostTrustStore.NormalizeHost("[2001:DB8::1]"));
        });
        await t.Run("reject first host key never persists or grants session trust", async () =>
        {
            var store = NewStore(); var session = new HashSet<string>(); int prompts = 0;
            var accepted = await store.Trust.VerifyAsync("server", 22, "ssh-ed25519", [1, 2], session,
                _ => { prompts++; return Task.FromResult(TrustDecision.Reject); }, default);
            TestSuite.True(!accepted);
            TestSuite.Equal(0, store.Trust.Records.Count);
            TestSuite.Equal(0, session.Count);
            TestSuite.Equal(1, prompts);
        });
        await t.Run("temporary host trust belongs to one logical session only", async () =>
        {
            var store = NewStore(); var session = new HashSet<string>(); int prompts = 0;
            Task<TrustDecision> Prompt(HostKeyPrompt _) { prompts++; return Task.FromResult(TrustDecision.Session); }
            TestSuite.True(await store.Trust.VerifyAsync("server", 22, "ssh-ed25519", [1, 2], session, Prompt, default));
            TestSuite.True(await store.Trust.VerifyAsync("server", 22, "ssh-ed25519", [1, 2], session, Prompt, default));
            TestSuite.Equal(1, prompts);
            TestSuite.Equal(0, store.Trust.Records.Count);
            await store.Trust.VerifyAsync("server", 22, "ssh-ed25519", [1, 2], [], Prompt, default);
            TestSuite.Equal(2, prompts);
        });
        await t.Run("temporary trust blocks changed key or algorithm inside its logical session", async () =>
        {
            var store = NewStore(); var session = new HashSet<string>(); int prompts = 0;
            Task<TrustDecision> Prompt(HostKeyPrompt _) { prompts++; return Task.FromResult(TrustDecision.Session); }
            await store.Trust.VerifyAsync("server", 22, "ssh-ed25519", [1, 2], session, Prompt, default);
            await TestSuite.ThrowsAsync<HostKeyChangedException>(() => store.Trust.VerifyAsync("server", 22, "ssh-ed25519", [3, 4], session, Prompt, default));
            await TestSuite.ThrowsAsync<HostKeyChangedException>(() => store.Trust.VerifyAsync("server", 22, "rsa-sha2-512", [1, 2], session, Prompt, default));
            TestSuite.Equal(1, prompts);
            await store.Trust.VerifyAsync("server", 22, "ssh-ed25519", [3, 4], [], Prompt, default);
            TestSuite.Equal(2, prompts);
        });
        await t.Run("persistent trust survives restart and is bound to endpoint and algorithm", async () =>
        {
            var store = NewStore();
            await store.Trust.VerifyAsync("SERVER.", 22, "ssh-ed25519", [1, 2], [], _ => Task.FromResult(TrustDecision.Permanent), default);
            var reopened = new AppStore(store.DirectoryPath); int prompts = 0;
            Task<TrustDecision> Prompt(HostKeyPrompt _) { prompts++; return Task.FromResult(TrustDecision.Reject); }
            TestSuite.True(await reopened.Trust.VerifyAsync("server", 22, "ssh-ed25519", [1, 2], [], Prompt, default));
            TestSuite.Equal(0, prompts);
            TestSuite.True(!await reopened.Trust.VerifyAsync("server", 2222, "ssh-ed25519", [1, 2], [], Prompt, default));
            TestSuite.True(!await reopened.Trust.VerifyAsync("other-server", 22, "ssh-ed25519", [1, 2], [], Prompt, default));
            TestSuite.Equal(2, prompts);
            await TestSuite.ThrowsAsync<HostKeyChangedException>(() => reopened.Trust.VerifyAsync("server", 22, "rsa-sha2-512", [1, 2], [], Prompt, default));
            TestSuite.Equal(2, prompts);
        });
        await t.Run("changed host key is blocked before ordinary trust dialog", async () =>
        {
            var store = NewStore();
            await store.Trust.VerifyAsync("server", 22, "ssh-ed25519", [1, 2], [], _ => Task.FromResult(TrustDecision.Permanent), default);
            int prompts = 0;
            try
            {
                await store.Trust.VerifyAsync("server", 22, "ssh-ed25519", [3, 4], [], _ => { prompts++; return Task.FromResult(TrustDecision.Permanent); }, default);
                throw new Exception("Changed key unexpectedly accepted");
            }
            catch (HostKeyChangedException e)
            {
                TestSuite.True(e.Prompt.Changed);
                TestSuite.Equal(HostTrustStore.Fingerprint([1, 2]), e.Prompt.PreviousFingerprint);
                TestSuite.Equal(HostTrustStore.Fingerprint([3, 4]), e.Prompt.Fingerprint);
            }
            TestSuite.Equal(0, prompts);
            TestSuite.Equal(Convert.ToBase64String([1, 2]), store.Trust.Records.Single().PublicKey);
        });
        await t.Run("simultaneous channels coalesce one logical-session trust prompt", async () =>
        {
            var store = NewStore(); var session = new HashSet<string>(); int prompts = 0;
            async Task<TrustDecision> Prompt(HostKeyPrompt _) { Interlocked.Increment(ref prompts); await Task.Delay(10); return TrustDecision.Session; }
            var accepted = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
                store.Trust.VerifyAsync("server", 22, "ssh-ed25519", [1, 2], session, Prompt, default)));
            TestSuite.True(accepted.All(x => x));
            TestSuite.Equal(1, prompts);
        });
        await t.Run("explicit trust reset is endpoint scoped", async () =>
        {
            var store = NewStore();
            await store.Trust.VerifyAsync("one", 22, "ssh-ed25519", [1], [], _ => Task.FromResult(TrustDecision.Permanent), default);
            await store.Trust.VerifyAsync("two", 22, "ssh-ed25519", [2], [], _ => Task.FromResult(TrustDecision.Permanent), default);
            await store.Trust.ForgetAsync("one", 22, "ssh-ed25519");
            TestSuite.Equal("two", new AppStore(store.DirectoryPath).Trust.Records.Single().Host);
        });
        await t.Run("profiles and settings survive atomic rewrite without inline secrets", async () =>
        {
            var store = NewStore();
            var first = new ServerProfile { Name = "Первый", Host = "one.example", Username = "user", InitialPath = "/home/user" };
            var second = new ServerProfile { Name = "Второй", Host = "::1", Port = 2222, Username = "other", Authentication = AuthenticationKind.PrivateKey, PrivateKeyPath = "C:\\keys\\id", InitialPath = "/" };
            store.Profiles.AddRange([first, second]); store.Settings.TerminalFontSize = 17;
            await store.SaveAsync(); await store.SaveAsync();
            var read = new AppStore(store.DirectoryPath);
            TestSuite.Equal(2, read.Profiles.Count);
            TestSuite.Equal(first.Id, read.Profiles[0].Id);
            TestSuite.Equal("Второй", read.Profiles[1].Name);
            TestSuite.Equal(17, read.Settings.TerminalFontSize);
            var json = await File.ReadAllTextAsync(Path.Combine(store.DirectoryPath, "settings.json"));
            TestSuite.True(!json.Contains("\"Secret\"", StringComparison.Ordinal));
            TestSuite.True(File.Exists(Path.Combine(store.DirectoryPath, "settings.json.bak")));
            await read.DeleteProfileAsync(first.Id);
            TestSuite.Equal(1, new AppStore(store.DirectoryPath).Profiles.Count);
        });
        if (OperatingSystem.IsWindows())
        {
            await t.Run("DPAPI CurrentUser roundtrip is not plaintext in any store file", async () =>
            {
                var store = NewStore(); var id = Guid.NewGuid();
                var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                await store.SaveSecretAsync(id, secret);
                TestSuite.True(string.Equals(secret, await store.ReadSecretAsync(id), StringComparison.Ordinal), "DPAPI roundtrip mismatch");
                foreach (var file in Directory.EnumerateFiles(store.DirectoryPath))
                    TestSuite.True(!(await File.ReadAllTextAsync(file)).Contains(secret, StringComparison.Ordinal), "Secret appeared in plaintext");
                store.DeleteSecret(id);
                TestSuite.Equal<string?>(null, await store.ReadSecretAsync(id));
            });
        }
        await t.Run("closing one logical session leaves the other lifetime independent", async () =>
        {
            var store = NewStore(); var profile = new ServerProfile { Name = "test", Host = "localhost", Username = "test" };
            var one = new ServerSession(profile, store); var two = new ServerSession(profile, store);
            TestSuite.True(one.Id != two.Id);
            await one.DisposeAsync(); await one.DisposeAsync();
            TestSuite.True(one.Lifetime.IsCancellationRequested);
            TestSuite.Equal(SessionState.Closed, one.State);
            TestSuite.True(!two.Lifetime.IsCancellationRequested);
            await two.DisposeAsync();
        });
        foreach (var fail in new[] { true, false })
        {
            await t.Run(fail ? "failed native export unlinks source lifetime and is collectible" : "cancelled native export unlinks source lifetime and is collectible", async () =>
            {
                var store = NewStore();
                await using var source = new ServerSession(new ServerProfile { Name = "isolated lifetime", Host = "unused.invalid", Username = "unused" }, store);
                var reference = await TransientExternalOperation(source, fail);
                for (var attempt = 0; attempt < 12 && reference.IsAlive; attempt++)
                { await Task.Delay(25); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
                TestSuite.True(!source.Lifetime.IsCancellationRequested, "Test source must remain alive while its finished export is collected");
                TestSuite.True(!reference.IsAlive, "A terminal export was retained by its still-open source session");
            });
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> TransientExternalOperation(ServerSession source, bool fail)
    {
        TransferQueue? queue = new(); ExternalTransferOperation? operation = queue.BeginExternalDownload(source, ["/isolated-test-file"]);
        TransferJob? job = operation.Job;
        if (fail) operation.Fail(new IOException("isolated expected export failure")); else operation.Cancel();
        await queue.WaitAsync(job);
        TestSuite.Equal(fail ? TransferState.Failed : TransferState.Cancelled, job.State);
        var reference = new WeakReference(operation); operation = null; job = null; queue = null; return reference;
    }

    private static AppStore NewStore()
    {
        var root = Path.Combine(Environment.CurrentDirectory, ".tools", "test-data", Guid.NewGuid().ToString("N"));
        return new AppStore(root);
    }
}
#endif
