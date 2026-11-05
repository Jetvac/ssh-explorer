using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>A key allowed to log in as this user.</summary>
public sealed record AuthorizedKeyRow(AuthorizedKey Key, bool Current)
{
    public string Type => Key.TypeText;
    public string Fingerprint => Key.Fingerprint;
    public string Comment => Key.Comment.Length > 0 ? Key.Comment : "—";
    public string Notes => string.Join(" · ", new[] { Current ? "этим ключом входит этот профиль" : null, Key.Weak ? "слабый ключ — замените на Ed25519" : null, Key.Options.Length > 0 ? "ограничения: " + Key.Options : null }.OfType<string>());
}

/// <summary>
/// «Защита SSH»: who may log in as this user by key (<c>authorized_keys</c>), switching this profile to key login, and
/// the SSH server's own settings. Changes to the keys file never touch other users.
/// </summary>
public sealed partial class SecurityView : UserControl, IDisposable
{
    readonly KeyLoginService keys;
    readonly ObservableCollection<AuthorizedKeyRow> rows = [];
    readonly TextBlock login = Ui.Text("", 13), keysState = Ui.Text("", 12), state = Ui.Text("", 12);
    readonly Dictionary<string, Button> commands = [];
    readonly CancellationTokenSource lifetime = new();
    readonly StackPanel page = new() { Margin = new Thickness(16, 12, 16, 16) };
    bool loaded, busy;
    internal DataGrid Table { get; } = new() { Name = "AuthorizedKeysTable", IsReadOnly = true, AutoGenerateColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single, MinHeight = 120, MaxHeight = 320 };
    internal Func<string, bool>? ConfirmOverride { get; set; }
    internal Func<KeyLoginWindow, bool?>? KeyWindowOverride { get; set; }
    internal Func<string?>? PastePublicOverride { get; set; }
    internal string StateText => state.Text;
    internal string LoginText => login.Text;
    internal ObservableCollection<AuthorizedKeyRow> Rows => rows;
    AuthorizedKeyRow? Selected => Table.SelectedItem as AuthorizedKeyRow;

    public SecurityView(KeyLoginService service)
    {
        keys = service; Name = "SecurityView";
        SetResourceReference(BackgroundProperty, "Paper");
        var title = Ui.Text("Защита SSH", 18, true);
        var subtitle = Ui.Text("ключи и настройки входа · " + keys.Session.Profile.Name, 12); subtitle.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); subtitle.TextTrimming = TextTrimming.CharacterEllipsis; subtitle.MaxWidth = 360;
        var heading = new StackPanel { Margin = new Thickness(4, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center }; heading.Children.Add(title); heading.Children.Add(subtitle);
        var bar = new ResponsiveCommandBar { Name = "SecurityCommandBar", VerticalAlignment = VerticalAlignment.Center };
        void Command(string key, string glyph, string label, Func<Task> action, string tooltip, int priority)
        {
            var b = Ui.AsyncCommandButton(glyph, label, () => Run(action), tooltip); b.Name = "Security" + key; commands[key] = b;
            bar.Add(key, b, () => Ui.AsyncMenu(label, () => Run(action), glyph: glyph), priority);
        }
        Command("Refresh", Ui.RefreshGlyph, "Обновить", () => RefreshAsync(), "Заново прочитать ключи и настройки", 100);
        Command("KeyLogin", IconCatalog.Server, "Вход по ключу…", KeyLoginAsync, "Создать ключ, добавить его на сервер, проверить вход и переключить профиль", 95);
        Command("AddKey", Ui.AddGlyph, "Добавить открытый ключ…", AddKeyAsync, "Разрешить вход ещё одному ключу (например, с другого компьютера)", 70);
        Command("RemoveKey", Ui.DeleteGlyph, "Удалить ключ…", RemoveKeyAsync, "Запретить вход выбранному ключу", 60);
        AddHardeningCommands(Command);
        var barRow = new DockPanel(); DockPanel.SetDock(heading, Dock.Left); barRow.Children.Add(heading); var divider = Ui.Line(); DockPanel.SetDock(divider, Dock.Left); barRow.Children.Add(divider); barRow.Children.Add(bar);
        var barHost = new Border { Child = barRow, Padding = new Thickness(10, 4, 10, 4) }; barHost.SetResourceReference(StyleProperty, "CommandBarStyle");

        page.Children.Add(Section("Вход в этот профиль"));
        login.Name = "SecurityLogin"; login.TextWrapping = TextWrapping.Wrap; page.Children.Add(login);
        page.Children.Add(Section("Ключи, которым разрешён вход (~/.ssh/authorized_keys)"));
        Table.SetResourceReference(StyleProperty, "ProcessTableStyle");
        MainWindow.AddColumn(Table, "Тип", nameof(AuthorizedKeyRow.Type), 130); MainWindow.AddColumn(Table, "Отпечаток", nameof(AuthorizedKeyRow.Fingerprint), 360);
        MainWindow.AddColumn(Table, "Комментарий", nameof(AuthorizedKeyRow.Comment), 220); MainWindow.AddColumn(Table, "Замечания", nameof(AuthorizedKeyRow.Notes), 220);
        Table.Columns[^1].Width = new DataGridLength(1, DataGridLengthUnitType.Star);
        Table.ItemsSource = rows; Table.SelectionChanged += (_, _) => UpdateCommands();
        page.Children.Add(Table);
        keysState.Name = "SecurityKeysState"; keysState.TextWrapping = TextWrapping.Wrap; keysState.Margin = new Thickness(0, 6, 0, 0); keysState.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); page.Children.Add(keysState);
        BuildHardening(page);

