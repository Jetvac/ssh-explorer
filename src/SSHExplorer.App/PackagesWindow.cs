using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>
/// «Обновления пакетов»: what the server's package manager would update (security first), how old its lists are, and
/// whether a reboot is pending. Installing goes to the terminal as a typed command the user runs, so sudo asks its
/// password there.
/// </summary>
public sealed class PackagesWindow : Window
{
    readonly Func<CancellationToken, Task<string>> read;
    readonly Func<string, Task>? typeInTerminal;
    readonly ObservableCollection<PackageUpdate> rows = [];
    readonly TextBlock summary = Ui.Text("", 13), note = Ui.Text("", 12);
    readonly DataGrid table = new() { Name = "PackagesTable", IsReadOnly = true, AutoGenerateColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single, MinHeight = 200 };
    readonly Button refreshLists, upgradeSecurity, upgradeAll;
    PackageReport? report;
    internal PackageReport? Report => report;
    internal string SummaryText => summary.Text;
    internal string NoteText => note.Text;
    internal Button SecurityButton => upgradeSecurity;
    internal string? Typed { get; private set; }

    public PackagesWindow(string server, Func<CancellationToken, Task<string>> read, Func<string, Task>? typeInTerminal)
    {
        this.read = read; this.typeInTerminal = typeInTerminal;
        Title = "Обновления пакетов"; Width = 900; Height = 620; MinWidth = 640; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "DialogWindowStyle");
        var body = new DockPanel { Margin = new Thickness(24, 20, 24, 12) };
        var head = new StackPanel(); DockPanel.SetDock(head, Dock.Top);
        head.Children.Add(Ui.DialogHeading("Обновления пакетов · " + server));
        summary.Name = "PackagesSummary"; summary.TextWrapping = TextWrapping.Wrap; summary.Margin = new Thickness(0, 4, 0, 4); head.Children.Add(summary);
        note.Name = "PackagesNote"; note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(0, 0, 0, 8); note.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); head.Children.Add(note);
        body.Children.Add(head);
        table.SetResourceReference(StyleProperty, "ProcessTableStyle");
        MainWindow.AddColumn(table, "Пакет", nameof(PackageUpdate.Name), 220); MainWindow.AddColumn(table, "Сейчас", nameof(PackageUpdate.Current), 170); MainWindow.AddColumn(table, "Новая версия", nameof(PackageUpdate.Version), 190);
        MainWindow.AddColumn(table, "Источник", nameof(PackageUpdate.Source), 160);
        table.Columns.Add(new DataGridTextColumn { Header = "Безопасность", Binding = new System.Windows.Data.Binding(nameof(PackageUpdate.Security)) { Converter = new SecurityText() }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        table.ItemsSource = rows; body.Children.Add(table);
        refreshLists = Ui.Button("Обновить списки в терминале", () => Type(Packages.RefreshCommand(report!))); refreshLists.Name = "PackagesRefreshLists";
        upgradeSecurity = Ui.Button("Только безопасность в терминале", () => Type(Packages.UpgradeCommand(report!, true))); upgradeSecurity.Name = "PackagesSecurity";
        upgradeAll = Ui.Button("Все обновления в терминале", () => Type(Packages.UpgradeCommand(report!, false))); upgradeAll.Name = "PackagesAll"; upgradeAll.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        var again = Ui.AsyncButton("Проверить снова", LoadAsync);
        var close = Ui.Button("Закрыть", () => DialogResult = false); close.IsCancel = true;
        Content = Ui.DialogSurface(body, close, again, refreshLists, upgradeSecurity, upgradeAll);
        foreach (var b in new[] { refreshLists, upgradeSecurity, upgradeAll }) b.IsEnabled = false;
        Loaded += async (_, _) => await Ui.Guard(LoadAsync);
    }

    sealed class SecurityText : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) => value is true ? "да" : "";
        public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }

    internal async Task LoadAsync()
    {
        summary.Text = "Проверка…";
        try { report = Packages.Parse(await read(CancellationToken.None)); }
        catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or Renci.SshNet.Common.SshException) { summary.Text = "Не удалось проверить обновления: " + ErrorMessages.Describe(ex); return; }
        rows.Clear(); foreach (var u in report.Updates) rows.Add(u);
        summary.Text = report.Summary;
        var notes = new List<string>();
        if (report.ListsAgeHours is { } h && h > 24) notes.Add($"Списки пакетов обновлялись {(h >= 48 ? $"{h / 24:0} дн." : $"{h:0} ч")} назад — данные могли устареть; «Обновить списки» обновит их.");
        if (report.RebootRequired) notes.Add("Сервер ждёт перезагрузки после прошлых обновлений (ядро или системные библиотеки).");
        if (report.Manager is "dnf" or "yum") notes.Add("Список взят из кэша dnf/yum без обращения к сети.");
        notes.Add(typeInTerminal is null ? "Терминал недоступен: выполните команды вручную." : "Кнопки вписывают команду в терминал этого сервера — проверьте её и нажмите Enter; пароль sudo вводится там же.");
        note.Text = string.Join(" ", notes);
        bool ok = report.Problem is null && typeInTerminal is not null;
        refreshLists.IsEnabled = ok; upgradeAll.IsEnabled = ok && report.Updates.Count > 0; upgradeSecurity.IsEnabled = ok && report.SecurityCount > 0 && report.Manager != "";
    }

    void Type(string command)
    {
        if (command.Length == 0 || typeInTerminal is null) return;
        Typed = command;
        try { DialogResult = true; } catch (InvalidOperationException) { Close(); }
        _ = typeInTerminal(command);
    }
}
