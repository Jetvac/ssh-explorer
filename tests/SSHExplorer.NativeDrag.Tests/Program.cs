using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SSHExplorer.App.NativeDragDrop;
using SSHExplorer.Core;

internal static class Program
{
    [MTAThread]
    static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 3 && args[0] is "--consume" or "--consume-sync") return Consume(args[1], args[2], args[0] == "--consume-sync");
            if (args.Length == 1 && args[0] == "--abi-only") return await AbiOnly();
            var cancelProbe = args.Length == 2 && args[0] == "--cancel";
            var closeProbe = args.Length == 2 && args[0] == "--close-source";
            var skipProbe = args.Length == 2 && args[0] == "--skip-name";
            var zeroGoneProbe = args.Length == 2 && args[0] == "--zero-gone";
            var lostTargetProbe = args.Length == 2 && args[0] == "--lost-target";
            var metadataProbe = args.Length == 2 && args[0] == "--metadata";
            var syncProbe = args.Length == 2 && args[0] == "--sync";
            if (args.Length != 1 && !cancelProbe && !closeProbe && !skipProbe && !zeroGoneProbe && !lostTargetProbe && !metadataProbe && !syncProbe) throw new ArgumentException("Use isolated-config.json or a native test mode with config");
            var configuration = JsonSerializer.Deserialize<Config>(await File.ReadAllTextAsync(args[^1]),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } })!;
            if (configuration.ExpectedHostFingerprint.Length == 0) throw new InvalidOperationException("Independently obtained host fingerprint required");
            var secret = configuration.SecretFile is null ? configuration.Secret : (await File.ReadAllTextAsync(configuration.SecretFile)).TrimEnd('\r', '\n');
            var local = Path.Combine(Environment.CurrentDirectory, ".tools", "native-ole-probe", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(local); var store = new AppStore(Path.Combine(local, "store"));
            var profile = new ServerProfile { Name = "Native OLE test", Host = configuration.Host, Port = configuration.Port, Username = configuration.Username, Authentication = configuration.Authentication, PrivateKeyPath = configuration.PrivateKeyPath, TimeoutSeconds = 20 };
            await using var source = new ServerSession(profile, store);
            await source.ConnectAsync(new SessionCredentials(secret), prompt =>
            { Check(prompt.Fingerprint == configuration.ExpectedHostFingerprint, "independent host fingerprint"); return Task.FromResult(TrustDecision.Session); });
            var remote = PosixPath.Combine(source.Files.HomeDirectory, "sshx-native-ole-" + Guid.NewGuid().ToString("N"));
            await source.Files.CreateDirectoryAsync(remote);
            try
            {
                var tree = Path.Combine(local, "дерево"); Directory.CreateDirectory(Path.Combine(tree, "пустая")); Directory.CreateDirectory(Path.Combine(tree, "вложенная"));
                var payload = RandomNumberGenerator.GetBytes((cancelProbe || closeProbe || lostTargetProbe ? 16 : 1) * 1024 * 1024 + 123);
                await File.WriteAllBytesAsync(Path.Combine(tree, "вложенная", "данные.bin"), payload);
                await File.WriteAllTextAsync(Path.Combine(tree, "первый.txt"), "Привет из SFTP через настоящий COM");
                await File.WriteAllBytesAsync(Path.Combine(tree, "пустой-файл.txt"), []);
                var uploadQueue = new TransferQueue(); var upload = uploadQueue.Upload(source, [tree], remote); await uploadQueue.WaitAsync(upload);
                Check(upload.State == TransferState.Completed, "fixture uploaded via real SFTP");
                if (skipProbe) await source.Files.CreateFileAsync(PosixPath.Combine(remote, "дерево/CON"));
                string? remoteLarge = null;
                if (configuration.LargeFileBytes > 0)
                {
                    if (cancelProbe || closeProbe || skipProbe || zeroGoneProbe || lostTargetProbe || metadataProbe || syncProbe) throw new InvalidOperationException("LargeFileBytes is only used by the full native COM copy probe");
                    remoteLarge = PosixPath.Combine(remote, "large.bin");
                    var created = await source.ExecuteAsync("truncate -s " + configuration.LargeFileBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + PosixPath.Quote(remoteLarge));
                    Check(created.ExitStatus == 0, "isolated fixture creates sparse large source with existing truncate");
                }
                var zero = PosixPath.Combine(remote, "zero-byte.txt");
                if (zeroGoneProbe) await source.Files.CreateFileAsync(zero);
                IReadOnlyList<RemoteEntry> selected = zeroGoneProbe ? [await source.Files.StatAsync(zero)] : await source.Files.ListAsync(remote);
                var queue = new TransferQueue(); if (skipProbe) queue.ResolveConflict = _ => Task.FromResult(ConflictDecision.Skip);
                var data = await ShellVirtualDataObject.CreateAsync(source, selected, Guid.NewGuid().ToString("N"), queue);
                if (zeroGoneProbe) await source.Files.DeleteAsync(zero);
                Check(data.Job is null, "creating native drag data does not start download or queue job");
                var hdrop = new FORMATETC { cfFormat = 15, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
                Check(data.QueryGetData(ref hdrop) < 0, "remote source never advertises nonexistent CF_HDROP paths");
                var largeDescriptor = ShellVirtualDataObject.DescriptorBytes([new("large.bin", new RemoteEntry { IsRegularFile = true, Length = 5L * 1024 * 1024 * 1024 + 123, LastWriteTimeUtc = DateTime.UtcNow }, false)]);
                var high = BitConverter.ToUInt32(largeDescriptor, 4 + 64); var low = BitConverter.ToUInt32(largeDescriptor, 4 + 68);
                Check((((ulong)high << 32) | low) == 5UL * 1024 * 1024 * 1024 + 123, "FILEDESCRIPTOR high/low preserves >4 GiB lengths");
                if (metadataProbe)
                {
                    await StaMetadataProbe(data);
                    Check(!data.IsCancelled && data.Failure is null, "pending STA metadata query does not fail or cancel the drag");
                    Check(data.Job?.BytesTransferred == 0, "metadata preparation transfers no file content");
                    data.Cancel(); if (data.Job is not null) await queue.WaitAsync(data.Job);
                    Console.WriteLine("PASS raw in-process STA IDataObject metadata with background preparation and nonfatal E_PENDING"); return 0;
                }
                var blob = MarshalBlob(data); var blobFile = Path.Combine(local, "objref.bin"); await File.WriteAllBytesAsync(blobFile, blob);
                var destination = Path.Combine(local, "native-target"); Directory.CreateDirectory(destination);
                var info = new ProcessStartInfo { FileName = Environment.ProcessPath!, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(typeof(Program).Assembly.Location);
                info.ArgumentList.Add(syncProbe ? "--consume-sync" : "--consume"); info.ArgumentList.Add(blobFile); info.ArgumentList.Add(destination);
                using var child = Process.Start(info)!; var output = child.StandardOutput.ReadToEndAsync(); var errors = child.StandardError.ReadToEndAsync();
                if (lostTargetProbe)
                {
                    var deadline = Stopwatch.StartNew();
                    while (data.Job?.BytesTransferred is not >= 128 * 1024 && !child.HasExited && deadline.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(1);
                    Check(data.Job?.BytesTransferred is >= 128 * 1024, "target dies during a real indexed IStream transfer after StartOperation");
                    data.OleDragReturned(true); child.Kill(); await child.WaitForExitAsync(); await output; await errors;
                    var drain = Stopwatch.StartNew(); await source.DisposeAsync(); await queue.WaitAsync(data.Job!);
                    while ((ShellVirtualDataObject.ActiveCount != 0 || ShellVirtualDataObject.MtaHostCount != 0) && drain.Elapsed < TimeSpan.FromSeconds(4)) await Task.Delay(20);
                    Check(data.Job!.State == TransferState.Cancelled, "source close cancels extraction after target crash without EndOperation");
                    Check(ShellVirtualDataObject.ActiveCount == 0 && ShellVirtualDataObject.MtaHostCount == 0, "lost target drains active registry and MTA host within bounded cancellation grace");
                    Console.WriteLine($"LOST_TARGET drain={drain.Elapsed.TotalMilliseconds:0}ms active={ShellVirtualDataObject.ActiveCount} hosts={ShellVirtualDataObject.MtaHostCount}");
                    File.Delete(blobFile); return 0;
                }
                if (zeroGoneProbe)
                {
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); Console.Write(await output); await errors;
                    data.OleDragReturned(true); await queue.WaitAsync(data.Job!).WaitAsync(TimeSpan.FromSeconds(3));
                    Check(child.ExitCode != 0, "native target rejects zero-byte source removed after selection");
                    Check(data.Job!.State == TransferState.Failed, "missing zero-byte source is failed, never completed as an empty copy");
                    File.Delete(blobFile); return 0;
                }
                if (cancelProbe || closeProbe)
                {
                    var deadline = Stopwatch.StartNew();
                    while (data.Job?.BytesTransferred is not >= 128 * 1024 && !child.HasExited && deadline.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(1);
                    Check(data.Job?.BytesTransferred is >= 128 * 1024, "native cancellation occurs during actual nonempty IStream transfer");
                    if (closeProbe) await source.DisposeAsync(); else data.Job!.Cancel();
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    Console.Write(await output); await errors;
                    data.OleDragReturned(true); await queue.WaitAsync(data.Job!).WaitAsync(TimeSpan.FromSeconds(3));
                    Check(data.Job!.State == TransferState.Cancelled, closeProbe ? "source-session close cancels native extraction queue job" : "post-drop queue cancellation interrupts native IStream extraction");
                    Check(child.ExitCode != 0, "native target observes cancellation instead of false success");
                    Check(!queue.HasActive(source.Id), "cancelled native export has no active common-queue work");
                    File.Delete(blobFile); Console.WriteLine($"PASS native {(closeProbe ? "source-close" : "post-drop-cancel")} probe; bytes={data.Job.BytesTransferred}, real Explorer gesture remains separate");
                    return 0;
                }
                long producerPeak = Process.GetCurrentProcess().WorkingSet64, consumerPeak = 0;
                var childExit = child.WaitForExitAsync();
                while (!childExit.IsCompleted)
                {
                    await Task.WhenAny(childExit, Task.Delay(TimeSpan.FromSeconds(10)));
                    producerPeak = Math.Max(producerPeak, Process.GetCurrentProcess().WorkingSet64);
                    try { child.Refresh(); consumerPeak = Math.Max(consumerPeak, child.WorkingSet64); } catch (InvalidOperationException) { }
                    if (!childExit.IsCompleted)
                        Console.WriteLine($"NATIVE_PROGRESS {data.Job?.BytesTransferred ?? 0}/{data.Job?.TotalBytes ?? 0} bytes, {data.Job?.BytesPerSecond / 1024 / 1024:0.0} MiB/s, producerRSS {producerPeak / 1024 / 1024} MiB, consumerRSS {consumerPeak / 1024 / 1024} MiB");
                }
                await childExit; Console.Write(await output); var error = await errors;
                if (child.ExitCode != 0) throw new Exception("Native out-of-process consumer failed: " + error);
                data.OleDragReturned(true);
                Check(data.Job is not null, "native Shell extraction registered in common transfer queue");
                await queue.WaitAsync(data.Job!).WaitAsync(TimeSpan.FromSeconds(5));
                Check(data.Job!.State == TransferState.Completed, syncProbe ? "accepted synchronous Drop completes actual common queue job" : "native EndOperation completes actual common queue job");
                if (skipProbe) Check(data.Job.SkippedItems == 1 && !File.Exists(Path.Combine(destination, "дерево", "_CON")), "Skip omits only incompatible object and preserves the rest of the native batch");
                Check(Directory.Exists(Path.Combine(destination, "дерево", "пустая")), "empty directory preserved by FILEDESCRIPTORW");
                var receivedHash = SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(destination, "дерево", "вложенная", "данные.bin")));
                Check(receivedHash.SequenceEqual(SHA256.HashData(payload)), "out-of-process native IStream bytes match SHA256");
                Check(data.Job.BytesTransferred == payload.Length + Encoding.UTF8.GetByteCount("Привет из SFTP через настоящий COM") + configuration.LargeFileBytes, "common queue reports real transferred bytes");
                Check(data.GetDataApartment == ApartmentState.MTA && data.ReadApartment == ApartmentState.MTA, "native GetData and actual network Read ran in MTA, outside UI STA");
                Console.WriteLine($"NATIVE_THREADS GetData={data.GetDataThreadId}/{data.GetDataApartment} Read={data.ReadThreadId}/{data.ReadApartment}; no UI entered");
                if (remoteLarge is not null)
                {
                    var localLarge = Path.Combine(destination, "large.bin");
                    Check(new FileInfo(localLarge).Length == configuration.LargeFileBytes, "native IStream target length matches real >2 GiB source");
                    using var localStream = File.OpenRead(localLarge);
                    var localHash = Convert.ToHexString(await SHA256.HashDataAsync(localStream));
                    var remoteHash = await RemoteSha256(source, remoteLarge);
                    Check(localHash.Equals(remoteHash, StringComparison.OrdinalIgnoreCase), "real native IStream >2 GiB source and target SHA256 match");
                    Console.WriteLine($"NATIVE_LARGE bytes={configuration.LargeFileBytes} sourceSHA256={remoteHash} targetSHA256={localHash} producerPeakObservedMiB={producerPeak / 1024 / 1024} consumerPeakObservedMiB={consumerPeak / 1024 / 1024}");
                    await File.WriteAllTextAsync(Path.Combine(local, "large-proof.json"), JsonSerializer.Serialize(new { Bytes = configuration.LargeFileBytes, SourceSha256 = remoteHash, TargetSha256 = localHash, ProducerPeakObservedBytes = producerPeak, ConsumerPeakObservedBytes = consumerPeak, NativeOutOfProcess = true, ActualGuiOrExplorerGesture = false }));
                }
                await File.WriteAllTextAsync(Path.Combine(local, "result.json"), JsonSerializer.Serialize(new { Passed = true, data.GetDataThreadId, data.ReadThreadId, GetDataApartment = data.GetDataApartment.ToString(), ReadApartment = data.ReadApartment.ToString(), data.Job.BytesTransferred, NativeOutOfProcess = true, ActualExplorerGesture = false }));
                File.Delete(blobFile); Console.WriteLine("PASS native COM probe; live Explorer mouse/OLE acceptance remains separate");
            }
            finally
            {
                if (!source.Lifetime.IsCancellationRequested) await source.Files.DeleteAsync(remote);
                else
                {
                    await using var cleanup = new ServerSession(profile, store);
                    await cleanup.ConnectAsync(new SessionCredentials(secret), prompt =>
                    { Check(prompt.Fingerprint == configuration.ExpectedHostFingerprint, "cleanup connection independently verifies host key"); return Task.FromResult(TrustDecision.Session); });
                    await cleanup.Files.DeleteAsync(remote);
                }
            }
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private static byte[] MarshalBlob(ShellVirtualDataObject data)
    {
        Marshal.ThrowExceptionForHR(ShellInterop.CoInitializeEx(IntPtr.Zero, 0));
        try
        {
            Marshal.ThrowExceptionForHR(CreateStreamOnHGlobal(IntPtr.Zero, true, out var stream));
            var pointer = Marshal.GetIUnknownForObject(data); var iid = typeof(System.Runtime.InteropServices.ComTypes.IDataObject).GUID;
            try
            {
                Marshal.ThrowExceptionForHR(CoMarshalInterface(stream, in iid, pointer, 0, IntPtr.Zero, 0));
                stream.Stat(out var stat, 1); var bytes = new byte[checked((int)stat.cbSize)]; stream.Seek(0, 0, IntPtr.Zero); stream.Read(bytes, bytes.Length, IntPtr.Zero); return bytes;
            }
            finally { Marshal.Release(pointer); Marshal.FinalReleaseComObject(stream); }
        }
        finally { ShellInterop.CoUninitialize(); }
    }

    private static int Consume(string blob, string destination, bool synchronous = false)
    {
        Marshal.ThrowExceptionForHR(ShellInterop.CoInitializeEx(IntPtr.Zero, 0));
        try
        {
            Marshal.ThrowExceptionForHR(CreateStreamOnHGlobal(IntPtr.Zero, true, out var marshalStream));
            var bytes = File.ReadAllBytes(blob); marshalStream.Write(bytes, bytes.Length, IntPtr.Zero); marshalStream.Seek(0, 0, IntPtr.Zero);
            var iid = typeof(System.Runtime.InteropServices.ComTypes.IDataObject).GUID;
            Marshal.ThrowExceptionForHR(CoUnmarshalInterface(marshalStream, in iid, out var pointer));
            var com = Marshal.GetObjectForIUnknown(pointer); Marshal.Release(pointer); Marshal.FinalReleaseComObject(marshalStream);
            var data = (System.Runtime.InteropServices.ComTypes.IDataObject)com;
            IDataObjectAsyncCapability? async = synchronous ? null : (IDataObjectAsyncCapability)com;
            if (async is not null)
            { Marshal.ThrowExceptionForHR(async.GetAsyncMode(out var enabled)); Check(enabled, "native QI negotiates IDataObjectAsyncCapability"); Marshal.ThrowExceptionForHR(async.StartOperation(IntPtr.Zero)); }
            try
            {
                var format = new FORMATETC { cfFormat = ShellInterop.Descriptor, dwAspect = DVASPECT.DVASPECT_CONTENT, tymed = TYMED.TYMED_HGLOBAL, lindex = -1 };
                data.GetData(ref format, out var medium); byte[] descriptors;
                try { descriptors = ShellInterop.GlobalBytes(medium.unionmember); }
                finally { ShellInterop.ReleaseStgMedium(ref medium); }
                var count = BitConverter.ToInt32(descriptors, 0); Check(count >= 1, "native descriptor group has entries");
                Console.WriteLine("DESCRIPTOR_COUNT " + count);
                for (var i = 0; i < count; i++)
                {
                    var offset = 4 + i * 592; var name = Encoding.Unicode.GetString(descriptors, offset + 72, 520).TrimEnd('\0');
                    var path = Path.GetFullPath(Path.Combine(destination, name));
                    Check(path.StartsWith(Path.GetFullPath(destination) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "descriptor target contained in dedicated directory");
                    var attributes = BitConverter.ToUInt32(descriptors, offset + 36);
                    if ((attributes & 0x10) != 0) { Directory.CreateDirectory(path); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    format = new FORMATETC { cfFormat = ShellInterop.Contents, dwAspect = DVASPECT.DVASPECT_CONTENT, tymed = TYMED.TYMED_ISTREAM, lindex = i };
                    data.GetData(ref format, out medium);
                    try
                    {
                        var streamObject = Marshal.GetObjectForIUnknown(medium.unionmember); var content = (IStream)streamObject;
                        var read = Marshal.AllocHGlobal(4);
                        try
                        {
                            using var output = File.Create(path); var buffer = new byte[128 * 1024];
                            while (true) { content.Read(buffer, buffer.Length, read); var n = Marshal.ReadInt32(read); if (n == 0) break; output.Write(buffer, 0, n); }
                        }
                        finally { Marshal.FreeHGlobal(read); Marshal.FinalReleaseComObject(streamObject); }
                    }
                    finally { ShellInterop.ReleaseStgMedium(ref medium); }
                }
                if (async is not null) Marshal.ThrowExceptionForHR(async.EndOperation(0, IntPtr.Zero, 1));
                Console.WriteLine(synchronous ? "PASS synchronous native consumer copied all streams without async capability or EndOperation" : "PASS native out-of-process IDataObject -> FILEDESCRIPTORW -> indexed IStream -> EndOperation");
            }
            catch { NativeTrace.Write("CONSUMER error -> EndOperation"); async?.EndOperation(ShellInterop.Abort, IntPtr.Zero, 0); NativeTrace.Write("CONSUMER EndOperation returned"); throw; }
            finally { NativeTrace.Write("CONSUMER FinalRelease data start"); Marshal.FinalReleaseComObject(com); NativeTrace.Write("CONSUMER FinalRelease data done"); }
            return 0;
        }
        finally { ShellInterop.CoUninitialize(); }
    }

    private static async Task<int> AbiOnly()
    {
        var local = Path.Combine(Environment.CurrentDirectory, ".tools", "native-abi-probe", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(local);
        var store = new AppStore(Path.Combine(local, "store"));
        await using var source = new ServerSession(new ServerProfile { Name = "ISOLATED ABI TEST — no SSH", Host = "unused.invalid", Username = "unused" }, store);
        var queue = new TransferQueue();
        try { ShellVirtualDataObject.DescriptorBytes([new("unknown.bin", new RemoteEntry { IsRegularFile = true, Length = -1 }, false)]); throw new Exception("negative length incorrectly advertised"); }
        catch (IOException) { Check(true, "negative/unknown size is rejected before unsigned FILEDESCRIPTOR conversion"); }
        var data = await ShellVirtualDataObject.CreateAsync(source,
            [new RemoteEntry { Name = "пустой.txt", FullPath = "/unused/пустой.txt", IsRegularFile = true, Length = 0, LastWriteTimeUtc = DateTime.UtcNow }], Guid.NewGuid().ToString("N"), queue);
        // ABI-only mode deliberately injects immutable inline bytes. Production remote zero-byte files
        // always go through the real export reader and are covered by --zero-gone.
        typeof(ShellVirtualDataObject).GetField("manifest", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(data, Task.FromResult(new[] { new ShellVirtualDataObject.VirtualEntry("пустой.txt", null, false, []) }));
        var blob = Path.Combine(local, "objref.bin"); await File.WriteAllBytesAsync(blob, MarshalBlob(data));
        var target = Path.Combine(local, "target"); Directory.CreateDirectory(target);
        var info = new ProcessStartInfo { FileName = Environment.ProcessPath!, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(typeof(Program).Assembly.Location);
        info.ArgumentList.Add("--consume"); info.ArgumentList.Add(blob); info.ArgumentList.Add(target);
        using var child = Process.Start(info)!; var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
        await child.WaitForExitAsync(); Console.Write(await output); if (child.ExitCode != 0) throw new Exception(await error);
        data.OleDragReturned(true); await queue.WaitAsync(data.Job!).WaitAsync(TimeSpan.FromSeconds(3));
        Check(data.Job!.State == TransferState.Completed, "ABI-only native EndOperation completes zero-byte job");
        Check(File.Exists(Path.Combine(target, "пустой.txt")), "ABI-only native stream produced zero-byte test file");
        File.Delete(blob); Console.WriteLine("PASS isolated ABI-only probe: synthetic zero-byte file, no SSH or live Explorer claimed"); return 0;
    }

    private static Task StaMetadataProbe(ShellVirtualDataObject data)
    {
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var initialized = ShellInterop.CoInitializeEx(IntPtr.Zero, 2);
            IntPtr pointer = IntPtr.Zero;
            try
            {
                Marshal.ThrowExceptionForHR(initialized);
                pointer = Marshal.GetComInterfaceForObject(data, typeof(System.Runtime.InteropServices.ComTypes.IDataObject));
                var table = Marshal.ReadIntPtr(pointer); var getData = Marshal.GetDelegateForFunctionPointer<GetDataNative>(Marshal.ReadIntPtr(table, 3 * IntPtr.Size));
                var timer = Stopwatch.StartNew(); var pending = 0; double maxCall = 0;
                while (true)
                {
                    var f = new FORMATETC { cfFormat = ShellInterop.Descriptor, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
                    var call = Stopwatch.StartNew(); var hr = getData(pointer, ref f, out var medium); maxCall = Math.Max(maxCall, call.Elapsed.TotalMilliseconds);
                    if (hr == unchecked((int)0x8000000A)) { pending++; if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Metadata never completed"); Thread.Sleep(10); continue; }
                    Marshal.ThrowExceptionForHR(hr); ShellInterop.ReleaseStgMedium(ref medium); break;
                }
                Check(data.GetDataApartment == ApartmentState.STA, "raw in-process COM metadata call executed on actual STA caller");
                Check(maxCall < 200, "STA descriptor calls never wait on recursive network enumeration");
                Console.WriteLine($"STA_METADATA pending={pending}, maxGetData={maxCall:0.00}ms"); complete.SetResult();
            }
            catch (Exception error) { complete.TrySetException(error); }
            finally { if (pointer != IntPtr.Zero) Marshal.Release(pointer); if (initialized >= 0) ShellInterop.CoUninitialize(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return complete.Task;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDataNative(IntPtr self, ref FORMATETC format, out STGMEDIUM medium);

    private static void Check(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); }
    [DllImport("ole32.dll")] static extern int CreateStreamOnHGlobal(IntPtr memory, [MarshalAs(UnmanagedType.Bool)] bool delete, out IStream stream);
    [DllImport("ole32.dll")] static extern int CoMarshalInterface(IStream stream, in Guid iid, IntPtr unknown, uint destination, IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] static extern int CoUnmarshalInterface(IStream stream, in Guid iid, out IntPtr pointer);
    private sealed class Config
    {
        public string Host { get; set; } = "127.0.0.1"; public int Port { get; set; } = 22222;
        public string Username { get; set; } = "sshx-test"; public AuthenticationKind Authentication { get; set; }
        public string PrivateKeyPath { get; set; } = ""; public string Secret { get; set; } = "";
        public string? SecretFile { get; set; } public string ExpectedHostFingerprint { get; set; } = "";
        public long LargeFileBytes { get; set; }
    }

    private static async Task<string> RemoteSha256(ServerSession source, string path)
    {
        using var shell = await source.CreateShellAsync();
        var command = Encoding.UTF8.GetBytes("sha256sum " + PosixPath.Quote(path) + "\r"); shell.Write(command, 0, command.Length); shell.Flush();
        var timer = Stopwatch.StartNew(); var lastProgress = TimeSpan.Zero; var text = new StringBuilder();
        while (timer.Elapsed < TimeSpan.FromMinutes(5))
        {
            if (shell.DataAvailable) text.Append(shell.Read());
            var found = Regex.Match(text.ToString(), "(?:^|[\\r\\n])([0-9a-fA-F]{64})[ \\t]+", RegexOptions.CultureInvariant);
            if (found.Success) return found.Groups[1].Value.ToUpperInvariant();
            if (timer.Elapsed - lastProgress > TimeSpan.FromSeconds(10)) { Console.WriteLine($"REMOTE_SHA256 waiting {timer.Elapsed.TotalSeconds:0}s (existing server utility, PTY only for test)"); lastProgress = timer.Elapsed; }
            if (text.Length > 16384) throw new IOException("Unexpected fixture SHA256 output size");
            await Task.Delay(100);
        }
        throw new TimeoutException("Fixture SHA256 measurement did not finish within 5 minutes");
    }
}
