using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace SSHExplorer.App;

internal interface ICancellableModal
{
    void CancelModal();
}

/// <summary>No prompt queue: the attempt coordinator dispatches calls; this layer owns one visible modal's lifetime.</summary>
internal static class ModalRunner
{
    internal static bool Show(Window dialog,Window? owner,CancellationToken ct=default)
    {
        dialog.Dispatcher.VerifyAccess();
        if(ct.IsCancellationRequested||dialog.Dispatcher.HasShutdownStarted)return false;
        if(owner is not null)
        {
            if(owner.Dispatcher!=dialog.Dispatcher)throw new InvalidOperationException("Диалог и его владелец должны принадлежать одному UI-потоку.");
            if(owner.IsLoaded||new System.Windows.Interop.WindowInteropHelper(owner).Handle!=IntPtr.Zero)
            {dialog.Owner=owner;dialog.WindowStartupLocation=WindowStartupLocation.CenterOwner;}
        }
        bool closed=false,cancelled=false;
        void Cancel()
        {
            if(closed||cancelled)return;cancelled=true;
            if(dialog is ICancellableModal cancellable)cancellable.CancelModal();
            else if(dialog.IsVisible)dialog.Close();
        }
        void OwnerClosing(object? sender,CancelEventArgs e){if(!e.Cancel)Cancel();}
        void Closed(object? sender,EventArgs e){closed=true;}
        void Loaded(object sender,RoutedEventArgs e){if(ct.IsCancellationRequested)Cancel();}
        dialog.Closed+=Closed;dialog.Loaded+=Loaded;if(owner is not null)owner.Closing+=OwnerClosing;
        using var registration=ct.Register(()=>
        {
            if(dialog.Dispatcher.HasShutdownStarted)return;
            _=dialog.Dispatcher.InvokeAsync(Cancel,DispatcherPriority.Send);
        });
        try
        {
            if(ct.IsCancellationRequested)return false;
            var result=dialog.ShowDialog();return result==true&&!cancelled&&!ct.IsCancellationRequested;
        }
        finally{closed=true;dialog.Closed-=Closed;dialog.Loaded-=Loaded;if(owner is not null)owner.Closing-=OwnerClosing;}
    }
}
