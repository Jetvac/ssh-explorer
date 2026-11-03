using System.Runtime.InteropServices;

namespace SSHExplorer.App.NativeDragDrop;

public static class NativeFileDrag
{
    public static Guid? InternalTargetSession { get; set; }
    public static int Run(ShellVirtualDataObject data)
    {
        InternalTargetSession = null;
        var result = ShellInterop.DoDragDrop(data, new DropSource(data), 3, out var effect);
        data.OleDragReturned(result == 0x00040100 && effect != 0);
        InternalTargetSession = null; return result;
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class DropSource(ShellVirtualDataObject source) : IDropSource
    {
        [DllImport("user32.dll")] static extern short GetKeyState(int key);
        [DllImport("user32.dll")] static extern IntPtr SetCursor(IntPtr cursor);
        [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
        public int QueryContinueDrag(bool escape, uint keys)
        {
            if (escape || source.IsCancelled) { source.Cancel(); return 0x00040101; }
            if ((keys & 1) == 0)
            {
                if ((keys & 12) == 12 || ((keys & 4) != 0 && InternalTargetSession != source.SourceSessionId)) { source.Cancel(); return 0x00040101; }
                return 0x00040100;
            }
            return 0;
        }
        public int GiveFeedback(uint effect)
        {
            if ((GetKeyState(0x10) & 0x8000) != 0 && InternalTargetSession != source.SourceSessionId)
            { SetCursor(LoadCursor(IntPtr.Zero, (IntPtr)32648)); return 0; }
            return 0x00040102;
        }
    }
}
