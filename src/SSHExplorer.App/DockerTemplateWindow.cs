using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SSHExplorer.App.ViewModels;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>One editable environment variable. A stored secret is never shown; an empty value keeps it.</summary>
public sealed class DockerEnvRow : ObservableObject
{
    string key = "", value = ""; bool secret;
    public string Key { get => key; set => Set(ref key, value); }
    public string Value { get => value; set { Set(ref this.value, value); Raise(nameof(Hint)); } }
    public bool Secret { get => secret; set { Set(ref secret, value); Raise(nameof(Hint)); } }
    /// <summary>Encrypted value already saved for this key (kept unless a new value is typed).</summary>
    public string StoredProtected { get; init; } = "";
    public string Hint => Secret && StoredProtected.Length > 0 && Value.Length == 0 ? "сохранено · введите, чтобы заменить" : Secret ? "секрет · хранится зашифрованным" : "";
}

/// <summary>Run parameters of one container: image, name, restart policy, ports, volumes, network, command and env.</summary>
public sealed class DockerTemplateWindow : Window
{
    public sealed record Result(DockerTemplate Template, bool Run);

    readonly DockerTemplate original; readonly bool exists; readonly IReadOnlyCollection<string> otherNames;
    readonly TextBox image = Field("DockerImage"), name = Field("DockerName"), network = Field("DockerNetwork"), command = Field("DockerCommand");
    readonly TextBox ports = Lines("DockerPorts"), volumes = Lines("DockerVolumes");
    readonly ComboBox restart = new() { ItemsSource = DockerCommands.RestartPolicies, MinHeight = 32, Name = "DockerRestart" };
    readonly TextBlock errors = Ui.Text("", 12);
    public ObservableCollection<DockerEnvRow> EnvRows { get; } = [];
    internal DataGrid EnvTable { get; } = new() { Name = "DockerEnv", AutoGenerateColumns = false, CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column, MinHeight = 120, MaxHeight = 260 };
    public Result? Accepted { get; private set; }

