using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>
/// «Наборы правил»: save this server's rules as a named set, and apply a set to this server. Applying shows the
/// exact commands and this connection's access afterwards; the commands then go through the safe apply.
/// Sets live on this computer and travel with «Экспорт».
/// </summary>
public sealed class FirewallRuleSetsWindow : Window
{
    readonly FirewallSnapshot snapshot;
    readonly List<FirewallRuleSet> store;
    readonly Func<Task> persist;
    readonly ObservableCollection<FirewallRuleSet> sets;
    readonly ListBox list = new() { Name = "RuleSetList", MinHeight = 220, DisplayMemberPath = nameof(FirewallRuleSet.Name) };
    readonly TextBox newName = new() { Name = "RuleSetNewName", MinHeight = 32 }, rename = new() { Name = "RuleSetRename", MinHeight = 32 };
    readonly CheckBox withPolicy = new() { Name = "RuleSetWithPolicy", IsChecked = true, Margin = new Thickness(0, 6, 0, 0) };
    readonly CheckBox removeOthers = new() { Name = "RuleSetRemoveOthers", Margin = new Thickness(0, 8, 0, 0), Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Удалить правила сервера, которых нет в наборе (сервер станет точной копией набора)" } };
    readonly TextBlock rules = Ui.Text("", 12), preview = Ui.Text("", 12), verdict = Ui.Text("", 12), notes = Ui.Text("", 12), saveState = Ui.Text("", 12);
    readonly StackPanel details = new(), placeholder = new();
    readonly TextBlock emptyList = Ui.Text("", 12);
    readonly Button apply, saveNew, renameButton, delete;
    public IReadOnlyList<string> Commands { get; private set; } = [];
    public string? AppliedName { get; private set; }
    internal string Preview => preview.Text;
    internal string Verdict => verdict.Text;
    internal string SaveState => saveState.Text;
    internal Button ApplyButton => apply;
    internal bool PlaceholderVisible => placeholder.Visibility == Visibility.Visible;
    internal string EmptyText => emptyList.Text;
    internal Button SaveButton => saveNew;
    internal ListBox List => list;
    internal void TypeNewName(string name) => newName.Text = name;
    internal void SetRemoveOthers(bool on) { removeOthers.IsChecked = on; Update(); }
    internal Task SaveNewAsync() => SaveCurrentAsync();
    internal Task RenameAsync(string name) { rename.Text = name; return RenameSelectedAsync(); }
    internal Task DeleteAsync() => DeleteSelectedAsync(confirm: false);

    public FirewallRuleSetsWindow(FirewallSnapshot snapshot, List<FirewallRuleSet> store, Func<Task> persist, string serverName)
    {
        this.snapshot = snapshot; this.store = store; this.persist = persist;
        sets = new(store.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase));
        Title = "Наборы правил файервола"; Width = 980; SizeToContent = SizeToContent.Height; MaxHeight = Math.Max(480, SystemParameters.WorkArea.Height - 60); MinWidth = 720; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "DialogWindowStyle");
        var layout = new Grid { Margin = new Thickness(24, 20, 24, 12) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) }); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) }); layout.ColumnDefinitions.Add(new ColumnDefinition());

        // Left: the saved sets (or a short empty state), then saving this server's rules right under them — no gaps.
        var left = new StackPanel();
        left.Children.Add(Ui.DialogHeading("Наборы правил"));
        left.Children.Add(Muted("Набор — список правил, который можно применить к любому серверу. Хранится на этом компьютере и переносится «Экспортом».", new Thickness(0, 2, 0, 10)));
        var listHost = new Grid(); list.MinHeight = 120; list.MaxHeight = 260; listHost.Children.Add(list);
        emptyList.Name = "RuleSetEmpty"; emptyList.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); emptyList.TextWrapping = TextWrapping.Wrap; emptyList.IsHitTestVisible = false; emptyList.Margin = new Thickness(12); emptyList.VerticalAlignment = VerticalAlignment.Center; emptyList.HorizontalAlignment = HorizontalAlignment.Center; emptyList.TextAlignment = TextAlignment.Center; listHost.Children.Add(emptyList);
        list.ItemsSource = sets; AutomationProperties.SetName(list, "Сохранённые наборы правил"); left.Children.Add(listHost);
        var create = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        create.Children.Add(Ui.Text("Сохранить правила «" + serverName + "» как набор", 13, true));
        var label = Muted(snapshot.Rules.Count > 0 ? $"Название (правил на сервере: {snapshot.Rules.Count})" : "Название. Правил ufw на этом сервере нет — в набор войдёт только политика входящих.", new Thickness(0, 6, 0, 4)); create.Children.Add(label);
        AutomationProperties.SetName(newName, "Название нового набора"); create.Children.Add(newName);
        withPolicy.Content = $"Вместе с политикой входящих («{snapshot.DefaultIncoming}»)"; create.Children.Add(withPolicy);
        saveNew = Ui.AsyncButton("Сохранить набор", SaveCurrentAsync); saveNew.Name = "RuleSetSave"; saveNew.HorizontalAlignment = HorizontalAlignment.Left; saveNew.Margin = new Thickness(0, 8, 0, 0); create.Children.Add(saveNew);
        saveState.Name = "RuleSetSaveState"; saveState.TextWrapping = TextWrapping.Wrap; saveState.Margin = new Thickness(0, 6, 0, 0); saveState.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); create.Children.Add(saveState);
        left.Children.Add(create);
        layout.Children.Add(left);

        // Right: the chosen set and its plan for this server, or what a set is for when there are none yet.
        var rightHost = new Grid(); rightHost.Children.Add(details);
        placeholder.Name = "RuleSetPlaceholder"; placeholder.Margin = new Thickness(0, 52, 0, 0);
        placeholder.Children.Add(Ui.Text("Как пользоваться", 14, true));
        foreach (var step in new[] { "1. Сохраните правила сервера, который настроен как нужно, — форма слева.", "2. Откройте «Файервол» другого сервера и выберите здесь набор.", "3. Справа появятся команды и доступ вашего SSH-подключения после них; «Применить с проверкой…» применит их с возвратом при неудаче." })
            placeholder.Children.Add(Muted(step, new Thickness(0, 8, 0, 0)));
        rightHost.Children.Add(placeholder);
        var right = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = rightHost, MaxHeight = 560 }; Grid.SetColumn(right, 2); layout.Children.Add(right);
        details.Children.Add(Ui.Text("Название", 12));
        var renameRow = new Grid { Margin = new Thickness(0, 4, 0, 0) }; renameRow.ColumnDefinitions.Add(new ColumnDefinition()); renameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); renameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AutomationProperties.SetName(rename, "Название набора"); renameRow.Children.Add(rename);
        renameButton = Ui.AsyncButton("Переименовать", RenameSelectedAsync); renameButton.Name = "RuleSetRenameButton"; renameButton.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(renameButton, 1); renameRow.Children.Add(renameButton);
        delete = Ui.AsyncButton("Удалить…", () => DeleteSelectedAsync(confirm: true)); delete.Name = "RuleSetDelete"; delete.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(delete, 2); renameRow.Children.Add(delete);
        details.Children.Add(renameRow);
        details.Children.Add(Section("Правила набора"));
        rules.Name = "RuleSetRules"; rules.FontFamily = new FontFamily("Cascadia Mono, Consolas"); rules.TextWrapping = TextWrapping.Wrap; details.Children.Add(rules);
        details.Children.Add(Section("Применить к «" + serverName + "»"));
        details.Children.Add(removeOthers);
        preview.Name = "RuleSetPreview"; preview.FontFamily = new FontFamily("Cascadia Mono, Consolas"); preview.TextWrapping = TextWrapping.Wrap; preview.Margin = new Thickness(0, 8, 0, 0); details.Children.Add(preview);
        verdict.Name = "RuleSetVerdict"; verdict.TextWrapping = TextWrapping.Wrap; verdict.Margin = new Thickness(0, 8, 0, 0); details.Children.Add(verdict);
        notes.Name = "RuleSetNotes"; notes.TextWrapping = TextWrapping.Wrap; notes.Margin = new Thickness(0, 6, 0, 0); notes.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); details.Children.Add(notes);

        apply = Ui.Button("Применить с проверкой…", () => { if (apply!.IsEnabled) DialogResult = true; }); apply.Name = "RuleSetApply"; apply.IsDefault = true; apply.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        var close = Ui.Button("Закрыть", () => DialogResult = false); close.IsCancel = true;
        Content = Ui.DialogSurface(layout, close, apply);
        list.SelectionChanged += (_, _) => { rename.Text = (list.SelectedItem as FirewallRuleSet)?.Name ?? ""; Update(); };
        removeOthers.Click += (_, _) => Update(); newName.TextChanged += (_, _) => UpdateSave(); rename.TextChanged += (_, _) => UpdateSave(); withPolicy.Click += (_, _) => UpdateSave();
        if (sets.Count > 0) list.SelectedIndex = 0;
        Update(); UpdateSave();
    }

    static TextBlock Muted(string text, Thickness margin) { var t = Ui.Text(text, 12); t.TextWrapping = TextWrapping.Wrap; t.Margin = margin; t.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); return t; }
    static TextBlock Section(string text) { var t = Ui.Text(text, 14, true); t.Margin = new Thickness(0, 16, 0, 6); return t; }

    void UpdateSave()
    {
        saveNew.IsEnabled = newName.Text.Trim().Length > 0 && (snapshot.Rules.Count > 0 || withPolicy.IsChecked == true);
        renameButton.IsEnabled = list.SelectedItem is FirewallRuleSet s && rename.Text.Trim() != s.Name && FirewallRuleSets.ValidateName(rename.Text, store, s.Id) is null;
    }

    void Update()
    {
        if (list.SelectedItem is not FirewallRuleSet set)
        {
            details.Visibility = Visibility.Collapsed; placeholder.Visibility = Visibility.Visible; apply.IsEnabled = false; delete.IsEnabled = false; Commands = [];
            emptyList.Text = sets.Count == 0 ? "Сохранённых наборов пока нет" : ""; return;
        }
        details.Visibility = Visibility.Visible; placeholder.Visibility = Visibility.Collapsed; emptyList.Text = ""; delete.IsEnabled = true;
        rules.Text = set.Rules.Count == 0 ? "(пусто)" : string.Join("\n", set.Rules.Select((d, i) => $"{i + 1}. " + (Ufw.Validate(d) is { } bad ? "недопустимо: " + bad : Ufw.Spec(d with { Prepend = false })))) + (set.DefaultIncoming is { } p ? $"\nПолитика входящих: {p}" : "");
        var plan = FirewallRuleSets.Plan(snapshot, set, removeOthers.IsChecked == true);
        Commands = plan.Commands;
        if (plan.Commands.Count == 0) { preview.Text = "На сервере уже есть все правила набора — менять нечего."; verdict.Text = ""; apply.IsEnabled = false; }
        else
        {
            preview.Text = string.Join("\n", plan.Commands);
            var after = Ufw.CheckAfter(snapshot, plan.Commands);
            verdict.Text = (after.Allowed == false ? "⚠ " : "") + "Ваше SSH-подключение после этого: " + after.Reason;
            verdict.SetResourceReference(TextBlock.ForegroundProperty, after.Allowed == false ? "DangerText" : "Muted");
            apply.IsEnabled = true;
        }
        var summary = new List<string>();
        if (plan.Present.Count > 0) summary.Add($"Уже есть на сервере: {plan.Present.Count}.");
        if (plan.Added.Count > 0) summary.Add($"Будет добавлено: {plan.Added.Count}.");
        if (plan.Removed.Count > 0) summary.Add($"Будет удалено: {plan.Removed.Count}.");
        notes.Text = string.Join(" ", summary.Concat(plan.Notes));
        AppliedName = set.Name;
    }

    async Task SaveCurrentAsync()
    {
        if (FirewallRuleSets.ValidateName(newName.Text, store) is { } error) { saveState.Text = error; return; }
        var (set, skipped) = FirewallRuleSets.FromSnapshot(snapshot, newName.Text, withPolicy.IsChecked == true);
        store.Add(set); sets.Add(set); await persist();
        list.SelectedItem = set; newName.Text = "";
        saveState.Text = $"Набор «{set.Name}» сохранён: правил {set.Rules.Count}." + (skipped.Count > 0 ? $" Не вошли ({skipped.Count}): " + string.Join("; ", skipped.Select(r => r.Spec)) + " — их набор описать не может." : "");
    }

    async Task RenameSelectedAsync()
    {
        if (list.SelectedItem is not FirewallRuleSet set) return;
        if (FirewallRuleSets.ValidateName(rename.Text, store, set.Id) is { } error) { saveState.Text = error; return; }
        set.Name = rename.Text.Trim(); set.UpdatedUtc = DateTime.UtcNow; await persist();
        list.Items.Refresh(); UpdateSave(); Update();
    }

    async Task DeleteSelectedAsync(bool confirm)
    {
        if (list.SelectedItem is not FirewallRuleSet set) return;
        if (confirm && !Ui.Confirm($"Удалить набор «{set.Name}»? Правила на серверах не изменятся.", "Наборы правил")) return;
        store.Remove(set); sets.Remove(set); await persist();
        list.SelectedIndex = sets.Count > 0 ? 0 : -1; Update();
    }
}
