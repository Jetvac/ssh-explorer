using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

/// <summary>Software-injected OS mouse input, solely on this runner's owned CI window.
/// No entry point can run on the host. This does not claim physical input coverage.
/// Win32 ABI: https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-sendinput</summary>
internal sealed class CiSoftwarePointer:IDisposable
{
    readonly Window window;readonly nint hwnd;bool held;
    public CiSoftwarePointer(Window owner)
    {
        CiVisualEvidence.RequireCi();window=owner;hwnd=new WindowInteropHelper(owner).Handle;
        if(hwnd==0||!owner.IsVisible)throw new InvalidOperationException("CI pointer setup requires the runner's actual shown window.");
        owner.Left=SystemParameters.WorkArea.Left+16;owner.Top=SystemParameters.WorkArea.Top+16;owner.Width=Math.Min(owner.ActualWidth,SystemParameters.WorkArea.Width-32);owner.Height=Math.Min(owner.ActualHeight,SystemParameters.WorkArea.Height-32);owner.Activate();Pump();owner.UpdateLayout();
        if(!owner.IsActive)throw new InvalidOperationException("CI pointer setup failed: owned window activation was denied; software input NOT RUN.");
        if(Keyboard.Modifiers!=ModifierKeys.None)throw new InvalidOperationException("CI pointer setup failed: keyboard modifiers were already held; software input NOT RUN.");
        if((GetAsyncKeyState(1)&0x8000)!=0)throw new InvalidOperationException("CI pointer setup failed: a mouse button was already held.");
        Console.WriteLine($"INPUT LEVEL software SendInput mouse; physical mouse NOT RUN; owned HWND shown/active; INPUT size={Marshal.SizeOf<Input>()}");
    }
    public void MoveTo(FrameworkElement owner,Point point)
    {
        CiVisualEvidence.RequireCi();if(!ReferenceEquals(Window.GetWindow(owner),window)||!owner.IsVisible)throw new InvalidOperationException("Software pointer target left its owned window.");
        var screen=owner.PointToScreen(point);int x=checked((int)Math.Round(screen.X)),y=checked((int)Math.Round(screen.Y));
        var target=WindowFromPoint(new NativePoint(x,y));if(target==0||GetAncestor(target,2)!=hwnd)throw new InvalidOperationException($"CI pointer target is not covered by the owned window at ({x},{y}); software input NOT RUN.");
        int left=GetSystemMetrics(76),top=GetSystemMetrics(77),width=GetSystemMetrics(78),height=GetSystemMetrics(79);
        if(width<2||height<2||x<left||x>=left+width||y<top||y>=top+height)throw new InvalidOperationException("CI pointer target is outside the virtual desktop.");
        int normalizedX=(int)Math.Round((x-left)*65535d/(width-1)),normalizedY=(int)Math.Round((y-top)*65535d/(height-1));
        Send(0x0001|0x8000|0x4000,normalizedX,normalizedY);
        try{Wait(()=>GetCursorPos(out var cursor)&&Math.Abs(cursor.X-x)<=2&&Math.Abs(cursor.Y-y)<=2&&Near(owner.PointToScreen(Mouse.GetPosition(owner)),x,y),"CI SendInput movement did not reach matching OS and WPF positions.");}
        catch{TracePosition(owner,x,y);throw;}
        TracePosition(owner,x,y);Console.WriteLine($"INPUT software move x={x} y={y}; buttonHeld={held}");
    }
    public void Down()
    {
        CiVisualEvidence.RequireCi();Send(0x0002);held=true;
        Wait(()=>Mouse.LeftButton==MouseButtonState.Pressed&&(GetAsyncKeyState(1)&0x8000)!=0,"CI SendInput left-down did not reach WPF/OS state.");
    }
    public void Up()
    {
        CiVisualEvidence.RequireCi();Send(0x0004);held=false;
        Wait(()=>Mouse.LeftButton==MouseButtonState.Released&&(GetAsyncKeyState(1)&0x8000)==0,"CI SendInput left-up did not reach WPF/OS state.");
    }
    static void Send(uint flags,int x=0,int y=0)
    {
        CiVisualEvidence.RequireCi();var inputs=new[]{new Input{Type=0,Mouse=new MouseInput{Flags=flags,X=x,Y=y}}};uint accepted=SendInput(1,inputs,Marshal.SizeOf<Input>());
        if(accepted!=1)throw new Win32Exception(Marshal.GetLastWin32Error(),$"CI SendInput accepted {accepted}/1 events; injection may be blocked.");
        CiVisualEvidence.SoftwarePointerUsed=true;Console.WriteLine($"INPUT software SendInput flags=0x{flags:X}; accepted={accepted}");
    }
    public void Dispose(){CiVisualEvidence.RequireCi();if(held)Up();}
    static bool Near(Point position,int x,int y)=>Math.Abs(position.X-x)<=2&&Math.Abs(position.Y-y)<=2;
    static void TracePosition(FrameworkElement owner,int requestedX,int requestedY)
    {
        CiVisualEvidence.RequireCi();bool osValid=GetCursorPos(out var cursor);var local=Mouse.GetPosition(owner);var screen=owner.PointToScreen(local);
        Console.WriteLine("DIAGNOSTIC software pointer "+System.Text.Json.JsonSerializer.Serialize(new{requestedX,requestedY,osValid,osX=cursor.X,osY=cursor.Y,wpfScreenX=screen.X,wpfScreenY=screen.Y,wpfLocalX=local.X,wpfLocalY=local.Y,directlyOverType=Mouse.DirectlyOver?.GetType().Name,actualHitType=(owner.InputHitTest(local)as DependencyObject)?.GetType().Name}));
    }
    static void Wait(Func<bool> condition,string failure){var watch=System.Diagnostics.Stopwatch.StartNew();do{Pump();if(condition())return;}while(watch.Elapsed<TimeSpan.FromSeconds(3));throw new InvalidOperationException(failure);}
    static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,()=>frame.Continue=false);Dispatcher.PushFrame(frame);}
    [StructLayout(LayoutKind.Sequential)]struct Input{public uint Type;public MouseInput Mouse;}
    [StructLayout(LayoutKind.Sequential)]struct MouseInput{public int X,Y;public uint Data,Flags,Time;public nuint ExtraInfo;}
    [StructLayout(LayoutKind.Sequential)]readonly struct NativePoint(int x,int y){public readonly int X=x,Y=y;}
    [DllImport("user32.dll",SetLastError=true)]static extern uint SendInput(uint count,[In]Input[] inputs,int size);
    [DllImport("user32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")]static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")]static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")]static extern nint GetAncestor(nint hwnd,uint flags);
}