    public DockerTemplateWindow(DockerTemplate template, bool existingContainer, IReadOnlyCollection<string> otherContainerNames)
    {
        original = template; exists = existingContainer; otherNames = otherContainerNames;
        Title = existingContainer ? "Параметры запуска — " + template.ContainerName : "Новый контейнер"; Width = 760; Height = 760; MinWidth = 600; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; Style = (Style)FindResource(typeof(Window));
        image.Text = template.Image; name.Text = template.ContainerName; network.Text = template.Network; command.Text = template.Command;
        ports.Text = string.Join(Environment.NewLine, template.Ports); volumes.Text = string.Join(Environment.NewLine, template.Volumes);
        restart.SelectedItem = DockerCommands.RestartPolicies.Contains(template.RestartPolicy) ? template.RestartPolicy : "unless-stopped";
        foreach (var e in template.Env) EnvRows.Add(new DockerEnvRow { Key = e.Key, Value = e.Secret ? "" : e.Value, Secret = e.Secret, StoredProtected = e.Protected });

        var form = new StackPanel { Margin = new Thickness(24, 20, 24, 12) };
        form.Children.Add(Ui.DialogHeading(Title));
        var note = Ui.Text(existingContainer ? "Docker не меняет параметры существующего контейнера: «Сохранить и пересоздать» удалит его и запустит заново с этими параметрами. Данные в томах сохраняются." : "Параметры запоминаются для этого сервера: контейнер можно быстро пересоздать с ними позже.", 12);
        note.TextWrapping = TextWrapping.Wrap; note.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); note.Margin = new Thickness(0, 4, 0, 12); form.Children.Add(note);
        var top = new Grid(); top.ColumnDefinitions.Add(new ColumnDefinition()); top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); top.ColumnDefinitions.Add(new ColumnDefinition());
        var left = Labeled("Образ", image, "например nginx:1.27 или ghcr.io/org/app:2"); var right = Labeled("Имя контейнера", name, null); Grid.SetColumn(right, 2); top.Children.Add(left); top.Children.Add(right); form.Children.Add(top);
        var second = new Grid(); second.ColumnDefinitions.Add(new ColumnDefinition()); second.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); second.ColumnDefinitions.Add(new ColumnDefinition());
        var restartBox = Labeled("Перезапуск", restart, null); var networkBox = Labeled("Сеть Docker", network, "пусто — сеть по умолчанию"); Grid.SetColumn(networkBox, 2); second.Children.Add(restartBox); second.Children.Add(networkBox); form.Children.Add(second);

        form.Children.Add(Ui.Text("Переменные окружения", 14, true));
        var envHint = Ui.Text("Значения передаются через временный файл с правами 600, а не командной строкой. Отмеченные «Секрет» хранятся на этом компьютере зашифрованными (DPAPI).", 12);
        envHint.TextWrapping = TextWrapping.Wrap; envHint.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); envHint.Margin = new Thickness(0, 2, 0, 6); form.Children.Add(envHint);
        EnvTable.SetResourceReference(StyleProperty, "ProcessTableStyle");
        EnvTable.Columns.Add(new DataGridTextColumn { Header = "Ключ", Binding = new Binding(nameof(DockerEnvRow.Key)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 200 });
        EnvTable.Columns.Add(new DataGridTextColumn { Header = "Значение", Binding = new Binding(nameof(DockerEnvRow.Value)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 180 });
        EnvTable.Columns.Add(new DataGridCheckBoxColumn { Header = "Секрет", Binding = new Binding(nameof(DockerEnvRow.Secret)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 70 });
        EnvTable.Columns.Add(new DataGridTextColumn { Header = "", Binding = new Binding(nameof(DockerEnvRow.Hint)), IsReadOnly = true, Width = 190 });
        EnvTable.ItemsSource = EnvRows; form.Children.Add(EnvTable);
        var add = Ui.Button("Добавить переменную", () => { var row = new DockerEnvRow(); EnvRows.Add(row); EnvTable.SelectedItem = row; EnvTable.ScrollIntoView(row); EnvTable.CurrentCell = new DataGridCellInfo(row, EnvTable.Columns[0]); EnvTable.BeginEdit(); });
        var remove = Ui.Button("Удалить выбранную", () => { if (EnvTable.SelectedItem is DockerEnvRow row) EnvRows.Remove(row); });
        form.Children.Add(Ui.Row(add, remove)); ((FrameworkElement)form.Children[^1]).Margin = new Thickness(0, 6, 0, 12);

        var lists = new Grid(); lists.ColumnDefinitions.Add(new ColumnDefinition()); lists.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); lists.ColumnDefinitions.Add(new ColumnDefinition());
        var portsBox = Labeled("Порты, по одному в строке", ports, "8080:80, 127.0.0.1:5432:5432, 53:53/udp"); var volumesBox = Labeled("Тома, по одному в строке", volumes, "/srv/data:/data или имя_тома:/data[:ro]"); Grid.SetColumn(volumesBox, 2);
        lists.Children.Add(portsBox); lists.Children.Add(volumesBox); form.Children.Add(lists);
        form.Children.Add(Labeled("Команда (вместо CMD образа)", command, "пусто — команда образа; слова разделяются пробелами, кавычки группируют"));
        errors.Name = "DockerErrors"; errors.TextWrapping = TextWrapping.Wrap; errors.SetResourceReference(TextBlock.ForegroundProperty, "ValidationInk"); errors.Margin = new Thickness(0, 4, 0, 0); form.Children.Add(errors);

        var cancel = Ui.Button("Отмена", () => DialogResult = false); cancel.IsCancel = true;
        var save = Ui.Button("Сохранить параметры", () => Accept(false)); save.Name = "DockerSave";
        var run = Ui.Button(exists ? "Сохранить и пересоздать" : "Сохранить и запустить", () => Accept(true)); run.Name = "DockerRun"; run.IsDefault = true; run.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        Content = Ui.DialogSurface(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, cancel, save, run);
    }

    static TextBox Field(string name) => new() { MinHeight = 32, Name = name };
    static TextBox Lines(string name) => new() { Name = name, AcceptsReturn = true, MinHeight = 76, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap };
    static FrameworkElement Labeled(string label, Control input, string? hint)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) }; panel.Children.Add(Ui.Text(label, 12)); input.Margin = new Thickness(0, 4, 0, 0); panel.Children.Add(input);
        System.Windows.Automation.AutomationProperties.SetName(input, label);
        if (hint is not null) { var h = Ui.Text(hint, 11); h.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); h.TextWrapping = TextWrapping.Wrap; panel.Children.Add(h); }
        return panel;
    }

    /// <summary>Builds the template from the form; secrets are encrypted here. Null with messages shown when invalid.</summary>
    internal DockerTemplate? Build(out IReadOnlyList<string> problems)
    {
        EnvTable.CommitEdit(DataGridEditingUnit.Row, true);
        static List<string> Split(string text) => text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var template = original.Clone();
        template.Image = image.Text.Trim(); template.ContainerName = name.Text.Trim(); template.Network = network.Text.Trim(); template.Command = command.Text.Trim();
        template.RestartPolicy = restart.SelectedItem as string ?? "unless-stopped"; template.Ports = Split(ports.Text); template.Volumes = Split(volumes.Text);
        var env = EnvRows.Where(r => r.Key.Trim().Length > 0 || r.Value.Length > 0).ToArray();
        template.Env = env.Select(r => new DockerEnv { Key = r.Key.Trim(), Value = r.Secret ? "" : r.Value, Secret = r.Secret, Protected = r.Secret && r.Value.Length == 0 ? r.StoredProtected : "" }).ToList();
        // Validate typed values (a kept secret counts as present); encrypt only after validation passes.
        var typed = env.Select((r, i) => (Env: template.Env[i], Text: r.Value)).ToDictionary(x => x.Env, x => x.Text);
        var list = DockerCommands.Validate(template, e => typed[e]).ToList();
        if (otherNames.Contains(template.ContainerName, StringComparer.Ordinal)) list.Add($"Контейнер с именем «{template.ContainerName}» уже есть на сервере.");
        foreach (var (e, text) in typed) if (e.Secret && text.Length == 0 && e.Protected.Length == 0) list.Add($"Введите значение секрета «{e.Key}».");
        problems = list;
        if (list.Count > 0) return null;
        foreach (var (e, text) in typed) if (e.Secret && text.Length > 0) DockerSecrets.SetSecret(e, text);
        return template;
    }

    void Accept(bool run)
    {
        var template = Build(out var problems);
        errors.Text = string.Join("\n", problems.Select(p => "• " + p));
        if (template is null) return;
        Accepted = new Result(template, run); DialogResult = true;
    }
}
