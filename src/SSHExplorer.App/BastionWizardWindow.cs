using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>
/// «Доступ только через сервер»: SSH to this server stays open only for the jump server (as this server sees it) and
/// optional extra addresses; everything else is closed for SSH. The plan is shown in full before anything changes:
/// rules added, rules removed (each can be kept), services kept open if ufw has to be turned on, the exact commands
/// and the predicted access for the jump server and for any other address.
/// </summary>
public sealed class BastionWizardWindow : Window
{
    readonly FirewallSnapshot snapshot;
    readonly int sshPort;
    readonly string bastionName;
    readonly TextBox extra = new() { Name = "BastionExtra", MinHeight = 32 };
    readonly StackPanel removals = new(), keeps = new();
    readonly TextBlock preview = Ui.Text("", 12), verdicts = Ui.Text("", 12), problem = Ui.Text("", 12);
    readonly CheckBox understood = new() { Name = "BastionUnderstood", Margin = new Thickness(0, 12, 0, 0), Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Я понимаю: если промежуточный сервер станет недоступен или сменит адрес, войти на этот сервер по SSH можно будет только через консоль хостинга." } };
    readonly Button apply;
    readonly bool viaJump;
    public IReadOnlyList<string> Commands { get; private set; } = [];
    internal string Preview => preview.Text;
    internal string Verdicts => verdicts.Text;
    internal string? Problem => problem.Text.Length > 0 ? problem.Text : null;

    public BastionWizardWindow(FirewallSnapshot snapshot, string? route, string proxyJump, IReadOnlyList<ListeningPort> listening)
    {
        this.snapshot = snapshot; sshPort = snapshot.SshPort ?? 22; viaJump = route is not null;
        bastionName = route?.Split('→').Select(p => p.Trim()).LastOrDefault() ?? proxyJump;
        Title = "Доступ только через сервер"; Width = 720; SizeToContent = SizeToContent.Height; MaxHeight = Math.Max(480, SystemParameters.WorkArea.Height - 60); ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "DialogWindowStyle");
        var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(Ui.DialogHeading("SSH только через промежуточный сервер"));
        void Para(string text, bool muted = true) { var t = Ui.Text(text, 12); t.TextWrapping = TextWrapping.Wrap; t.Margin = new Thickness(0, 0, 0, 8); if (muted) t.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); body.Children.Add(t); }
        if (!viaJump)
            Para("Сейчас вы подключены к этому серверу напрямую. Сначала в профиле сервера укажите «Подключаться через» — промежуточный сервер — и переподключитесь. Тогда сервер увидит адрес промежуточного узла, а проверка после изменения пройдёт по новому маршруту. Иначе закрытие прямого доступа отрезало бы это подключение.", false);
        else
        {
            Para($"Подключение идёт через «{route}». Этот сервер видит его с адреса {snapshot.ClientAddress} — только с него (и с адресов ниже) останется доступ по SSH на порт {sshPort}.", false);
            var extraLabel = Ui.Text("Дополнительно разрешить SSH с адресов (через запятую, необязательно) — например, резервный промежуточный сервер или офис:", 12); extraLabel.TextWrapping = TextWrapping.Wrap; body.Children.Add(extraLabel);
            extra.Margin = new Thickness(0, 4, 0, 10); body.Children.Add(extra);
            System.Windows.Automation.AutomationProperties.SetName(extra, "Дополнительные адреса");
            var open = Ufw.OpenSshRules(snapshot.Rules, sshPort, Allowed());
            if (open.Count > 0)
            {
                var t = Ui.Text("Эти правила открывают SSH другим адресам и будут удалены (снимите флажок, чтобы оставить):", 12); t.TextWrapping = TextWrapping.Wrap; body.Children.Add(t);
                foreach (var r in open) removals.Children.Add(new CheckBox { Content = r.Spec, Tag = r, IsChecked = true, Margin = new Thickness(0, 3, 0, 3), FontFamily = new FontFamily("Cascadia Mono, Consolas") });
                removals.Margin = new Thickness(0, 4, 0, 10); body.Children.Add(removals);
            }
            if (!snapshot.Active)
            {
                var services = listening.Where(p => !p.Address.StartsWith("127.", StringComparison.Ordinal) && p.Address != "::1" && p.Port != sshPort && p.Protocol is "tcp" or "udp" or "tcp6" or "udp6")
                    .Select(p => (p.Port, Proto: p.Protocol.StartsWith("udp", StringComparison.Ordinal) ? "udp" : "tcp", Who: p.Process)).DistinctBy(p => (p.Port, p.Proto)).OrderBy(p => p.Port).ToArray();
                var t = Ui.Text("ufw сейчас выключен и будет включён. Он закроет все входящие порты, кроме разрешённых. Сейчас на сервере слушают порты ниже; отмеченные останутся открытыми для всех:", 12); t.TextWrapping = TextWrapping.Wrap; body.Children.Add(t);
                foreach (var s in services) keeps.Children.Add(new CheckBox { Content = $"{s.Port}/{s.Proto}" + (s.Who is null ? "" : " — " + s.Who), Tag = (s.Port, s.Proto), IsChecked = true, Margin = new Thickness(0, 3, 0, 3) });
                if (services.Length == 0) keeps.Children.Add(Ui.Text("Других слушающих портов не найдено (или их процессы не видны без root).", 12));
                keeps.Margin = new Thickness(0, 4, 0, 10); body.Children.Add(keeps);
            }
        }
        var risks = new Expander { Header = "Что важно знать", Margin = new Thickness(0, 4, 0, 0) };
        var riskText = Ui.Text(
            "• Работает для сервера, где включён ufw. Файервол облака или роутера настраивается отдельно, приложение его не видит.\n" +
            "• Если у промежуточного сервера сменится внешний адрес (динамический IP, пересоздание), доступ пропадёт. Добавьте второй адрес выше на этот случай.\n" +
            "• Перед применением сохранится текущее состояние ufw, а изменение проверит новое SSH-подключение через промежуточный сервер. Если оно не пройдёт, всё вернётся сразу, а при потере связи — само через " + FirewallService.RollbackSeconds + " с.\n" +
            "• Другие порты (сайты, базы данных) правило не трогает. Docker публикует порты контейнеров в обход ufw.\n" +
            "• В профилях серверов, которые подключались к этому напрямую, укажите тот же промежуточный сервер.", 12);
        riskText.TextWrapping = TextWrapping.Wrap; risks.Content = riskText; body.Children.Add(risks);
        body.Children.Add(understood);
        preview.Name = "BastionPreview"; preview.FontFamily = new FontFamily("Cascadia Mono, Consolas"); preview.TextWrapping = TextWrapping.Wrap; preview.Margin = new Thickness(0, 12, 0, 0); body.Children.Add(preview);
        verdicts.Name = "BastionVerdicts"; verdicts.TextWrapping = TextWrapping.Wrap; verdicts.Margin = new Thickness(0, 8, 0, 0); body.Children.Add(verdicts);
        problem.Name = "BastionProblem"; problem.TextWrapping = TextWrapping.Wrap; problem.Margin = new Thickness(0, 6, 0, 0); problem.SetResourceReference(TextBlock.ForegroundProperty, "DangerText"); body.Children.Add(problem);
        var cancel = Ui.Button("Отмена", () => DialogResult = false); cancel.IsCancel = true;
        apply = Ui.Button("Применить с проверкой…", () => { if (apply!.IsEnabled) DialogResult = true; }); apply.Name = "BastionApply"; apply.IsDefault = true; apply.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        Content = Ui.DialogSurface(body, cancel, apply);
        extra.TextChanged += (_, _) => Update(); understood.Click += (_, _) => Update();
        foreach (var c in removals.Children.OfType<CheckBox>().Concat(keeps.Children.OfType<CheckBox>())) c.Click += (_, _) => Update();
        Update();
    }

    List<string> Allowed()
    {
        var list = new List<string>(); if (snapshot.ClientAddress is { } client) list.Add(client);
        foreach (var a in extra.Text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) if (!list.Contains(a)) list.Add(a);
        return list;
    }

    internal void Update()
    {
        string? error = !viaJump ? "Сначала подключитесь к этому серверу через промежуточный сервер." : snapshot.ClientAddress is null ? "Сервер не сообщил адрес подключения (SSH_CLIENT): план составить нельзя." : null;
        var allowed = Allowed();
        if (error is null) foreach (var a in allowed) if (a == "any" || !Ufw.IsAddress(a)) { error = "Адрес «" + a + "» не распознан. Укажите IP-адрес или подсеть."; break; }
        if (error is null && understood.IsChecked != true) error = "Подтвердите флажком, что понимаете последствия.";
        Commands = [];
        if (viaJump && snapshot.ClientAddress is not null && (error is null || error.StartsWith("Подтвердите", StringComparison.Ordinal)))
        {
            var remove = removals.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (UfwRule)c.Tag).ToArray();
            var keep = keeps.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => ((int, string))c.Tag).ToArray();
            var planned = Ufw.BastionCommands(snapshot, allowed, sshPort, remove, keep, bastionName);
            preview.Text = string.Join("\n", planned);
            var jump = Ufw.CheckAfter(snapshot, planned);
            // Any address outside the allowed list stands for «everyone else».
            var other = Ufw.CheckAfter(snapshot, planned, snapshot.ClientAddress.Contains(':') ? "2001:db8::77" : "203.0.113.77");
            verdicts.Text = $"Промежуточный сервер ({snapshot.ClientAddress}): {jump.Reason}\nЛюбой другой адрес: {other.Reason}";
            if (error is null && jump.Allowed != true) error = "По расчёту промежуточный сервер не получит доступ — применять нельзя. " + jump.Reason;
            if (error is null && other.Allowed != false) error = "По расчёту SSH останется открыт для других адресов. " + other.Reason;
            if (error is null) Commands = planned;
        }
        else { preview.Text = ""; verdicts.Text = ""; }
        problem.Text = error ?? ""; problem.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        apply.IsEnabled = error is null;
    }
}
