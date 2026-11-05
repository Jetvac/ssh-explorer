using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>«Защита SSH» → settings of the SSH server: findings with one-click fixes through the safe apply.</summary>
public sealed partial class SecurityView
{
    SshdService? sshd;
    SshdSnapshot? sshdSnapshot;
    IReadOnlyList<SshdFinding> findings = [];
    readonly StackPanel findingsPanel = new() { Name = "SshdFindings" };
    readonly TextBlock sshdState = Ui.Text("", 12);
    internal Func<SshdService>? SshdFactory { get; set; }
    internal IReadOnlyList<SshdFinding> Findings => findings;
    internal string SshdStateText => sshdState.Text;

    void AddHardeningCommands(Action<string, string, string, Func<Task>, string, int> command) =>
        command("FixAll", IconCatalog.Firewall, "Исправить важное…", FixAllAsync, "Применить все доступные исправления уровня «Важно» и «Желательно»", 80);

    void BuildHardening(StackPanel page)
    {
        page.Children.Add(Section("Настройки SSH-сервера"));
        sshdState.Name = "SshdState"; sshdState.TextWrapping = TextWrapping.Wrap; sshdState.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); page.Children.Add(sshdState);
        findingsPanel.Margin = new Thickness(0, 8, 0, 0); page.Children.Add(findingsPanel);
    }

    void UpdateHardeningCommands(bool ready) => commands["FixAll"].IsEnabled = ready && sshd is { CanChange: true } && FixableImportant().Any();

    IEnumerable<SshdFinding> FixableImportant() => findings.Where(f => f.Fix is not null && f.Blocked is null && f.Severity is Severity.High or Severity.Medium);

    async Task RefreshHardeningAsync()
    {
        try
        {
            sshd ??= SshdFactory?.Invoke() ?? new SshdService(keys.Session);
            await sshd.ProbeAsync(lifetime.Token);
            sshdSnapshot = await sshd.ReadAsync(lifetime.Token);
        }
        catch (Exception ex) when (ex is System.IO.IOException or Renci.SshNet.Common.SshException or InvalidOperationException) { sshdState.Text = "Не удалось прочитать настройки SSH: " + ErrorMessages.Describe(ex); findingsPanel.Children.Clear(); findings = []; return; }
        if (sshdSnapshot.Problem is { } problem) { sshdState.Text = problem; findingsPanel.Children.Clear(); findings = []; return; }
        findings = Sshd.Audit(sshdSnapshot, CurrentProfile.Authentication, rows.Count);
        sshdState.Text = (sshdSnapshot.Effective ? "Действующие настройки для этого пользователя (sshd -T)." : "Прочитано из файлов sshd_config — приблизительно: для точных значений нужны права root.")
            + (sshd.CanChange ? $" Изменения пишутся в {(sshdSnapshot.IncludesDropIns ? Sshd.DropIn : "начало /etc/ssh/sshd_config")}, проверяются новым подключением и откатываются при неудаче." : " Изменять настройки можно с правами root или sudo без пароля.")
            + (findings.All(f => f.Severity == Severity.Info) ? " Серьёзных замечаний нет." : "");
        findingsPanel.Children.Clear();
        foreach (var f in findings) findingsPanel.Children.Add(Card(f));
    }

    FrameworkElement Card(SshdFinding f)
    {
        var card = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 8), BorderThickness = new Thickness(1), Tag = f.Id, Name = "Finding_" + f.Id };
        card.SetResourceReference(Border.BackgroundProperty, "Surface"); card.SetResourceReference(Border.BorderBrushProperty, f.Severity == Severity.High ? "DangerText" : "Edge");
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel();
        var head = new WrapPanel();
        var badge = new Border { CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(0, 0, 8, 0), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
        badge.SetResourceReference(Border.BorderBrushProperty, f.Severity == Severity.High ? "DangerText" : "Edge");
        var badgeText = Ui.Text(f.SeverityText, 11); badgeText.SetResourceReference(TextBlock.ForegroundProperty, f.Severity == Severity.High ? "DangerText" : "Muted"); badge.Child = badgeText;
        head.Children.Add(badge); head.Children.Add(Ui.Text(f.Title, 13, true)); text.Children.Add(head);
        var detail = Ui.Text(f.Detail, 12); detail.TextWrapping = TextWrapping.Wrap; detail.Margin = new Thickness(0, 4, 0, 0); detail.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); text.Children.Add(detail);
        if (f.Fix is { } fix)
        {
            var what = Ui.Text("Исправление: " + string.Join(", ", fix.Select(p => p.Key + " " + p.Value)), 12); what.FontFamily = new FontFamily("Cascadia Mono, Consolas"); what.Margin = new Thickness(0, 4, 0, 0); what.TextWrapping = TextWrapping.Wrap; text.Children.Add(what);
            if (f.Blocked is { } why) { var b = Ui.Text(why, 12); b.TextWrapping = TextWrapping.Wrap; b.Margin = new Thickness(0, 4, 0, 0); b.SetResourceReference(TextBlock.ForegroundProperty, "DangerText"); text.Children.Add(b); }
        }
        grid.Children.Add(text);
        if (f.Fix is not null)
        {
            var button = Ui.AsyncButton("Исправить…", () => Run(() => FixAsync([f]))); button.Name = "Fix_" + f.Id; button.VerticalAlignment = VerticalAlignment.Top; button.Margin = new Thickness(12, 0, 0, 0);
            button.IsEnabled = f.Blocked is null && sshd is { CanChange: true }; Grid.SetColumn(button, 1); grid.Children.Add(button);
        }
        card.Child = grid; return card;
    }

    Task FixAllAsync() => FixAsync(FixableImportant().ToArray());

    async Task FixAsync(IReadOnlyList<SshdFinding> chosen)
    {
        if (sshd is null || sshdSnapshot is null || chosen.Count == 0) return;
        var changes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in chosen) foreach (var (k, v) in f.Fix!) changes[k] = v;
        var block = Sshd.Block(sshdSnapshot.Ours, changes);
        if (!Confirm($"Изменить настройки SSH на «{keys.Session.Profile.Name}»?\n\n{string.Join("\n", chosen.Select(f => "• " + f.Title))}\n\nБудет записано в {(sshdSnapshot.IncludesDropIns ? Sshd.DropIn : "начало /etc/ssh/sshd_config")}:\n{block}\nНастройки проверит sshd -t, сервер перечитает их без обрыва текущих сеансов. Затем приложение откроет новое SSH-подключение: если оно не пройдёт, прежние настройки вернутся сразу, а при потере связи — сами через {SshdService.RollbackSeconds} с.")) return;
        var result = await sshd.ApplyAsync(sshdSnapshot, changes, text => Dispatcher.Invoke(() => state.Text = text), lifetime.Token);
        await RefreshHardeningAsync(); UpdateCommands();
        state.Text = result.Message;
        if (result.Outcome != ApplyOutcome.Confirmed) Ui.Error(result.Message + (result.Output.Length > 0 ? "\n\nОтвет сервера:\n" + result.Output : ""));
    }
}
