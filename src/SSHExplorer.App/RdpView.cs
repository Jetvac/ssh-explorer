using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using System.Windows.Threading;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>
/// A remote desktop tab. The control is a native window, so it does not live inside the tab: it sits on a layer over
/// the whole window and is moved onto this view's slot while the tab is shown, and hidden (not destroyed) otherwise —
/// switching tabs never drops the session. Over SSH (profile «Подключаться через»), a loopback tunnel to port 3389 is
/// opened first and closed with the tab.
/// </summary>
public sealed class RdpView : DockPanel, IAsyncDisposable
{
    readonly Canvas layer;
    readonly Border slot = new() { ClipToBounds = true };
    readonly WindowsFormsHost host = new() { Visibility = Visibility.Collapsed };
    readonly TextBlock status = Ui.Text("", 12), message = Ui.Text("", 15);
    readonly StackPanel notice = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 520, Margin = new Thickness(24) };
    readonly Button reconnectButton, fullScreenButton, keysButton, disconnectButton, snapshotButton, filesButton;
    readonly DispatcherTimer resize = new() { Interval = TimeSpan.FromMilliseconds(700) };
    readonly HashSet<string> sessionTrusted = [];
    RdpClient? client;
    ServerSession? tunnel;
    TaskCompletionSource<bool>? login;
    string? password;
    bool disposed, keyVerified, userDisconnect;
    int desktopWidth, desktopHeight;
    Rect placed;

    public ServerProfile Profile { get; }
    public Guid Id { get; } = Guid.NewGuid();
    public SessionState State { get; private set; } = SessionState.Closed;
    public event EventHandler? StateChanged;
    /// <summary>Asks the main window to connect again (it reads the saved or entered password).</summary>
    internal Func<RdpView, Task>? ReconnectRequested { get; set; }
    /// <summary>Asks the main window to open this server's files over SSH (Windows OpenSSH Server).</summary>
    internal Func<RdpView, Task>? FilesRequested { get; set; }
    /// <summary>Test hook: the PNG file a snapshot is saved to (null — cancel).</summary>
    internal static Func<string?>? SnapshotPathOverride { get; set; }
    internal string StatusText => status.Text;
    internal string MessageText => message.Text;
    internal bool ControlCreated => client?.Ready == true;
    internal string? LastError { get; private set; }
    /// <summary>The address and port the control dials: the server, or the local end of the SSH tunnel.</summary>
    internal (string Host, int Port)? Dialed { get; private set; }

    public RdpView(ServerProfile profile, Canvas layer)
    {
        Profile = profile; this.layer = layer;
        SetResourceReference(BackgroundProperty, "Paper");
        reconnectButton = Ui.AsyncCommandButton(IconCatalog.Reconnect, "Переподключить", () => ReconnectRequested?.Invoke(this) ?? Task.CompletedTask, "Подключиться заново");
        fullScreenButton = Ui.CommandButton(IconCatalog.ViewHuge, "Во весь экран", FullScreen, "Рабочий стол на весь экран; вернуться — кнопкой на панели вверху экрана");
        // Keys go to the remote computer, so they share one menu instead of looking like local commands.
        keysButton = Ui.FlyoutButton("\uE765", "Клавиши", KeysMenu, "Отправить сочетание клавиш удалённому компьютеру");
        disconnectButton = Ui.AsyncCommandButton(Ui.CloseGlyph, "Отключить", DisconnectAsync, "Завершить подключение, вкладка останется");
        snapshotButton = Ui.CommandButton("\uE722", "Снимок", Snapshot, "Сохранить снимок рабочего стола в PNG");
        filesButton = Ui.AsyncCommandButton(Ui.FileGlyph, "Файлы по SSH", () => FilesRequested?.Invoke(this) ?? Task.CompletedTask, "Открыть файлы этого сервера по SSH (нужен OpenSSH Server на Windows)");
        snapshotButton.Name = "RdpSnapshot"; filesButton.Name = "RdpFiles";
        var mstsc = Ui.CommandButton(IconCatalog.RemoteDesktop, "Открыть в mstsc", OpenInMstsc, "Открыть этот сервер в «Подключении к удалённому рабочему столу» Windows (пароль не передаётся)");
        reconnectButton.Name = "RdpReconnect"; fullScreenButton.Name = "RdpFullScreen"; keysButton.Name = "RdpKeys"; disconnectButton.Name = "RdpDisconnect"; mstsc.Name = "RdpMstsc";
        status.Name = "RdpStatus"; status.VerticalAlignment = VerticalAlignment.Center; status.Margin = new Thickness(12, 0, 0, 0); status.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); status.TextTrimming = TextTrimming.CharacterEllipsis; status.MaxWidth = 360;
        var commands = new ResponsiveCommandBar { Name = "RdpCommands" };
        commands.Add("reconnect", reconnectButton, () => Ui.AsyncMenu("Переподключить", () => ReconnectRequested?.Invoke(this) ?? Task.CompletedTask, glyph: IconCatalog.Reconnect), 90);
        commands.Add("disconnect", disconnectButton, () => Ui.AsyncMenu("Отключить", DisconnectAsync, glyph: Ui.CloseGlyph), 80);
        commands.Add("fullscreen", fullScreenButton, () => Ui.Menu("Во весь экран", FullScreen, glyph: IconCatalog.ViewHuge), 70);
        commands.Add("keys", keysButton, () => { var m = new MenuItem { Header = "Клавиши" }; m.Items.Add(Ui.Menu("Ctrl+Alt+Del", CtrlAltDel)); m.Items.Add(Ui.Menu("Пуск (клавиша Windows)", StartMenu)); return m; }, 60);
        commands.Add("snapshot", snapshotButton, () => Ui.Menu("Снимок", Snapshot, glyph: "\uE722"), 40);
        commands.Add("files", filesButton, () => Ui.AsyncMenu("Файлы по SSH", () => FilesRequested?.Invoke(this) ?? Task.CompletedTask, glyph: Ui.FileGlyph), 30);
        commands.Add("mstsc", mstsc, () => Ui.Menu("Открыть в mstsc", OpenInMstsc, glyph: IconCatalog.RemoteDesktop), 20);
        var bar = new DockPanel { LastChildFill = true }; DockPanel.SetDock(status, Dock.Right); bar.Children.Add(status); bar.Children.Add(commands);
        var barHost = new Border { Child = bar, Padding = new Thickness(10, 4, 10, 4) }; barHost.SetResourceReference(StyleProperty, "CommandBarStyle"); SetDock(barHost, Dock.Top); Children.Add(barHost);
        message.Name = "RdpMessage"; message.TextWrapping = TextWrapping.Wrap; message.TextAlignment = TextAlignment.Center; notice.Children.Add(message);
        var again = Ui.AsyncCommandButton(IconCatalog.Reconnect, "Подключиться снова", () => ReconnectRequested?.Invoke(this) ?? Task.CompletedTask); again.Name = "RdpConnectAgain"; again.HorizontalAlignment = HorizontalAlignment.Center; again.Margin = new Thickness(0, 14, 0, 0); notice.Children.Add(again);
        var surface = new Grid(); surface.Children.Add(notice); surface.Children.Add(slot); Children.Add(surface);
        slot.SizeChanged += (_, _) => { Place(); if (State == SessionState.Connected) { resize.Stop(); resize.Start(); } };
        slot.LayoutUpdated += (_, _) => Place();
        slot.IsVisibleChanged += (_, _) => Place();
        resize.Tick += (_, _) => { resize.Stop(); FitDesktop(); };
        layer.Children.Add(host);
        Update("Не подключён", null);
    }

    void Update(string text, string? problem)
    {
        status.Text = (Dialed is not null && tunnel is not null ?$"через SSH {tunnel.Route ?? Profile.ProxyJump} → " : "") + text;
        message.Text = problem ?? text;
        bool live = State is SessionState.Connecting or SessionState.Connected;
        notice.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        fullScreenButton.IsEnabled = keysButton.IsEnabled = snapshotButton.IsEnabled = State == SessionState.Connected;
        disconnectButton.IsEnabled = live;
        Place();
    }

    void SetState(SessionState value, string text, string? problem = null)
    {
        State = value; Update(text, problem);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Keeps the native control exactly over the slot; hidden when the tab is not shown or not connected.</summary>
    void Place()
    {
        if (disposed) return;
        bool show = slot.IsVisible && (State is SessionState.Connecting or SessionState.Connected) && slot.ActualWidth > 1 && slot.ActualHeight > 1 && PresentationSource.FromVisual(slot) is not null;
        if (!show) { if (host.Visibility != Visibility.Collapsed) host.Visibility = Visibility.Collapsed; return; }
        Rect rect;
        try { var origin = slot.TransformToVisual(layer).Transform(new Point(0, 0)); rect = new Rect(origin, new Size(slot.ActualWidth, slot.ActualHeight)); }
        catch (InvalidOperationException) { host.Visibility = Visibility.Collapsed; return; }
        if (rect != placed) { placed = rect; Canvas.SetLeft(host, rect.X); Canvas.SetTop(host, rect.Y); host.Width = rect.Width; host.Height = rect.Height; }
        if (host.Visibility != Visibility.Visible) host.Visibility = Visibility.Visible;
    }

    (int Width, int Height) DesktopSize()
    {
        if (!Profile.Rdp.FitsTab) return (Profile.Rdp.Width, Profile.Rdp.Height);
        var scale = PresentationSource.FromVisual(slot)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        int w = (int)Math.Round(Math.Max(slot.ActualWidth, 640) * scale.M11), h = (int)Math.Round(Math.Max(slot.ActualHeight, 480) * scale.M22);
        return (Math.Clamp(w & ~1, 200, 8192), Math.Clamp(h, 200, 8192));
    }

    /// <summary>Connects with the given password; completes when sign-in succeeds (true) or fails (false).</summary>
    public async Task<bool> ConnectAsync(string secret, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (State is SessionState.Connecting or SessionState.Connected) return State == SessionState.Connected;
        password = secret; keyVerified = false; userDisconnect = false; LastError = null;
        SetState(SessionState.Connecting, "Подключение…");
        try
        {
            if (RdpAxHost.FindClass() is not { } clsid) { Fail("На этом компьютере нет элемента «Подключение к удалённому рабочему столу» (mstscax.dll)."); return false; }
            if (ProxyJumpSpec.Parse(Profile.ProxyJump).Count > 0)
            {
                if (tunnel is null || !tunnel.ForwardAlive)
                {
                    if (tunnel is not null) await tunnel.DisposeAsync();
                    Update("Открывается туннель SSH…", null);
                    tunnel = new ServerSession(Profile, App.Store);
                    var window = Window.GetWindow(this);
                    int port = await tunnel.OpenForwardOnlyAsync((key, token) => Dispatcher.InvokeAsync(() => Dialogs.Trust(key, window, token)).Task, ct);
                    Dialed = ("127.0.0.1", port);
                }
            }
            else Dialed = (Profile.Host, Profile.Port);
            // The server key is read and checked here, before the control (and the password) connect.
            var probe = await RdpProbe.ReadKeyAsync(Dialed.Value.Host, Dialed.Value.Port, TimeSpan.FromSeconds(15), ct);
            if (probe.PublicKey is { Length: > 0 } key)
            {
                if (!VerifyKey(key)) { Fail(LastError ?? "Сертификат сервера не принят. Пароль не отправлен."); return false; }
            }
            else _ = LocalDiagnostics.RecordAsync("rdp.probe.unavailable", new InvalidOperationException(probe.Problem ?? "no key"));
            client ??= CreateClient(clsid);
            for (int i = 0; i < 100 && !client.EnsureCreated(); i++) { await Task.Delay(50, ct); Place(); }
            if (!client.Ready) { Fail("Не удалось создать элемент удалённого рабочего стола."); return false; }
            login = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Configure(client.Ocx!);
            Com.Call(client.Ocx!, "Connect");
            using var registration = ct.Register(() => login.TrySetCanceled(ct));
            return await login.Task;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail(ErrorMessages.Describe(ex)); _ = LocalDiagnostics.RecordAsync("rdp.connect.failed", ex); return false;
        }
    }

    RdpClient CreateClient(string clsid)
    {
        var created = new RdpClient(clsid);
        created.Event += OnEvent;
        host.Child = created.Control;
        host.Visibility = Visibility.Visible; Place();
        return created;
    }

    void Configure(object ocx)
    {
        var o = Profile.Rdp; var (address, port) = Dialed!.Value;
        Com.Set(ocx, "Server", address);
        Com.SetFirst(ocx, "RDPPort", port, "AdvancedSettings2", "AdvancedSettings");
        Com.Set(ocx, "UserName", Profile.Username);
        Com.Set(ocx, "Domain", o.Domain);
        (desktopWidth, desktopHeight) = DesktopSize();
        Com.Set(ocx, "DesktopWidth", desktopWidth); Com.Set(ocx, "DesktopHeight", desktopHeight);
        Com.Set(ocx, "ColorDepth", o.ColorDepth);
        Com.SetFirst(ocx, "ConnectingText", "Подключение к " + Profile.Name + "…", "");
        Com.SetFirst(ocx, "SmartSizing", !o.FitsTab, Com.Advanced);
        Com.SetFirst(ocx, "EnableCredSspSupport", o.Nla, Com.Advanced);
        // A key checked by RdpProbe needs no second warning; otherwise Windows checks the certificate itself.
        Com.SetFirst(ocx, "AuthenticationLevel", keyVerified ? 0 : 2, Com.Advanced);
        Com.SetFirst(ocx, "RedirectClipboard", o.Clipboard, Com.Advanced);
        Com.SetFirst(ocx, "RedirectDrives", o.Drives, Com.Advanced);
        Com.SetFirst(ocx, "RedirectPrinters", o.Printers, Com.Advanced);
        Com.SetFirst(ocx, "RedirectSmartCards", o.SmartCards, Com.Advanced);
        Com.SetFirst(ocx, "RedirectPorts", false, Com.Advanced);
        Com.SetFirst(ocx, "AudioRedirectionMode", (int)o.Audio, "SecuredSettings3", "SecuredSettings2");
        Com.SetFirst(ocx, "AudioCaptureRedirectionMode", o.Microphone, Com.Advanced);
        Com.SetFirst(ocx, "ConnectToAdministerServer", o.AdminSession, Com.Advanced);
        Com.SetFirst(ocx, "EnableAutoReconnect", true, Com.Advanced);
        Com.SetFirst(ocx, "MaxReconnectAttempts", 10, Com.Advanced);
        Com.SetFirst(ocx, "DisplayConnectionBar", true, Com.Advanced);
        Com.SetFirst(ocx, "ConnectionBarShowRestoreButton", true, Com.Advanced);
        Com.SetFirst(ocx, "ContainerHandledFullScreen", 0, Com.Advanced);
        Com.SetFirst(ocx, "GrabFocusOnConnect", true, Com.Advanced);
        Com.SetFirst(ocx, "FullScreenTitle", Profile.Name + " — SSH Explorer", "");
        if (o.GatewayMode != RdpGatewayMode.Never)
        {
            Com.SetFirst(ocx, "GatewayHostname", o.GatewayHost, "TransportSettings");
            Com.SetFirst(ocx, "GatewayUsageMethod", o.GatewayMode == RdpGatewayMode.Always ? 1 : 2, "TransportSettings");
            Com.SetFirst(ocx, "GatewayProfileUsageMethod", 1, "TransportSettings");
            Com.SetFirst(ocx, "GatewayCredsSource", 0, "TransportSettings");
            Com.SetFirst(ocx, "GatewayCredSharing", o.GatewaySameCredentials ? 1 : 0, "TransportSettings2");
            if (!o.GatewaySameCredentials && o.GatewayUsername.Length > 0) Com.SetFirst(ocx, "GatewayUsername", o.GatewayUsername, "TransportSettings2");
        }
        client!.SetPassword(password ?? "");
    }

    void OnEvent(RdpEvent e)
    {
        if (disposed) return;
        switch (e.Id)
        {
            case RdpEvent.Connecting: Update("Подключение…", null); break;
            case RdpEvent.Connected: Update("Вход…", null); break;
            case RdpEvent.ReceivedPublicKey: e.SetBool(true); break;
            case RdpEvent.LoginComplete:
                password = null; LastError = null;
                SetState(SessionState.Connected, "Подключён · " + Profile.Username + "@" + Profile.Endpoint);
                login?.TrySetResult(true); resize.Stop(); resize.Start();
                break;
            case RdpEvent.AutoReconnecting: Update("Связь потеряна, переподключение…", null); break;
            // Negative codes are winlogon arbitration notices (another session, continue logon), not failures.
            case RdpEvent.LogonError when e.Int(0) >= 0: LastError = e.Int(0) == 0 ? "Неверное имя пользователя или пароль." : "Вход не выполнен (код " + e.Int(0).ToString(CultureInfo.InvariantCulture) + ")."; break;
            case RdpEvent.FatalError: Fail("Ошибка элемента удалённого рабочего стола (код " + e.Int(0).ToString(CultureInfo.InvariantCulture) + ")."); break;
            case RdpEvent.Disconnected:
                {
                    int reason = e.Int(0);
                    bool wasConnected = State == SessionState.Connected;
                    if (userDisconnect) { if (State != SessionState.Disconnected) SetState(SessionState.Disconnected, "Отключено", LastError ?? "Подключение завершено."); login?.TrySetResult(false); }
                    else Fail(reason is 1 or 2 or 3 && wasConnected ? "Сеанс завершён на сервере." : Describe(reason));
                    break;
                }
        }
    }

    string Describe(int reason)
    {
        try
        {
            if (client?.Ocx is { } ocx)
            {
                var extended = Convert.ToUInt32(Com.Get(ocx, "ExtendedDisconnectReason") ?? 0, CultureInfo.InvariantCulture);
                if (Com.Call(ocx, "GetErrorDescription", (uint)reason, extended) is string { Length: > 0 } text) return text.Trim();
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or MissingMethodException or System.Reflection.TargetInvocationException or InvalidCastException or OverflowException) { }
        return RdpTrust.DescribeDisconnect(reason);
    }

    /// <summary>The TLS key reported before sign-in: trusted, confirmed now, or refused.</summary>
    bool VerifyKey(byte[]? key)
    {
        if (key is not { Length: > 0 }) { LastError = "Сервер не передал ключ для проверки."; return false; }
        var (verdict, prompt) = RdpTrust.Check(App.Store.Trust, Profile.Host, Profile.Port, key, sessionTrusted);
        if (verdict == RdpTrust.Verdict.Trusted) { keyVerified = true; return true; }
        var decision = Dialogs.RdpCertificate(prompt, Window.GetWindow(this));
        if (decision == TrustDecision.Reject) { LastError = verdict == RdpTrust.Verdict.Changed ? "Сертификат сервера изменён. Подключение отменено, пароль не отправлен." : "Сертификат сервера не принят. Пароль не отправлен."; return false; }
        sessionTrusted.Add(RdpTrust.SessionKey(Profile.Host, Profile.Port, key));
        if (decision == TrustDecision.Permanent)
            _ = PersistTrustAsync(key, verdict == RdpTrust.Verdict.Changed);
        keyVerified = true; return true;
    }

    async Task PersistTrustAsync(byte[] key, bool replace)
    {
        try { await App.Store.Trust.TrustAsync(Profile.Host, Profile.Port, RdpTrust.Algorithm, key, replace); }
        catch (Exception ex) { _ = LocalDiagnostics.RecordAsync("rdp.trust.save.failed", ex); Dispatcher.Invoke(() => Update(status.Text, "Сертификат принят для этого сеанса, но не сохранён: " + ErrorMessages.Describe(ex))); }
    }

    void Fail(string text)
    {
        password = null; LastError ??= text;
        SetState(SessionState.Disconnected, "Не подключён", text);
        login?.TrySetResult(false);
    }

    void FitDesktop()
    {
        if (disposed || State != SessionState.Connected || !Profile.Rdp.FitsTab || client?.Ocx is not { } ocx || !slot.IsVisible) return;
        var (w, h) = DesktopSize();
        if (Math.Abs(w - desktopWidth) < 8 && Math.Abs(h - desktopHeight) < 8) return;
        try { Com.Call(ocx, "UpdateSessionDisplaySettings", (uint)w, (uint)h, (uint)w, (uint)h, 0u, 100u, 100u); desktopWidth = w; desktopHeight = h; }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or MissingMethodException or System.Reflection.TargetInvocationException or ArgumentException)
        {
            // Older servers cannot resize a running session: scale the desktop into the tab instead.
            Com.SetFirst(ocx, "SmartSizing", true, Com.Advanced);
        }
    }

    void FullScreen()
    {
        if (client?.Ocx is not { } ocx) return;
        try { Com.Set(ocx, "FullScreen", true); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Reflection.TargetInvocationException) { Ui.Error("Не удалось включить полноэкранный режим: " + ErrorMessages.Describe(ex)); }
    }

    ContextMenu KeysMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Ui.Menu("Ctrl+Alt+Del", CtrlAltDel));
        menu.Items.Add(Ui.Menu("Пуск (клавиша Windows)", StartMenu));
        return menu;
    }

    internal void StartMenu()
    {
        try { client?.SendStartMenu(); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException or InvalidOperationException) { Ui.Error("Клавиша не отправлена: " + ErrorMessages.Describe(ex)); }
    }

    /// <summary>Saves what the tab shows as PNG; the file name carries the server and the time.</summary>
    internal void Snapshot()
    {
        if (client is null || State != SessionState.Connected) { Ui.Info("Снимок доступен, пока рабочий стол подключён."); return; }
        System.Drawing.Bitmap picture;
        try { picture = client.Snapshot(); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException) { Ui.Error("Снимок не получен: " + ErrorMessages.Describe(ex)); return; }
        using (picture)
        {
            string? path = SnapshotPathOverride is { } fake ? fake() : null;
            if (SnapshotPathOverride is null)
            {
                var safe = string.Concat(Profile.Name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Снимок рабочего стола", Filter = "Изображение PNG|*.png", FileName = $"{safe} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png", InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) };
                if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
                path = dialog.FileName;
            }
            if (string.IsNullOrEmpty(path)) return;
            try { picture.Save(path, System.Drawing.Imaging.ImageFormat.Png); Update("Снимок сохранён: " + System.IO.Path.GetFileName(path), null); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException) { Ui.Error("Не удалось сохранить снимок: " + ex.Message); }
        }
    }

    void CtrlAltDel()
    {
        try { client?.SendCtrlAltDel(); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException or InvalidOperationException) { Ui.Error("Ctrl+Alt+Del не отправлен: " + ErrorMessages.Describe(ex)); }
    }

    static bool IsConnected(object ocx)
    {
        try { return Convert.ToInt32(Com.Get(ocx, "Connected"), CultureInfo.InvariantCulture) != 0; }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Reflection.TargetInvocationException or InvalidCastException or MissingMethodException) { return false; }
    }

    void TryCall(string method)
    {
        try { if (client?.Ocx is { } ocx) Com.Call(ocx, method); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or MissingMethodException or System.Reflection.TargetInvocationException) { }
    }

    public Task DisconnectAsync()
    {
        userDisconnect = true;
        if (client?.Ocx is { } ocx && State is SessionState.Connected or SessionState.Connecting)
        {
            try { if (Convert.ToInt32(Com.Get(ocx, "Connected"), CultureInfo.InvariantCulture) != 0) { TryCall("Disconnect"); return Task.CompletedTask; } }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Reflection.TargetInvocationException or InvalidCastException) { }
        }
        SetState(SessionState.Disconnected, "Отключено", "Подключение завершено.");
        login?.TrySetResult(false);
        return Task.CompletedTask;
    }

    /// <summary>A temporary .rdp file for the Windows client; the password is never written.</summary>
    void OpenInMstsc()
    {
        try
        {
            var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SSHExplorer-rdp"); System.IO.Directory.CreateDirectory(folder);
            var file = System.IO.Path.Combine(folder, string.Concat(Profile.Name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".rdp");
            var (address, port) = Dialed is { } d && tunnel?.ForwardAlive == true ? (d.Host, d.Port) : (Profile.Host, Profile.Port);
            System.IO.File.WriteAllText(file, RdpFile.Write(Profile, address == Profile.Host ? null : address, port), System.Text.Encoding.Unicode);
            Process.Start(new ProcessStartInfo("mstsc.exe", "\"" + file + "\"") { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { Ui.Error("Не удалось открыть mstsc: " + ErrorMessages.Describe(ex)); }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        userDisconnect = true; TryCall("Disconnect");
        // Destroying the control while it is still disconnecting can block inside it: let the session end first.
        for (int i = 0; i < 100 && client?.Ocx is { } ocx && IsConnected(ocx); i++) await Task.Delay(100);
        disposed = true; resize.Stop(); login?.TrySetResult(false); password = null;
        layer.Children.Remove(host);
        client?.Dispose(); client = null;
        try { host.Dispose(); } catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        if (tunnel is not null) { await tunnel.DisposeAsync(); tunnel = null; }
        State = SessionState.Closed; StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
