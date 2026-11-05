using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>«Фрагменты»: saved terminal commands, shared by all servers. By default a fragment is only typed in.</summary>
public sealed class SnippetsWindow : Window
{
    readonly ObservableCollection<CommandSnippet> items;
    readonly ListBox list = new() { Name = "SnippetList", MinHeight = 260, DisplayMemberPath = nameof(CommandSnippet.Name) };
    readonly TextBox name = new() { Name = "SnippetName", MinHeight = 32 }, command = new() { Name = "SnippetCommand", MinHeight = 90, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
    readonly CheckBox run = new() { Name = "SnippetRun", Margin = new Thickness(0, 8, 0, 0), Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Выполнять сразу (нажимать Enter за вас). Без флажка команда только вписывается — удобно проверить перед запуском." } };
    readonly TextBlock problem = Ui.Text("", 12);
    readonly StackPanel editor = new();
    readonly Button save, remove;
    bool loading;
    public IReadOnlyList<CommandSnippet> Result => items;
    internal ListBox List => list;
    internal Button SaveButton => save;
    internal string? Problem => problem.Text.Length > 0 ? problem.Text : null;
    internal void AddNew(string n, string c, bool r = false) { Add(); name.Text = n; command.Text = c; run.IsChecked = r; Store(); }

    public SnippetsWindow(IEnumerable<CommandSnippet> snippets)
    {
        items = new(snippets.Select(s => s.Clone()));
        Title = "Фрагменты команд"; Width = 820; Height = 560; MinWidth = 620; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "DialogWindowStyle");
        var layout = new Grid { Margin = new Thickness(24, 20, 24, 12) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) }); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) }); layout.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new DockPanel();
        var head = Ui.DialogHeading("Фрагменты"); DockPanel.SetDock(head, Dock.Top); left.Children.Add(head);
        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) }; DockPanel.SetDock(buttons, Dock.Bottom);
        var add = Ui.Button("Добавить", Add); add.Name = "SnippetAdd"; add.Margin = new Thickness(0, 0, 8, 0); buttons.Children.Add(add);
        remove = Ui.Button("Удалить", Remove); remove.Name = "SnippetRemove"; buttons.Children.Add(remove);
        left.Children.Add(buttons);
        list.ItemsSource = items; list.Margin = new Thickness(0, 8, 0, 0); AutomationProperties.SetName(list, "Фрагменты"); left.Children.Add(list);
        layout.Children.Add(left);
        StackPanel Field(string label, Control box) { var p = new StackPanel { Margin = new Thickness(0, 0, 0, 10) }; var l = Ui.Text(label, 12); l.Margin = new Thickness(0, 0, 0, 4); l.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); p.Children.Add(l); p.Children.Add(box); AutomationProperties.SetName(box, label); return p; }
        var note = Ui.Text("Фрагмент вписывается в активный терминал (и во все, если включено «Во все терминалы»). Фрагменты общие для всех серверов и хранятся в параметрах этого компьютера.", 12);
        note.TextWrapping = TextWrapping.Wrap; note.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); note.Margin = new Thickness(0, 44, 0, 12);
        editor.Children.Add(note); editor.Children.Add(Field("Название", name)); editor.Children.Add(Field("Команда", command)); editor.Children.Add(run);
        problem.Name = "SnippetProblem"; problem.TextWrapping = TextWrapping.Wrap; problem.Margin = new Thickness(0, 8, 0, 0); problem.SetResourceReference(TextBlock.ForegroundProperty, "DangerText"); editor.Children.Add(problem);
        Grid.SetColumn(editor, 2); layout.Children.Add(editor);
        save = Ui.Button("Сохранить", () => { if (save!.IsEnabled) DialogResult = true; }); save.Name = "SnippetSave"; save.IsDefault = false; save.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        var cancel = Ui.Button("Отмена", () => DialogResult = false); cancel.IsCancel = true;
        Content = Ui.DialogSurface(layout, cancel, save);
        list.SelectionChanged += (_, _) => Load();
        name.TextChanged += (_, _) => Store(); command.TextChanged += (_, _) => Store(); run.Click += (_, _) => Store();
        if (items.Count > 0) list.SelectedIndex = 0;
        Load();
    }

    void Add() { var s = new CommandSnippet { Name = "Новый фрагмент", Command = "" }; items.Add(s); list.SelectedItem = s; name.Focus(); name.SelectAll(); }
    void Remove() { if (list.SelectedItem is CommandSnippet s) { var i = list.SelectedIndex; items.Remove(s); list.SelectedIndex = Math.Min(i, items.Count - 1); } Validate(); }

    void Load()
    {
        loading = true;
        var s = list.SelectedItem as CommandSnippet;
        editor.IsEnabled = remove.IsEnabled = s is not null;
        name.Text = s?.Name ?? ""; command.Text = s?.Command ?? ""; run.IsChecked = s?.RunImmediately == true;
        loading = false; Validate();
    }

    void Store()
    {
        if (loading || list.SelectedItem is not CommandSnippet s) return;
        s.Name = name.Text; s.Command = command.Text.Replace("\r\n", "\n"); s.RunImmediately = run.IsChecked == true;
        list.Items.Refresh(); Validate();
    }

    void Validate()
    {
        var bad = items.Select(s => (s, CommandSnippet.Validate(s))).FirstOrDefault(p => p.Item2 is not null);
        problem.Text = bad.s is null ? "" : $"«{(bad.s.Name.Trim().Length > 0 ? bad.s.Name : "без названия")}»: {bad.Item2}";
        save.IsEnabled = bad.s is null;
    }
}
