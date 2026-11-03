using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using SSHExplorer.App;
using SSHExplorer.App.ViewModels;
using SSHExplorer.Core;

internal static class DragPayloadTests
{
    // This tests the in-process capture/registry boundary, never OLE, Clipboard,
    // a transfer, a remote connection, or readiness of an SFTP drop target.
    public static int Run()
    {
        App? app=null;
        try
        {
            app=TestApplication.Create();Theme.Apply("Light");int checks=0;
            void Check(bool ok,string name){if(!ok)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
            var store=new AppStore(Path.Combine(Environment.CurrentDirectory,".tools","drag-payload-tests",Guid.NewGuid().ToString("N")));typeof(App).GetProperty(nameof(App.Store))!.SetValue(null,store);
            var first=new ServerSession(new(){Name="source",Host="fixture.invalid",Username="source-user"},store);
            var second=new ServerSession(new(){Name="destination",Host="fixture.invalid",Username="different-user"},store);
            using var source=new FileBrowserView(first,null!);using var destination=new FileBrowserView(second,null!);
            try
            {
                // Only the registry's lifetime eligibility is represented. SFTP is
                // unavailable throughout; no fake connected transport is supplied.
                typeof(ServerSession).GetProperty(nameof(ServerSession.State))!.SetValue(first,SessionState.Connected);
                typeof(ServerSession).GetProperty(nameof(ServerSession.State))!.SetValue(second,SessionState.Connected);
                var model=(FileBrowserViewModel)source.DataContext;var target=(FileBrowserViewModel)destination.DataContext;
                var paths=new[]{"/source/first.txt","/source/folder"};
                model.CommitNavigation(model.BeginNavigation(),"/source",paths.Select(p=>new RemoteEntry{Name=PosixPath.Name(p),FullPath=p,IsRegularFile=true}).ToArray(),true,[],0);model.SetSelection(paths);
                Type payloadType=typeof(FileBrowserView).GetNestedType("RemoteClipboard",BindingFlags.NonPublic)!;
                object payload=Activator.CreateInstance(payloadType,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,[first,paths,false,new WeakReference<FileBrowserView>(source)],null)!;
                var data=(DataObject)typeof(FileBrowserView).GetMethod("RemoteData",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[payload])!;
                var raw=data.GetData("SSHExplorer.RemoteFiles",false) as MemoryStream??throw new InvalidDataException("Drag format did not contain an opaque token stream");var bytes=raw.ToArray();
                Check(bytes.Length==32&&Encoding.UTF8.GetString(bytes).All(Uri.IsHexDigit)&&!data.GetDataPresent(DataFormats.FileDrop),"drag registry exposes only a bounded opaque token and no nonexistent FileDrop path");
                paths[0]="/mutated-after-capture";
                target.CommitNavigation(target.BeginNavigation(),"/destination",[new RemoteEntry{Name="destination.txt",FullPath="/destination/destination.txt",IsRegularFile=true}],true,[],0);target.SetSelection(["/destination/destination.txt"]);
                model.CommitNavigation(model.BeginNavigation(),"/other",[],true,model.SelectedPaths,17);model.SetSelection([]);
                object resolved=Resolve(data)??throw new InvalidDataException("Captured source token stopped resolving before source disposal");var captured=(ServerSession)payloadType.GetProperty("Session")!.GetValue(resolved)!;var frozen=(string[])payloadType.GetProperty("Paths")!.GetValue(resolved)!;
                Check(ReferenceEquals(captured,first)&&captured.Id!=second.Id&&captured.Profile.Username=="source-user","destination/source navigation and selection changes cannot replace the captured source session or user");
                Check(frozen.SequenceEqual(new[]{"/source/first.txt","/source/folder"}),"registered drag paths remain frozen after caller array mutation and selection changes");
                Check(((WeakReference<FileBrowserView>)payloadType.GetProperty("SourceView")!.GetValue(resolved)!).TryGetTarget(out var capturedView)&&ReferenceEquals(capturedView,source),"refresh/cleanup source view remains the original weak source instead of the active destination view");
                Check(!first.SftpAvailable&&!second.SftpAvailable&&destination.DragEffect(data,DragDropKeyStates.None)==DragDropEffects.None,"payload-only fixture never claims a disconnected SFTP target is ready");
                source.Dispose();Check(Resolve(data)is null,"disposing the original source invalidates its tokens after navigation changes");
            }
            finally{first.DisposeAsync().AsTask().GetAwaiter().GetResult();second.DisposeAsync().AsTask().GetAwaiter().GetResult();}
            Check(app.DesktopStartupCount==0&&app.Windows.Count==0,"drag registry checks leave no application windows or desktop startup");
            app.Shutdown();Console.WriteLine($"RESULT: {checks} opaque drag source/navigation checks passed; no Window, Clipboard, OLE, SSH or transfer");return 0;
        }
        catch(Exception ex){Console.WriteLine(ex);app?.Shutdown();return 1;}
    }
    static object? Resolve(IDataObject data)=>typeof(FileBrowserView).GetMethod("GetRemote",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[data]);
}
