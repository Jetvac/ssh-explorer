using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>Name, commands, how the script runs (manual, on connect, every N minutes, daily) and its limits.</summary>
public sealed class ScriptEditorWindow : Window
{
    readonly SavedScript original;
    readonly TextBox name = new() { MinHeight = 32, Name = "ScriptName" };
    readonly TextBox body = new() { Name = "ScriptBody", AcceptsReturn = true, AcceptsTab = true, MinHeight = 220, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top };
    readonly CheckBox stopOnError = new() { Content = "Остановиться на первой ошибке (set -e)", Name = "ScriptStopOnError" };
    readonly ComboBox trigger = new() { ItemsSource = new[] { "Вручную", "При подключении к серверу", "Каждые N минут", "Ежедневно в указанное время" }, MinHeight = 32, Name = "ScriptTrigger" };
    readonly TextBox interval = new() { MinHeight = 32, Width = 120, Name = "ScriptInterval", HorizontalAlignment = HorizontalAlignment.Left };
    readonly TextBox daily = new() { MinHeight = 32, Width = 120, Name = "ScriptDaily", HorizontalAlignment = HorizontalAlignment.Left };
    readonly CheckBox enabled = new() { Content = "Автоматический запуск включён", Name = "ScriptEnabled" };
    readonly TextBox timeout = new() { MinHeight = 32, Width = 120, Name = "ScriptTimeout", HorizontalAlignment = HorizontalAlignment.Left };
    readonly TextBlock errors = Ui.Text("", 12);
    public SavedScript? Accepted { get; private set; }

    public ScriptEditorWindow(SavedScript script)
    {
        original = script;
        Title = "Скрипт — " + script.Name; Width = 720; Height = 720; MinWidth = 560; MinHeight = 520; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; Style = (Style)FindResource(typeof(Window));
        name.Text = script.Name; body.Text = script.Body.Replace("\n", Environment.NewLine); stopOnError.IsChecked = script.StopOnError; trigger.SelectedIndex = (int)script.Trigger;
        interval.Text = script.IntervalMinutes.ToString(CultureInfo.InvariantCulture); daily.Text = script.DailyTime; enabled.IsChecked = script.Enabled; timeout.Text = script.TimeoutMinutes.ToString(CultureInfo.InvariantCulture);
        var form = new StackPanel { Margin = new Thickness(24, 20, 24, 12) };
        form.Children.Add(Ui.DialogHeading("Скрипт"));
        form.Children.Add(Labeled("Название", name, null));
        form.Children.Add(Labeled("Команды, по одной в строке", body, "Выполняются по порядку через sh. Первая строка вида #!/usr/bin/env bash выбирает другой интерпретатор. Текст передаётся на сервер файлом с правами 700, а не командной строкой."));
        form.Children.Add(stopOnError); ((FrameworkElement)form.Children[^1]).Margin = new Thickness(0, 0, 0, 12);
        form.Children.Add(Labeled("Запуск", trigger, null));
        var intervalBox = Labeled("Интервал, минут", interval, null); var dailyBox = Labeled("Время, ЧЧ:ММ (местное)", daily, null);
        form.Children.Add(intervalBox); form.Children.Add(dailyBox); form.Children.Add(enabled);
        ((FrameworkElement)form.Children[^1]).Margin = new Thickness(0, 0, 0, 12);
        void UpdateTrigger()
        {
            intervalBox.Visibility = trigger.SelectedIndex == (int)ScriptTrigger.Interval ? Visibility.Visible : Visibility.Collapsed;
            dailyBox.Visibility = trigger.SelectedIndex == (int)ScriptTrigger.Daily ? Visibility.Visible : Visibility.Collapsed;
            enabled.Visibility = trigger.SelectedIndex == (int)ScriptTrigger.Manual ? Visibility.Collapsed : Visibility.Visible;
        }
        trigger.SelectionChanged += (_, _) => UpdateTrigger(); UpdateTrigger();
        var autoNote = Ui.Text("Автоматический запуск работает, пока приложение открыто и сервер подключён. Пропущенный ежедневный запуск выполнится при следующем подключении в тот же день.", 12);
        autoNote.TextWrapping = TextWrapping.Wrap; autoNote.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); autoNote.Margin = new Thickness(0, 0, 0, 12); form.Children.Add(autoNote);
        form.Children.Add(Labeled("Таймаут, минут", timeout, "Ожидание прерывается по истечении; процесс на сервере может продолжить работу."));
        errors.Name = "ScriptErrors"; errors.TextWrapping = TextWrapping.Wrap; errors.SetResourceReference(TextBlock.ForegroundProperty, "ValidationInk"); form.Children.Add(errors);
        var cancel = Ui.Button("Отмена", () => DialogResult = false); cancel.IsCancel = true;
        var save = Ui.Button("Сохранить", Accept); save.Name = "ScriptSave"; save.IsDefault = false; save.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        Content = Ui.DialogSurface(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, cancel, save);
        Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
    }

    static FrameworkElement Labeled(string label, Control input, string? hint)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) }; panel.Children.Add(Ui.Text(label, 12)); input.Margin = new Thickness(0, 4, 0, 0); panel.Children.Add(input);
        System.Windows.Automation.AutomationProperties.SetName(input, label);
        if (hint is not null) { var h = Ui.Text(hint, 11); h.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); h.TextWrapping = TextWrapping.Wrap; panel.Children.Add(h); }
        return panel;
    }

    internal SavedScript? Build(out IReadOnlyList<string> problems)
    {
        var s = original.Clone();
        s.Name = name.Text.Trim(); s.Body = body.Text.Replace("\r\n", "\n"); s.StopOnError = stopOnError.IsChecked == true;
        s.Trigger = (ScriptTrigger)Math.Max(0, trigger.SelectedIndex); s.Enabled = enabled.IsChecked == true; s.DailyTime = daily.Text.Trim();
        var list = new List<string>();
        if (int.TryParse(interval.Text.Trim(), out var minutes)) s.IntervalMinutes = minutes; else if (s.Trigger == ScriptTrigger.Interval) list.Add("Интервал — целое число минут.");
        if (int.TryParse(timeout.Text.Trim(), out var limit)) s.TimeoutMinutes = limit; else list.Add("Таймаут — целое число минут.");
        if (s.Trigger == ScriptTrigger.Daily && ScriptRules.TryDaily(s.DailyTime, out var at)) s.DailyTime = $"{at.Hours:00}:{at.Minutes:00}";
        list.AddRange(ScriptRules.Validate(s));
        problems = list.Distinct().ToArray();
        return problems.Count == 0 ? s : null;
    }

    void Accept()
    {
        var s = Build(out var problems);
        errors.Text = string.Join("\n", problems.Select(p => "• " + p));
        if (s is null) return;
        Accepted = s; DialogResult = true;
    }
}
