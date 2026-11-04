using System.Collections.ObjectModel;
using System.Globalization;
using SSHExplorer.Core;

namespace SSHExplorer.App.ViewModels;

public sealed class TransferListViewModel
{
    public ObservableCollection<TransferRow> Rows { get; } = [];
    /// <summary>Updates rows in place: selection, sort and scroll position stay with the same objects.</summary>
    public void Refresh(IReadOnlyList<TransferJob> jobs)
    {
        var current = jobs.Select(j => j.Id).ToHashSet();
        for (int i = Rows.Count - 1; i >= 0; --i) if (!current.Contains(Rows[i].Job.Id)) Rows.RemoveAt(i);
        var known = Rows.ToDictionary(r => r.Job.Id);
        foreach (var job in jobs)
        {
            if (!known.TryGetValue(job.Id, out var row)) { row = new TransferRow(job); Rows.Add(row); }
            row.Refresh();
        }
    }
    public int Active => Rows.Count(r => r.Job.State is TransferState.Queued or TransferState.Running);
    public int Failed => Rows.Count(r => r.Job.State == TransferState.Failed);
}

public sealed class TransferRow(TransferJob job) : ObservableObject
{
    readonly Dictionary<string, string> values = [];
    public TransferJob Job => job;
    public string Name => job.Name;
    public string Source => job.Source;
    public string Destination => job.Destination;
    public string Direction => job.Kind switch
    {
        TransferKind.Upload => "Windows → " + job.DestinationServer,
        TransferKind.Download => job.SourceServer + " → Windows",
        TransferKind.ServerMove => job.SourceServer == job.DestinationServer ? "Перемещение · " + job.SourceServer : job.SourceServer + " → " + job.DestinationServer + " (перемещение)",
        _ => job.SourceServer == job.DestinationServer ? "Копирование · " + job.SourceServer : job.SourceServer + " → " + job.DestinationServer,
    };
    public string DirectionGlyph => job.Kind switch { TransferKind.Upload => Ui.UploadGlyph, TransferKind.Download => Ui.DownloadGlyph, _ => IconCatalog.Transfers };
    public string State => Value(nameof(State));
    public string Transferred => Value(nameof(Transferred));
    public string Total => Value(nameof(Total));
    public string Progress => Value(nameof(Progress));
    public string Speed => Value(nameof(Speed));
    public string Remaining => Value(nameof(Remaining));
    public string Result => Value(nameof(Result));
    public double ProgressValue => job.Progress ?? 0;
    public bool ProgressKnown => job.Progress is not null || job.State is not (TransferState.Running);
    public bool IsRunning => job.State == TransferState.Running;
    /// <summary>Typed sort keys: unknown stays null so it sorts after known values.</summary>
    public int StateRank => job.State switch { TransferState.Running => 0, TransferState.Queued => 1, TransferState.Failed => 2, TransferState.Cancelled => 3, _ => 4 };
    public double? ProgressKey => job.Progress;
    public double? SpeedKey => job.State == TransferState.Running && job.BytesPerSecond > 0 ? job.BytesPerSecond : null;
    public TimeSpan? RemainingKey => job.State == TransferState.Running ? job.Remaining : null;
    public long TransferredKey => job.BytesTransferred;
    public string StateKind => job.State.ToString();
    string Value(string name) => values.GetValueOrDefault(name, "—");
    void Update(string name, string value) { if (Value(name) != value) { values[name] = value; Raise(name); } }
    public void Refresh()
    {
        Update(nameof(State), job.State switch { TransferState.Queued => "В очереди", TransferState.Running => "Выполняется", TransferState.Completed => "Завершено", TransferState.Cancelled => "Отменено", _ => "Ошибка" });
        Update(nameof(Transferred), Ui.Size(job.BytesTransferred));
        Update(nameof(Total), job.TotalBytes is { } total ? Ui.Size(total) : job.State == TransferState.Running ? "Подсчёт…" : "—");
        Update(nameof(Progress), job.Progress is { } progress ? progress.ToString("0.0", CultureInfo.CurrentCulture) + " %" : job.State == TransferState.Running ? "Неизвестно" : "—");
        Update(nameof(Speed), SpeedKey is { } speed ? Ui.Size((long)speed) + "/с" : "—");
        // Unknown time is "—", never 0: completed jobs show their result instead.
        Update(nameof(Remaining), RemainingKey is { } remaining ? FormatDuration(remaining) : "—");
        Update(nameof(Result), job.Error ?? (job.State == TransferState.Completed ? job.Summary + " · " + FormatDuration(job.Elapsed) : job.State == TransferState.Cancelled ? "Отменено пользователем. " + job.Summary : job.Summary));
        // Derived and typed sort-key properties notify only on a real change: an unchanged refresh
        // must not churn bindings, live sorting or selection.
        var snapshot = (ProgressValue, ProgressKnown, IsRunning, StateKind, StateRank, ProgressKey, SpeedKey, RemainingKey, TransferredKey);
        if (derived is not { } previous) { derived = snapshot; return; }
        if (previous == snapshot) return;
        derived = snapshot;
        if (previous.ProgressValue != snapshot.ProgressValue) Raise(nameof(ProgressValue));
        if (previous.ProgressKnown != snapshot.ProgressKnown) Raise(nameof(ProgressKnown));
        if (previous.IsRunning != snapshot.IsRunning) Raise(nameof(IsRunning));
        if (previous.StateKind != snapshot.StateKind) Raise(nameof(StateKind));
        if (previous.StateRank != snapshot.StateRank) Raise(nameof(StateRank));
        if (previous.ProgressKey != snapshot.ProgressKey) Raise(nameof(ProgressKey));
        if (previous.SpeedKey != snapshot.SpeedKey) Raise(nameof(SpeedKey));
        if (previous.RemainingKey != snapshot.RemainingKey) Raise(nameof(RemainingKey));
        if (previous.TransferredKey != snapshot.TransferredKey) Raise(nameof(TransferredKey));
    }
    (double ProgressValue, bool ProgressKnown, bool IsRunning, string StateKind, int StateRank, double? ProgressKey, double? SpeedKey, TimeSpan? RemainingKey, long TransferredKey)? derived;
    public static string FormatDuration(TimeSpan value) => value.TotalHours >= 24 ? $"{(int)value.TotalDays} д {value.Hours} ч" : value.TotalHours >= 1 ? $"{(int)value.TotalHours} ч {value.Minutes:00} мин" : value.TotalMinutes >= 1 ? $"{value.Minutes} мин {value.Seconds:00} с" : value.TotalSeconds is > 0 and < 1 ? "< 1 с" : $"{Math.Max(0, value.Seconds)} с";
}
