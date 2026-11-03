using System.Text.Json;
using SSHExplorer.Core;

if(args.Length==2&&args[1]=="--connections-only"){await ConnectionScenarios.RunAsync(args[0]);return;}
if(args.Length==2&&args[1]=="--failures-only"){await TransferFailureScenarios.RunAsync(args[0]);return;}
if(args.Length!=1)throw new ArgumentException("Usage: SSHExplorer.Core.Smoke <isolated-fixture.json> [--connections-only|--failures-only]");
using var configuration=JsonDocument.Parse(await File.ReadAllTextAsync(args[0]));
var cfg=configuration.RootElement;
string Read(string name,string fallback="")=>cfg.TryGetProperty(name,out var p)?p.GetString()??fallback:fallback;
int port=cfg.GetProperty("Port").GetInt32();
string secret=(await File.ReadAllTextAsync(Read("SecretFile"))).TrimEnd('\r','\n');
var id=Guid.NewGuid().ToString("N");
string local=Path.Combine(Environment.CurrentDirectory,".tools","core-smoke",id);Directory.CreateDirectory(local);
var store=new AppStore(Path.Combine(local,"store"));
var profile=new ServerProfile{Name="Core symlink smoke",Host=Read("Host","127.0.0.1"),Port=port,Username=Read("Username"),TimeoutSeconds=20};
int passed=0;
void Check(bool condition,string label){if(!condition)throw new Exception("FAIL "+label);Console.WriteLine("PASS "+label);passed++;}
await using(var rejected=new ServerSession(profile,new AppStore(Path.Combine(local,"reject"))))
{
    try{await rejected.ConnectAsync(new SessionCredentials(secret),_=>Task.FromResult(TrustDecision.Reject));throw new Exception("Rejected host key connected");}
    catch(OperationCanceledException){Check(rejected.State==SessionState.Disconnected,"reject host key blocks real authentication");}
}
await using(var wrongPassword=new ServerSession(profile,new AppStore(Path.Combine(local,"bad-password"))))
{
    try{await wrongPassword.ConnectAsync(new SessionCredentials(Guid.NewGuid().ToString("N")),p=>p.Fingerprint==Read("ExpectedHostFingerprint")?Task.FromResult(TrustDecision.Session):throw new Exception("Fingerprint mismatch"));throw new Exception("Wrong password connected");}
    catch(Renci.SshNet.Common.SshAuthenticationException){Check(wrongPassword.State==SessionState.Disconnected,"wrong password preserves authentication error");}
}
string changedPath=Path.Combine(local,"changed");Directory.CreateDirectory(changedPath);byte[] oldKey=System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
await File.WriteAllTextAsync(Path.Combine(changedPath,"hostkeys.json"),JsonSerializer.Serialize(new[]{new TrustedHostKey{Host=HostTrustStore.NormalizeHost(profile.Host),Port=port,Algorithm="ssh-ed25519",PublicKey=Convert.ToBase64String(oldKey),Fingerprint=HostTrustStore.Fingerprint(oldKey),TrustedAtUtc=DateTime.UtcNow}}));
await using(var changed=new ServerSession(profile,new AppStore(changedPath)))
{
    try{await changed.ConnectAsync(new SessionCredentials(secret),_=>throw new Exception("Changed key must not prompt ordinary trust"));throw new Exception("Changed host key connected");}
    catch(HostKeyChangedException){Check(changed.State==SessionState.Disconnected,"changed host key blocks real SSH handshake");}
}
await using var session=new ServerSession(profile,store);
await session.ConnectAsync(new SessionCredentials(secret),p=>p.Fingerprint==Read("ExpectedHostFingerprint")?Task.FromResult(TrustDecision.Permanent):throw new Exception("Independent fingerprint mismatch"));
string parent=await session.Files.ResolvePathAsync(Read("RemoteParent","~"));
string root=PosixPath.Combine(parent,"sshx-test-core-"+id);
await session.Files.CreateDirectoryAsync(root);
async Task Exec(string command){var r=await session.ExecuteAsync(command);if(r.ExitStatus!=0)throw new Exception("Fixture command failed: "+r.Error);}
async Task Wait(TransferQueue q,TransferJob j){await q.WaitAsync(j);if(j.State!=TransferState.Completed)throw new Exception(j.Error);}
try
{
    string source=PosixPath.Combine(root,"source"),destination=PosixPath.Combine(root,"destination"),target=PosixPath.Combine(root,"target");
    await session.Files.CreateDirectoryAsync(source);await session.Files.CreateDirectoryAsync(destination);
    await Exec("printf 'sentinel' > "+PosixPath.Quote(target));
    string link=PosixPath.Combine(source,"relative"),broken=PosixPath.Combine(source,"broken"),cycle=PosixPath.Combine(source,"cycle");
    await Exec("ln -s '../target' "+PosixPath.Quote(link)+" && ln -s '../missing' "+PosixPath.Quote(broken)+" && ln -s '.' "+PosixPath.Quote(cycle));
    Check((await session.Files.StatAsync(link)).LinkTarget=="../target","READLINK retains relative target");
    Check((await session.Files.StatAsync(broken)).LinkTarget=="../missing","READLINK retains broken relative target");
    Check((await session.Files.FollowLinkAsync(link)).FullPath==target,"explicit follow resolves existing target");
    try{await session.Files.FollowLinkAsync(broken);throw new Exception("Broken link followed");}catch(IOException){Check(true,"explicit broken-link follow fails clearly");}
    string renamed=PosixPath.Combine(source,"renamed");
    await session.Files.RenameAsync(link,renamed);
    Check((await session.Files.StatAsync(renamed)).IsSymbolicLink,"rename moves link itself");
    await session.Files.UndoAsync();
    Check((await session.Files.StatAsync(link)).LinkTarget=="../target","undo restores link itself");
    var queue=new TransferQueue{ResolveConflict=_=>Task.FromResult(ConflictDecision.Replace)};
    await Wait(queue,queue.Copy(session,[link,broken,cycle],session,destination));
    Check((await session.Files.StatAsync(PosixPath.Combine(destination,"relative"))).LinkTarget=="../target","remote copy keeps relative symlink exact");
    Check((await session.Files.StatAsync(PosixPath.Combine(destination,"broken"))).LinkTarget=="../missing","remote copy keeps broken link exact");
    Check((await session.Files.StatAsync(PosixPath.Combine(destination,"cycle"))).LinkTarget==".","remote copy keeps cycle without traversing");
    string payload=Path.Combine(local,"relative");await File.WriteAllTextAsync(payload,"replacement");
    await Wait(queue,queue.Upload(session,[payload],destination));
    Check(!(await session.Files.StatAsync(PosixPath.Combine(destination,"relative"))).IsSymbolicLink,"replacement replaces symlink name");
    Check(System.Text.Encoding.UTF8.GetString(await session.Files.ReadBytesAsync(target,100))=="sentinel","replacement never overwrites symlink target");
    await session.Files.SetPermissionsAsync(target,1640);
    Check((await session.Files.StatAsync(target)).PermissionsText=="1640","properties preserve sticky mode bit");
    await session.Files.DeleteAsync(source);
    Check(System.Text.Encoding.UTF8.GetString(await session.Files.ReadBytesAsync(target,100))=="sentinel","recursive delete leaves symlink target intact");
    Check(!(await session.Files.ListAsync(root)).Any(x=>x.Name.StartsWith(".sshexplorer-",StringComparison.Ordinal)),"no own transfer temporaries remain");
    Console.WriteLine($"{passed} real core symlink checks passed");
}
finally{await session.Files.DeleteAsync(root);}
await ConnectionScenarios.RunAsync(args[0]);
await TransferFailureScenarios.RunAsync(args[0]);
