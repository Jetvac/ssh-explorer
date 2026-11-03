using System.Runtime.InteropServices;

namespace SSHExplorer.App;

internal static class WindowsClipboard
{
    [DllImport("user32.dll")]
    static extern uint GetClipboardSequenceNumber();
    public static uint CurrentSequence => GetClipboardSequenceNumber();
}
