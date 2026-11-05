using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>An open server tab that can receive files, with its current folder.</summary>
public sealed record CopyTarget(string Title, ServerSession Session, string Folder)
{
    public string Text => $"{Title} — {Session.Profile.Username}@{Session.Profile.Endpoint}";
}

/// <summary>
/// «Копировать на другой сервер»: pick an open server tab and a folder there. The copy goes through this computer,
/// like dragging between tabs: neither server connects to the other and no credentials are sent anywhere.
/// </summary>
public sealed class CopyToServerWindow : Window
{
    readonly ListBox targets = new() { Name = "CopyTargets", MinHeight = 120, MaxHeight = 240, DisplayMemberPath = nameof(CopyTarget.Text) };
    readonly TextBox destination = new() { Name = "CopyDestination", MinHeight = 32 };
    readonly CheckBox move = new() { Name = "CopyMove", Content = "Переместить: удалить здесь после проверенного копирования", Margin = new Thickness(0, 10, 0, 0) };
    readonly Button ok;
    public CopyTarget? Target => targets.SelectedItem as CopyTarget;
    public string Destination => destination.Text.Trim();
    public bool Move => move.IsChecked == true;
    internal void Choose(int index, string? folder = null, bool moveIt = false) { targets.SelectedIndex = index; if (folder is not null) destination.Text = folder; move.IsChecked = moveIt; }
    internal Button OkButton => ok;

    public CopyToServerWindow(string what, IReadOnlyList<CopyTarget> list)
    {
        Title = "Копировать на другой сервер"; Width = 620; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "DialogWindowStyle");
        var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(Ui.DialogHeading("Копировать «" + what + "»"));
        var note = Ui.Text("Данные идут через этот компьютер, как при перетаскивании между вкладками: серверы не подключаются друг к другу, пароли и ключи никуда не передаются. Совпадающие имена — с выбором, что сделать.", 12);
        note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(0, 2, 0, 10); note.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); body.Children.Add(note);
        body.Children.Add(Ui.Text("Куда: открытые вкладки серверов", 12));
        targets.ItemsSource = list; targets.Margin = new Thickness(0, 4, 0, 10); AutomationProperties.SetName(targets, "Сервер назначения"); body.Children.Add(targets);
        body.Children.Add(Ui.Text("Папка на сервере назначения", 12));
        destination.Margin = new Thickness(0, 4, 0, 0); AutomationProperties.SetName(destination, "Папка на сервере назначения"); body.Children.Add(destination);
        body.Children.Add(move);
        var cancel = Ui.Button("Отмена", () => DialogResult = false); cancel.IsCancel = true;
        ok = Ui.Button("Копировать", () => { if (ok!.IsEnabled) DialogResult = true; }); ok.Name = "CopyOk"; ok.IsDefault = true; ok.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        Content = Ui.DialogSurface(body, cancel, ok);
        targets.SelectionChanged += (_, _) => { if (Target is { } t) destination.Text = t.Folder; Update(); };
        destination.TextChanged += (_, _) => Update(); move.Click += (_, _) => ok.Content = Move ? "Переместить" : "Копировать";
        if (list.Count > 0) targets.SelectedIndex = 0;
        Update();
    }

    void Update() => ok.IsEnabled = Target is not null && Destination.StartsWith('/') || Target is not null && Destination.StartsWith('~');
}
