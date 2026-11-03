using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Renci.SshNet;
using SSHExplorer.Core;

internal static class ConnectionScenarios
{
    public static async Task<int> RunAsync(string configurationFile)
    {
        int passed=0;
        void Check(bool condition,string label){if(!condition)throw new Exception("FAIL "+label);Console.WriteLine("PASS "+label);passed++;}
        string fixtures=Path.GetDirectoryName(Path.GetFullPath(configurationFile))!;
        string keyConfiguration=Path.Combine(fixtures,"integration-key.json");
        if(File.Exists(keyConfiguration))
        {
            await using var keySession=await ConnectAsync(keyConfiguration,true);
            Check(keySession.SftpAvailable&&keySession.ShellAvailable,"encrypted Ed25519 key authenticates SSH and SFTP");
            using var shell=await keySession.CreateShellAsync(100,30);
            await InitializeShellAsync(shell);
            string marker="__SSHX_"+Guid.NewGuid().ToString("N")+"__";
            Write(shell,"printf 'Привет, мир 👋'; printf '\\n"+marker+"\\n'\r");
            string output=await ReadUntilAsync(shell,marker,TimeSpan.FromSeconds(5));
            Check(output.Contains("Привет, мир 👋",StringComparison.Ordinal),"PTY round-trips raw UTF-8 Cyrillic and emoji");
            marker="__SSHX_"+Guid.NewGuid().ToString("N")+"__";
            Write(shell,"printf '\\320'; sleep 0.1; printf '\\237'; printf '\\n"+marker+"\\n'\r");
            output=await ReadUntilAsync(shell,marker,TimeSpan.FromSeconds(5));
            Check(output.Contains('П'),"PTY preserves UTF-8 split across remote writes");
            shell.ChangeWindowSize(121,37,0,0);
            marker="__SSHX_"+Guid.NewGuid().ToString("N")+"__";
            Write(shell,"stty size; printf '\\n"+marker+"\\n'\r");
            output=await ReadUntilAsync(shell,marker,TimeSpan.FromSeconds(5));
            Check(output.Contains("37 121",StringComparison.Ordinal),"PTY resize reaches stty rows and columns");
            marker="__SSHX_"+Guid.NewGuid().ToString("N")+"__";
            string state="state-"+Guid.NewGuid().ToString("N");
            Write(shell,"SSHX_PERSIST="+PosixPath.Quote(state)+"; export SSHX_PERSIST; printf '\\n"+marker+"\\n'\r");
            await ReadUntilAsync(shell,marker,TimeSpan.FromSeconds(5));
            var exec=await keySession.ExecuteAsync("printf exec-alive");
            Check(exec.ExitStatus==0&&exec.Output=="exec-alive","independent exec remains usable alongside PTY");
            marker="__SSHX_"+Guid.NewGuid().ToString("N")+"__";
            Write(shell,"printf '%s' \"$SSHX_PERSIST\"; printf '\\n"+marker+"\\n'\r");
            output=await ReadUntilAsync(shell,marker,TimeSpan.FromSeconds(5));
            Check(output.Contains(state,StringComparison.Ordinal),"PTY shell state survives independent exec request");
            Write(shell,"sleep 30\r");await Task.Delay(250);Write(shell,"\u0003");
            marker="__SSHX_"+Guid.NewGuid().ToString("N")+"__";
            Write(shell,"printf '\\n"+marker+"\\n'\r");
            output=await ReadUntilAsync(shell,marker,TimeSpan.FromSeconds(5));
            Check(output.Contains(marker,StringComparison.Ordinal),"Ctrl+C interrupts real foreground sleep");
            var closed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            shell.Closed+=(_,_)=>closed.TrySetResult();
            Write(shell,"\u0004");await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(true,"Ctrl+D ends real interactive shell");
            Check((await keySession.ExecuteAsync("printf alive-after-shell")).Output=="alive-after-shell","closing PTY preserves independent session exec");
        }
        string sftpOnly=Path.Combine(fixtures,"sftp-only.json");
        if(File.Exists(sftpOnly))
        {
            await using var session=await ConnectAsync(sftpOnly,false);
            Check(session.SftpAvailable&&!session.ShellAvailable,"SFTP-only account retains file subsystem and explains unavailable shell");
            string home=await session.Files.ResolvePathAsync("~");
            string file=PosixPath.Combine(home,"sshx-test-"+Guid.NewGuid().ToString("N"));
            await session.Files.CreateFileAsync(file);
            Check((await session.Files.StatAsync(file)).IsRegularFile,"SFTP-only account performs real create and stat");
            await session.Files.DeleteAsync(file);
            try{using var shell=await session.CreateShellAsync();throw new Exception("SFTP-only account unexpectedly opened PTY");}
            catch(Renci.SshNet.Common.SshException){Check(session.SftpAvailable,"denied PTY does not disable established SFTP");}
        }
        string shellOnly=Path.Combine(fixtures,"shell-only.json");
        if(File.Exists(shellOnly))
        {
            await using var session=await ConnectAsync(shellOnly,false);
            Check(session.ShellAvailable&&!session.SftpAvailable&&!string.IsNullOrEmpty(session.SftpError),"shell-only account retains exec and explains unavailable SFTP");
            using var shell=await session.CreateShellAsync();await InitializeShellAsync(shell);
            string marker="__SSHX_"+Guid.NewGuid().ToString("N")+"__";
            Write(shell,"printf '\\n"+marker+"\\n'\r");
            Check((await ReadUntilAsync(shell,marker,TimeSpan.FromSeconds(5))).Contains(marker,StringComparison.Ordinal),"shell-only account performs real PTY interaction");
            try{await session.Files.ListAsync("/");throw new Exception("Unavailable SFTP unexpectedly worked");}
            catch(IOException){Check((await session.ExecuteAsync("printf shell-still-alive")).Output=="shell-still-alive","failed SFTP operation preserves shell-only exec");}
        }
        Console.WriteLine($"{passed} real key/PTY/restricted-mode checks passed");
        return passed;
    }
    internal static async Task<ServerSession> ConnectAsync(string configurationFile,bool key)
    {
        using var doc=JsonDocument.Parse(await File.ReadAllTextAsync(configurationFile));var cfg=doc.RootElement;
        string Read(string name)=>cfg.GetProperty(name).GetString()??"";
        var profile=new ServerProfile{Name="Isolated connection smoke",Host=Read("Host"),Port=cfg.GetProperty("Port").GetInt32(),Username=Read("Username"),Authentication=key?AuthenticationKind.PrivateKey:AuthenticationKind.Password,PrivateKeyPath=key?Read("PrivateKeyPath"):"",TimeoutSeconds=10};
        var store=new AppStore(Path.Combine(Environment.CurrentDirectory,".tools","connection-smoke",Guid.NewGuid().ToString("N")));
        var session=new ServerSession(profile,store);
        try
        {
            string secret=(await File.ReadAllTextAsync(Read("SecretFile"))).TrimEnd('\r','\n');
            await session.ConnectAsync(new SessionCredentials(secret),p=>p.Fingerprint==Read("ExpectedHostFingerprint")?Task.FromResult(TrustDecision.Permanent):throw new Exception("Independent fixture fingerprint mismatch"));
            return session;
        }
        catch{await session.DisposeAsync();throw;}
    }
    private static void Write(ShellStream shell,string text)
    {byte[] bytes=Encoding.UTF8.GetBytes(text);shell.Write(bytes,0,bytes.Length);shell.Flush();}
    private static async Task InitializeShellAsync(ShellStream shell)
    {
        Write(shell,"stty -echo\r");await Task.Delay(200);
        var buffer=new byte[8192];while(shell.DataAvailable){int discarded=shell.Read(buffer,0,buffer.Length);if(discarded==0)break;}
        string marker="__SSHX_READY_"+Guid.NewGuid().ToString("N")+"__";
        Write(shell,"printf '\\n"+marker+"\\n'\r");await ReadUntilAsync(shell,marker,TimeSpan.FromSeconds(5));
    }
    private static async Task<string> ReadUntilAsync(ShellStream shell,string marker,TimeSpan timeout)
    {
        var watch=Stopwatch.StartNew();using var bytes=new MemoryStream();byte[] buffer=new byte[8192];
        while(watch.Elapsed<timeout)
        {
            while(shell.DataAvailable)
            {
                int count=shell.Read(buffer,0,buffer.Length);if(count==0)break;bytes.Write(buffer,0,count);
                if(bytes.Length>1024*1024)throw new IOException("PTY test output exceeded 1 MiB bound");
            }
            string text=Encoding.UTF8.GetString(bytes.GetBuffer(),0,(int)bytes.Length);
            if(text.Contains(marker,StringComparison.Ordinal))return text;
            await Task.Delay(20);
        }
        throw new TimeoutException("PTY did not produce its expected marker");
    }
}
