using System.Reflection;
using System.Runtime.ExceptionServices;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace SSHExplorer.Core;

/// <summary>
/// Narrow adapter for exact SFTP v3 paths. SSH.NET's high-level Get/Rename/SymbolicLink
/// canonicalize their arguments and can dereference a final symlink. The pinned 2026.0.0
/// transport already implements the wire requests we need, but does not expose READLINK.
/// Signature checks fail closed; trimming is deliberately disabled in the application.
/// </summary>
internal static class SftpProtocolAdapter
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly FieldInfo SessionField = typeof(SftpClient).GetField("_sftpSession",Members) ?? throw Unsupported();
    private static readonly Type SessionType = typeof(SftpClient).Assembly.GetType("Renci.SshNet.Sftp.SftpSession") ?? throw Unsupported();
    private static readonly MethodInfo LStat = FindMethod("RequestLStat",typeof(string));
    private static readonly MethodInfo ReadLinkMethod = FindMethod("RequestReadLink",typeof(string),typeof(bool));
    private static readonly MethodInfo SymLinkMethod = FindMethod("RequestSymLink",typeof(string),typeof(string));
    private static readonly MethodInfo PosixRenameMethod = FindMethod("RequestPosixRename",typeof(string),typeof(string));
    private static readonly ConstructorInfo FileConstructor = typeof(SftpFile).GetConstructors(Members).SingleOrDefault(x=>x.GetParameters().Length==3&&x.GetParameters()[1].ParameterType==typeof(string)&&x.GetParameters()[2].ParameterType==typeof(SftpFileAttributes)) ?? throw Unsupported();
    private static NotSupportedException Unsupported()=>new("Версия SSH.NET не соответствует проверенному SFTP-адаптеру 2026.0.0. Файловая операция остановлена; требуется совместимое обновление приложения.");
    private static MethodInfo FindMethod(string name,params Type[] types)=>SessionType.GetMethod(name,Members,null,types,null)??throw Unsupported();
    private static object Session(SftpClient client)=>SessionField.GetValue(client)??throw new IOException("SFTP-сессия закрыта.");
    private static object? Invoke(MethodInfo method,object session,params object?[] args)
    {
        try{return method.Invoke(session,args);}
        catch(TargetInvocationException ex)when(ex.InnerException is not null){ExceptionDispatchInfo.Capture(ex.InnerException).Throw();throw;}
    }
    internal static void ValidateBindings()
    {
        if(typeof(SftpClient).Assembly.GetName().Version?.Major!=2026||LStat.ReturnType!=typeof(SftpFileAttributes)||ReadLinkMethod.ReturnType!=typeof(KeyValuePair<string,SftpFileAttributes>[]))throw Unsupported();
    }
    internal static ISftpFile Get(SftpClient client,string path)
    {
        ValidateBindings();var session=Session(client);
        var attrs=Invoke(LStat,session,path)as SftpFileAttributes??throw new IOException("SFTP не вернул метаданные объекта.");
        return (ISftpFile)FileConstructor.Invoke([session,path,attrs]);
    }
    internal static string ReadLink(SftpClient client,string path)
    {
        ValidateBindings();
        var result=Invoke(ReadLinkMethod,Session(client),path,false)as KeyValuePair<string,SftpFileAttributes>[];
        if(result is not{Length:>0})throw new IOException("SFTP READLINK не вернул цель ссылки.");
        return result[0].Key;
    }
    internal static void SymbolicLink(SftpClient client,string target,string linkPath)
    { ValidateBindings();Invoke(SymLinkMethod,Session(client),target,linkPath); }
    internal static void PosixRename(SftpClient client,string source,string destination)
    { ValidateBindings();Invoke(PosixRenameMethod,Session(client),source,destination); }
}
