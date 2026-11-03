using System.Collections.ObjectModel;
using SSHExplorer.Core;

namespace SSHExplorer.App.ViewModels;

public sealed class TransferListViewModel
{
    public ObservableCollection<TransferRow> Rows { get; } = [];
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
}

public sealed class TransferRow(TransferJob job) : ObservableObject
{
    readonly Dictionary<string, string> values = [];
    public TransferJob Job => job;
    public string Name => job.Name;
    public string Source => job.Source;
    public string Destination => job.Destination;
    public string State => Value(nameof(State));
    public string Transferred => Value(nameof(Transferred));
    public string Total => Value(nameof(Total));
    public string Progress => Value(nameof(Progress));
    public string Speed => Value(nameof(Speed));
    public string Remaining => Value(nameof(Remaining));
    public string Result => Value(nameof(Result));
    string Value(string name) => values.GetValueOrDefault(name, "—");
    void Update(string name, string value) { if (Value(name) != value) { values[name] = value; Raise(name); } }
    public void Refresh()
    {
        Update(nameof(State), job.State switch { TransferState.Queued => "В очереди", TransferState.Running => "Передача", TransferState.Completed => "Завершено", TransferState.Cancelled => "Отменено", _ => "Ошибка" });
        Update(nameof(Transferred), Ui.Size(job.BytesTransferred));
        Update(nameof(Total), job.TotalBytes is { } total ? Ui.Size(total) : "Подсчёт…");
        Update(nameof(Progress), job.Progress is { } progress ? $"{progress:0.0}%" : "Неизвестно");
        Update(nameof(Speed), job.BytesPerSecond > 0 ? Ui.Size((long)job.BytesPerSecond) + "/с" : "—");
        Update(nameof(Remaining), job.Remaining is { } remaining ? remaining.ToString(@"hh\:mm\:ss") : "—");
        Update(nameof(Result), job.Error ?? job.Summary);
    }
}
