using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Renci.SshNet;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>Local, isolated xterm frontend; each instance owns a distinct SSH PTY.</summary>
public sealed class TerminalView : UserControl, IDisposable
{
    private readonly ServerSession _session;
    private readonly WebView2 _web = new();
    private readonly TextBlock _status = Ui.Text("Терминал • новая оболочка", 12);
    private readonly TextBox _search = new() { Width = 210, ToolTip = "Поиск в буфере терминала" };
    private readonly CancellationTokenSource _stop;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ShellStream? _shell;
    private Task? _start;
    private bool _disposed;
    private bool _resizing;
    private bool _usesBundledEngine;
    private int _outputId;
    private TaskCompletionSource? _outputAck;
    private int _font;
    private uint _columns = 100, _rows = 30;

    public TerminalView(ServerSession session)
    {
        _session = session;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(session.Lifetime);
        _font = App.Store.Settings.TerminalFontSize;
        var root = new DockPanel();
        var tools = Ui.Row(_status, new Border { Width = 24 }, _search,
            Ui.AsyncButton("↑", () => FindAsync(true), "Предыдущее совпадение"),
            Ui.AsyncButton("↓", () => FindAsync(false), "Следующее совпадение"),
            Ui.AsyncButton("−", () => ChangeFontAsync(-1), "Уменьшить шрифт"),
            Ui.AsyncButton("+", () => ChangeFontAsync(1), "Увеличить шрифт"),
            Ui.AsyncButton("Вставить", PasteAsync, "Вставить • Ctrl+Shift+V"));
        tools.Margin = new Thickness(12, 6, 12, 6); DockPanel.SetDock(tools, Dock.Top);
        root.Children.Add(tools); root.Children.Add(_web); Content = root;
        _search.KeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { await FindAsync(false); e.Handled = true; } };
        _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 17, 24, 32);
        IsVisibleChanged += async (_, _) => { if (IsVisible && _ready.Task.IsCompletedSuccessfully && !_disposed) await RunScriptAsync("window.sshExplorer.fit()"); };
    }

    public Task StartAsync(string? directory = null) => _start ??= InitializeAsync(directory);

    /// <summary>Explicit input, used only after creating a fresh terminal for htop/top.</summary>
    public Task SendTextAsync(string text) => WriteAsync(text);

    internal async Task<object> RunAcceptanceProbeAsync()
    {
        await StartAsync();
        // Octal output prevents command echo from satisfying the output assertions.
        await SendTextAsync("printf '\\033[31m\\123\\123\\110\\130\\137\\120\\122\\117\\102\\105\\033[0m \\320\\237\\321\\200\\320\\270\\320\\262\\320\\265\\321\\202 \\360\\237\\230\\200\\n'\r");
        JsonElement result = default;
        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(100, _stop.Token);
            var json = await _web.CoreWebView2.ExecuteScriptAsync("window.sshExplorer.probeStatus()");
            using var doc = JsonDocument.Parse(json); result = doc.RootElement.Clone();
            if (result.GetProperty("marker").GetBoolean() && result.GetProperty("utf8").GetBoolean() && result.GetProperty("red").GetBoolean()) break;
        }
        return new { EngineVersion = _web.CoreWebView2.Environment.BrowserVersionString, BundledEngine = _usesBundledEngine, Columns = result.GetProperty("cols").GetInt32(), Rows = result.GetProperty("rows").GetInt32(), RealOutput = result.GetProperty("marker").GetBoolean(), Utf8 = result.GetProperty("utf8").GetBoolean(), AnsiRed = result.GetProperty("red").GetBoolean() };
    }

    private async Task InitializeAsync(string? directory)
    {
        try
        {
            var runtime = Path.Combine(AppContext.BaseDirectory, "WebView2Runtime");
            // Developer-only fallback is never used by the published portable distribution.
            if (!File.Exists(Path.Combine(runtime, "msedgewebview2.exe")))
            {
                var development = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", ".tools", "webview-runtime"));
                if (Directory.Exists(development)) runtime = Directory.GetDirectories(development).FirstOrDefault(d => File.Exists(Path.Combine(d, "msedgewebview2.exe"))) ?? runtime;
            }
            if (!File.Exists(Path.Combine(runtime, "msedgewebview2.exe"))) throw new FileNotFoundException("В комплекте отсутствует WebView2Runtime. Распакуйте весь portable ZIP, включая папки рядом с EXE.");
            _usesBundledEngine = string.Equals(Path.GetFullPath(runtime), Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "WebView2Runtime")), StringComparison.OrdinalIgnoreCase);
            var assets = Path.Combine(AppContext.BaseDirectory, "Assets", "Terminal");
            if (!File.Exists(Path.Combine(assets, "index.html"))) throw new FileNotFoundException("Не найдены локальные ресурсы терминала.");
            var env = await CoreWebView2Environment.CreateAsync(runtime, Path.Combine(App.Store.DirectoryPath, "WebView2"));
            await _web.EnsureCoreWebView2Async(env);
            _stop.Token.ThrowIfCancellationRequested();
            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = true;
            core.SetVirtualHostNameToFolderMapping("terminal.local", assets, CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, e) => { if (e.Uri != "https://terminal.local/index.html") e.Cancel = true; };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "/index.html", "/terminal.js", "/terminal.css", "/xterm.js", "/xterm.css", "/addon-fit.js", "/addon-search.js" };
            core.WebResourceRequested += (_, e) =>
            {
                if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "terminal.local" || !allowed.Contains(uri.AbsolutePath))
                    e.Response = env.CreateWebResourceResponse(new MemoryStream(), 403, "Forbidden", "Content-Type: text/plain");
            };
            core.WebMessageReceived += MessageReceived;
            core.Navigate("https://terminal.local/index.html");
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(20), _stop.Token);
            await RunScriptAsync($"window.sshExplorer.font({_font})");
            var shell = await _session.CreateShellAsync(_columns, _rows, directory, _stop.Token);
            if (_disposed || _stop.IsCancellationRequested) { _ = Task.Run(shell.Dispose); _stop.Token.ThrowIfCancellationRequested(); return; }
            _shell = shell;
            _status.Text = $"SSH • {_session.Profile.Username}@{_session.Profile.Host} • новая оболочка";
            _ = ReadLoopAsync(_shell);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = ErrorMessages.Describe(ex); throw; }
    }

    private async void MessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_disposed || e.Source != "https://terminal.local/index.html" || e.WebMessageAsJson.Length > 2 * 1024 * 1024) return;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var data = doc.RootElement;
            switch (data.GetProperty("type").GetString())
            {
                case "ready":
                    _columns = (uint)Math.Clamp(data.GetProperty("cols").GetInt32(), 2, 1000);
                    _rows = (uint)Math.Clamp(data.GetProperty("rows").GetInt32(), 2, 1000); _ready.TrySetResult(); break;
                case "input": await WriteAsync(data.GetProperty("data").GetString() ?? ""); break;
                case "resize":
                    _columns = (uint)Math.Clamp(data.GetProperty("cols").GetInt32(), 2, 1000);
                    _rows = (uint)Math.Clamp(data.GetProperty("rows").GetInt32(), 2, 1000);
                    await ResizeAsync();
                    break;
                case "outputAck": if (data.GetProperty("id").GetInt32() == _outputId) _outputAck?.TrySetResult(); break;
                case "copy": var text = data.GetProperty("data").GetString(); if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text); break;
                case "paste": await PasteAsync(); break;
                case "search": _search.Focus(); _search.SelectAll(); break;
                case "font": await ChangeFontAsync(Math.Clamp(data.GetProperty("delta").GetInt32(), -1, 1)); break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = ErrorMessages.Describe(ex); }
    }

    private async Task ReadLoopAsync(ShellStream shell)
    {
        try
        {
            await Task.Run(async () =>
            {
                var buffer = new byte[16 * 1024];
                while (!_stop.IsCancellationRequested)
                {
                    var count = await shell.ReadAsync(buffer.AsMemory(), _stop.Token);
                    if (count == 0) break;
                    var encoded = Convert.ToBase64String(buffer, 0, count);
                    // One 16 KiB chunk in flight. ACK follows xterm's parser callback, providing real backpressure.
                    var ack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    await Dispatcher.InvokeAsync(async () =>
                    {
                        _outputAck = ack; var id = ++_outputId;
                        await RunScriptAsync($"window.sshExplorer.write({JsonSerializer.Serialize(encoded)}, {id})");
                    }).Task.Unwrap();
                    await ack.Task.WaitAsync(TimeSpan.FromSeconds(30), _stop.Token);
                }
            }, _stop.Token);
            if (!_disposed) await Dispatcher.InvokeAsync(() => _status.Text = "Оболочка завершена. История сохранена на экране.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed) await Dispatcher.InvokeAsync(() => _status.Text = ErrorMessages.Describe(ex)); }
    }

    private async Task WriteAsync(string text)
    {
        var shell = _shell;
        if (shell is null || text.Length > 1024 * 1024) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        await _writeGate.WaitAsync(_stop.Token);
        try { await Task.Run(() => { shell.Write(bytes, 0, bytes.Length); shell.Flush(); }, _stop.Token); }
        finally { _writeGate.Release(); }
    }

    private async Task ResizeAsync()
    {
        if (_resizing || _shell is null) return;
        _resizing = true;
        try
        {
            uint columns, rows;
            do
            {
                columns = _columns; rows = _rows; var shell = _shell;
                await Task.Run(() => shell.ChangeWindowSize(columns, rows, 0, 0), _stop.Token);
            } while (!_disposed && (columns != _columns || rows != _rows));
        }
        finally { _resizing = false; }
    }

    private async Task PasteAsync()
    {
        if (!_ready.Task.IsCompletedSuccessfully || !Clipboard.ContainsText()) return;
        var text = Clipboard.GetText();
        if (text.Length > 1024 * 1024) { Ui.Error("Вставка ограничена 1 МБ."); return; }
        if ((text.Contains('\n') || text.Contains('\r')) && !Ui.Confirm("Вставить несколько строк в терминал? Они могут выполнить команды на сервере.", "Многострочная вставка")) return;
        await RunScriptAsync($"window.sshExplorer.paste({JsonSerializer.Serialize(text)})");
    }
    private Task FindAsync(bool previous) => RunScriptAsync($"window.sshExplorer.search({JsonSerializer.Serialize(_search.Text)}, {(previous ? "true" : "false")})");
    private async Task ChangeFontAsync(int delta)
    {
        _font = Math.Clamp(_font + delta, 9, 32); App.Store.Settings.TerminalFontSize = _font;
        await RunScriptAsync($"window.sshExplorer.font({_font})"); await App.Store.SaveAsync();
    }
    private async Task RunScriptAsync(string script) { if (!_disposed && _web.CoreWebView2 is not null) await _web.CoreWebView2.ExecuteScriptAsync(script); }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _stop.Cancel(); _ready.TrySetCanceled(); _outputAck?.TrySetCanceled();
        var shell = _shell; if (shell is not null) _ = Task.Run(shell.Dispose);
        if (_web.CoreWebView2 is not null) _web.CoreWebView2.WebMessageReceived -= MessageReceived;
        _web.Dispose();
    }
}
