using System.Security.Cryptography;
using System.Text;
using SSHExplorer.Core;

internal static class TransferFailureScenarios
{
    public static async Task<int> RunAsync(string configurationFile)
    {
        await using var session=await ConnectionScenarios.ConnectAsync(configurationFile,false);
        int passed=0;
        void Check(bool condition,string label){if(!condition)throw new Exception("FAIL "+label);Console.WriteLine("PASS "+label);passed++;}
        string id=Guid.NewGuid().ToString("N");
        string local=Path.Combine(Environment.CurrentDirectory,".tools","failure-smoke",id);Directory.CreateDirectory(local);
        string home=await session.Files.ResolvePathAsync("~");
        string source=PosixPath.Combine(home,"sshx-test-failure-"+id),limited=PosixPath.Combine("/mnt/diskfull-test","sshx-test-failure-"+id);
        await session.Files.CreateDirectoryAsync(source);await session.Files.CreateDirectoryAsync(limited);
        var queue=new TransferQueue{ResolveConflict=_=>Task.FromResult(ConflictDecision.Replace)};
        async Task Wait(TransferJob job){await queue.WaitAsync(job);if(job.State!=TransferState.Completed)throw new Exception(job.Error);}
        try
        {
            string original=Path.Combine(local,"original.txt");byte[] correct=Encoding.UTF8.GetBytes("original-correct-data");
            await File.WriteAllBytesAsync(original,correct);
            await Wait(queue.Upload(session,[original],limited));
            await using(var large=new FileStream(original,FileMode.Open,FileAccess.Write,FileShare.None))large.SetLength(8L*1024*1024);
            var failedUpload=queue.Upload(session,[original],limited);await queue.WaitAsync(failedUpload);
            Check(failedUpload.State==TransferState.Failed,"real ENOSPC replacement reports failed transfer");
            Check(failedUpload.Error?.Contains("Недостаточно свободного места",StringComparison.Ordinal)==true,"SFTP statvfs confirms and explains actual disk-full error");
            Check(SHA256.HashData(await session.Files.ReadBytesAsync(PosixPath.Combine(limited,"original.txt"),1024)).SequenceEqual(SHA256.HashData(correct)),"ENOSPC replacement preserves existing correct destination SHA256");
            Check(File.Exists(original)&&new FileInfo(original).Length==8L*1024*1024,"ENOSPC upload preserves local source");
            Check(!(await session.Files.ListAsync(limited)).Any(x=>x.Name.StartsWith(".sshexplorer-",StringComparison.Ordinal)&&x.Name.EndsWith(".part",StringComparison.Ordinal)),"ENOSPC cleans only its own temporary file");
            string big=Path.Combine(local,"move-big.bin");
            await using(var large=new FileStream(big,FileMode.CreateNew,FileAccess.Write,FileShare.None))large.SetLength(8L*1024*1024);
            await Wait(queue.Upload(session,[big],source));
            string remoteBig=PosixPath.Combine(source,"move-big.bin");
            var failedMove=queue.Copy(session,[remoteBig],session,limited,move:true);await queue.WaitAsync(failedMove);
            Check(failedMove.State==TransferState.Failed,"cross-filesystem move to full tmpfs reports failure");
            Check((await session.Files.StatAsync(remoteBig)).Length==8L*1024*1024,"failed cross-filesystem move preserves remote source");
            byte[] remoteHash=SHA256.HashData(await session.Files.ReadBytesAsync(remoteBig,8L*1024*1024+1));
            using(var stream=File.OpenRead(big)){byte[] localHash=await SHA256.HashDataAsync(stream);Check(localHash.SequenceEqual(remoteHash),"failed cross-filesystem move preserves source SHA256");}
            try{await session.Files.StatAsync(PosixPath.Combine(limited,"move-big.bin"));throw new Exception("Incomplete destination was committed");}
            catch(Renci.SshNet.Common.SftpPathNotFoundException){Check(true,"failed cross-filesystem move does not commit incomplete destination");}
            string small=Path.Combine(local,"move-small.bin");await File.WriteAllTextAsync(small,"crossfs-small-content");
            await Wait(queue.Upload(session,[small],source));
            string remoteSmall=PosixPath.Combine(source,"move-small.bin");
            await Wait(queue.Copy(session,[remoteSmall],session,limited,move:true));
            Check(Encoding.UTF8.GetString(await session.Files.ReadBytesAsync(PosixPath.Combine(limited,"move-small.bin"),1024))=="crossfs-small-content","successful cross-filesystem move falls back to verified streaming copy");
            try{await session.Files.StatAsync(remoteSmall);throw new Exception("Successful move retained source");}
            catch(Renci.SshNet.Common.SftpPathNotFoundException){Check(true,"successful cross-filesystem move deletes source only after copy");}
            Check(!(await session.Files.ListAsync(limited)).Any(x=>x.Name.StartsWith(".sshexplorer-",StringComparison.Ordinal)),"cross-filesystem failure and success leave no own temporaries");
            Console.WriteLine($"{passed} real disk-full/cross-filesystem checks passed");
            return passed;
        }
        finally
        {
            await session.Files.DeleteAsync(source);
            await session.Files.DeleteAsync(limited);
        }
    }
}
