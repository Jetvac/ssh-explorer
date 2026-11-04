using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

/// <summary>Software-injected OS keyboard input (SendInput) for this runner's owned, active CI window.
/// Text uses KEYEVENTF_UNICODE, keys use virtual-key codes. Not physical keyboard coverage.</summary>
internal sealed class CiSoftwareKeyboard
{
    readonly Window window;
    public CiSoftwareKeyboard(Window owner)
    {
        CiVisualEvidence.RequireCi(); window = owner;
        if (new WindowInteropHelper(owner).Handle == 0 || !owner.IsVisible) throw new InvalidOperationException("CI keyboard requires the runner's shown window.");
        owner.Activate(); Pump();
        if (!owner.IsActive) throw new InvalidOperationException("CI keyboard setup failed: window activation denied; software keyboard NOT RUN.");
    }
    public void Type(string text)
    {
        RequireActive();
        var inputs = new List<Input>();
        foreach (var ch in text) { inputs.Add(Unicode(ch, false)); inputs.Add(Unicode(ch, true)); }
        Send(inputs.ToArray()); Pump();
    }
    public void Press(Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        RequireActive();
        var inputs = new List<Input>();
        var mods = new List<ushort>();
        if (modifiers.HasFlag(ModifierKeys.Control)) mods.Add(0x11);
        if (modifiers.HasFlag(ModifierKeys.Alt)) mods.Add(0x12);
        if (modifiers.HasFlag(ModifierKeys.Shift)) mods.Add(0x10);
        foreach (var m in mods) inputs.Add(Virtual(m, false));
        ushort vk = (ushort)KeyInterop.VirtualKeyFromKey(key);
        inputs.Add(Virtual(vk, false)); inputs.Add(Virtual(vk, true));
        foreach (var m in Enumerable.Reverse(mods)) inputs.Add(Virtual(m, true));
        Send(inputs.ToArray()); Pump(); Pump();
    }
    void RequireActive()
    {
        CiVisualEvidence.RequireCi();
        if (!window.IsActive) { window.Activate(); Pump(); }
        if (!window.IsActive) throw new InvalidOperationException("Owned CI window lost activation; software keyboard NOT RUN.");
    }
    static Input Unicode(char ch, bool up) => new() { Type = 1, Keyboard = new KeyboardInput { Scan = ch, Flags = 0x0004 | (up ? 0x0002u : 0) } };
    static Input Virtual(ushort vk, bool up) => new() { Type = 1, Keyboard = new KeyboardInput { VirtualKey = vk, Flags = up ? 0x0002u : 0 } };
    static void Send(Input[] inputs)
    {
        uint accepted = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (accepted != inputs.Length) throw new Win32Exception(Marshal.GetLastWin32Error(), $"CI SendInput accepted {accepted}/{inputs.Length} keyboard events.");
        Console.WriteLine($"INPUT software SendInput keyboard events={accepted}");
    }
    static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame); }
    // INPUT is a union; the keyboard variant is padded to the mouse variant's size on x64.
    [StructLayout(LayoutKind.Sequential)] struct Input { public uint Type; public KeyboardInput Keyboard; public ulong Padding; }
    [StructLayout(LayoutKind.Sequential)] struct KeyboardInput { public ushort VirtualKey; public ushort Scan; public uint Flags; public uint Time; public nuint ExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, [In] Input[] inputs, int size);
}