        state.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); state.Margin = new Thickness(16, 6, 16, 6); state.TextTrimming = TextTrimming.CharacterEllipsis; state.Name = "SecurityState";
        var footer = new Border { Child = state }; footer.SetResourceReference(StyleProperty, "StatusBarStyle");
        var root = new DockPanel(); DockPanel.SetDock(barHost, Dock.Top); root.Children.Add(barHost); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = root;
        DescribeLogin(); UpdateCommands();
        IsVisibleChanged += async (_, e) => { if (e.NewValue is true && !loaded) await Ui.Guard(() => RefreshAsync()); };
    }

    static TextBlock Section(string text) { var t = Ui.Text(text, 14, true); t.Margin = new Thickness(0, 14, 0, 6); return t; }

    ServerProfile CurrentProfile => App.Store.Profiles.FirstOrDefault(p => p.Id == keys.Session.Profile.Id) ?? keys.Session.Profile;

    void DescribeLogin()
    {
        var p = CurrentProfile;
        login.Text = p.Authentication switch
        {
            AuthenticationKind.PrivateKey => $"По ключу: {p.PrivateKeyPath}.",
            AuthenticationKind.Agent => "Через SSH-агент: ключи хранит агент.",
            AuthenticationKind.KeyboardInteractive => "Интерактивный вход (пароль или код). Вход по ключу надёжнее: «Вход по ключу…».",
            _ => "По паролю. Вход по ключу надёжнее и не боится перебора паролей: «Вход по ключу…».",
        };
    }

    async Task Run(Func<Task> action)
    {
        if (busy) return; busy = true; UpdateCommands();
        try { await action(); }
        catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or ArgumentException or Renci.SshNet.Common.SshException) { state.Text = ErrorMessages.Describe(ex); Ui.Error(ErrorMessages.Describe(ex)); }
        finally { busy = false; UpdateCommands(); }
    }

    public async Task RefreshAsync()
    {
        loaded = true; DescribeLogin();
        if (keys.Session.State != SessionState.Connected && keys.ExecuteOverride is null) { keysState.Text = "Сервер не подключён."; state.Text = "Нет подключения"; return; }
        try
        {
            var list = await keys.ListAsync(lifetime.Token);
            var current = LocalKeys.CurrentFingerprint(CurrentProfile);
            rows.Clear(); foreach (var k in list) rows.Add(new AuthorizedKeyRow(k, current is not null && k.Fingerprint == current));
            keysState.Text = list.Count == 0 ? "Ключей нет: войти можно только паролем (если сервер его разрешает)." : $"Ключей: {list.Count}." + (list.Any(k => k.Weak) ? " Есть слабые ключи." : "");
        }
        catch (Exception ex) when (ex is System.IO.IOException or Renci.SshNet.Common.SshException or InvalidOperationException) { keysState.Text = "Не удалось прочитать ключи: " + ErrorMessages.Describe(ex); }
        await RefreshHardeningAsync();
        state.Text = $"Ключей: {rows.Count}";
        UpdateCommands();
    }

    void UpdateCommands()
    {
        commands["Refresh"].IsEnabled = !busy;
        commands["KeyLogin"].IsEnabled = commands["AddKey"].IsEnabled = !busy && loaded;
        commands["RemoveKey"].IsEnabled = !busy && Selected is not null;
        UpdateHardeningCommands(!busy && loaded);
    }

    bool Confirm(string text) => ConfirmOverride?.Invoke(text) ?? Ui.Confirm(text, "Защита SSH");

    async Task KeyLoginAsync()
    {
        var window = new KeyLoginWindow(keys, CurrentProfile, async (profile, secret) => { await App.Store.SaveProfileAsync(profile, secret, lifetime.Token); App.RaiseProfilesChanged(); }) { Owner = Window.GetWindow(this) };
        var shown = KeyWindowOverride?.Invoke(window) ?? window.ShowDialog();
        _ = shown;
        await RefreshAsync();
        if (window.Switched) state.Text = "Профиль переключён на вход по ключу.";
    }

    async Task AddKeyAsync()
    {
        string? text = PastePublicOverride is not null ? PastePublicOverride() : AskPublicKey();
        if (text is null) return;
        var (key, error) = SshKeys.ParsePublic(text);
        if (key is null || error is not null) { Ui.Error(error ?? "Строка не похожа на открытый ключ."); return; }
        if (!Confirm($"Разрешить вход как «{keys.Session.Profile.Username}» на «{keys.Session.Profile.Name}» ключу {key.TypeText}?\n\nОтпечаток: {key.Fingerprint}\nКомментарий: {(key.Comment.Length > 0 ? key.Comment : "—")}\n\nДобавляйте только ключи, владельцу которых вы доверяете доступ к серверу.")) return;
        state.Text = await keys.InstallAsync(key, lifetime.Token) ? "Ключ добавлен." : "Этот ключ уже был.";
        await RefreshAsync();
    }

    string? AskPublicKey()
    {
        var pick = new Microsoft.Win32.OpenFileDialog { Title = "Открытый ключ (.pub)", Filter = "Открытый ключ (*.pub)|*.pub|Все файлы|*.*", InitialDirectory = System.IO.Directory.Exists(LocalKeys.Directory) ? LocalKeys.Directory : null, CheckFileExists = true };
        if (pick.ShowDialog(Window.GetWindow(this)) != true) return null;
        var info = new System.IO.FileInfo(pick.FileName);
        if (info.Length > 16 * 1024) { Ui.Error("Файл слишком большой для открытого ключа."); return null; }
        return System.IO.File.ReadAllText(pick.FileName);
    }

    async Task RemoveKeyAsync()
    {
        if (Selected is not { } row) return;
        var warning = row.Current ? "\n\n⚠ Этим ключом входит этот профиль. После удаления войти по нему будет нельзя." : rows.Count == 1 ? "\n\n⚠ Это последний ключ: войти можно будет только паролем, если сервер его разрешает." : "";
        if (!Confirm($"Запретить вход ключу {row.Type} «{row.Comment}»?\n\nОтпечаток: {row.Fingerprint}{warning}\n\nТекущее подключение не прервётся.")) return;
        await keys.RemoveAsync(row.Key, lifetime.Token);
        state.Text = "Ключ удалён из ~/.ssh/authorized_keys.";
        await RefreshAsync();
    }

    public void Dispose() => lifetime.Cancel();
}
