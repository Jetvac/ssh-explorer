using SSHExplorer.Core;

namespace SSHExplorer.App.ViewModels;

public sealed class HomeViewModel : ObservableObject
{
    List<ServerProfile> profiles = [];
    (Guid ProfileId, SessionState State)[] sessions = [];
    string searchQuery = "", statusText = "";
    IReadOnlyList<ServerProfileViewModel> visibleProfiles = [];
    ServerProfileViewModel? selectedProfile;

    public string SearchQuery { get => searchQuery; set { if (Set(ref searchQuery, value)) Rebuild(); } }
    public string StatusText { get => statusText; private set => Set(ref statusText, value); }
    /// <summary>A one-off message (export or import result) until the next refresh.</summary>
    public void Announce(string text) => StatusText = text;
    public IReadOnlyList<ServerProfileViewModel> VisibleProfiles { get => visibleProfiles; private set => Set(ref visibleProfiles, value); }
    public ServerProfileViewModel? SelectedProfile { get => selectedProfile; set { if (Set(ref selectedProfile, value)) Raise(nameof(HasSelectedProfile)); } }
    public bool HasSelectedProfile => SelectedProfile is not null;
    public bool HasProfiles => profiles.Count > 0;
    /// <summary>Group headers appear only once some profile has a group; otherwise Home keeps its flat list.</summary>
    public bool HasGroups => profiles.Any(p => !string.IsNullOrWhiteSpace(p.Group));
    public bool ShowEmptyState => VisibleProfiles.Count == 0;
    public string EmptyStateTitle => HasProfiles ? "Ничего не найдено" : "Нет сохранённых серверов";
    public string EmptyStateMessage => HasProfiles ? "Попробуйте другое имя, адрес или пользователя." : "Добавьте SSH-сервер, чтобы открыть файлы, терминал и процессы.";
    public void Refresh(IEnumerable<ServerProfile> savedProfiles, IEnumerable<(Guid ProfileId, SessionState State)> activeSessions)
    {
        profiles = savedProfiles.ToList(); sessions = activeSessions.ToArray(); Rebuild();
    }
    void Rebuild()
    {
        var selectedId = SelectedProfile?.Profile.Id; var query = SearchQuery.Trim();
        VisibleProfiles = profiles.Where(p => query.Length == 0 || (p.Name + " " + p.Host + " " + p.Username + " " + p.Group).Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .Select(p => new ServerProfileViewModel(p, sessions.Where(s => s.ProfileId == p.Id).Select(s => s.State).ToArray())).ToList();
        SelectedProfile = VisibleProfiles.FirstOrDefault(p => p.Profile.Id == selectedId);
        Raise(nameof(HasProfiles)); Raise(nameof(HasGroups)); Raise(nameof(ShowEmptyState)); Raise(nameof(EmptyStateTitle)); Raise(nameof(EmptyStateMessage));
        StatusText = profiles.Count == 0 ? "Добавьте сервер, чтобы открыть файлы, терминал и диспетчер задач. Подключения идут напрямую по SSH."
            : $"Сохранено серверов: {profiles.Count}     Открыто сессий: {sessions.Length}";
    }
}

public sealed class ServerProfileViewModel(ServerProfile profile, SessionState[] states)
{
    public ServerProfile Profile => profile;
    public string Name => profile.Name;
    public string Endpoint => profile.Endpoint;
    public string Username => profile.IsRdp && profile.Rdp.Domain.Length > 0 ? profile.Rdp.Domain + "\\" + profile.Username : profile.Username;
    public string ProtocolGlyph => profile.IsRdp ? IconCatalog.RemoteDesktop : IconCatalog.Terminal;
    public string ProtocolName => profile.IsRdp ? "Удалённый рабочий стол (RDP)" : "SSH: файлы и терминал";
    readonly HomeMetrics.Summary? metrics = HomeMetrics.Get(profile.Id);
    /// <summary>CPU over the last 24 hours in half-hour averages (NaN — not recorded); null without history.</summary>
    public double[]? LoadHistory => metrics?.Cpu;
    public string LoadText => metrics?.Text ?? "";
    public string LoadToolTip => metrics is null ? "История не записывается" : "ЦП за последние 24 часа; справа — последние значения";
    /// <summary>Sort key of «Нагрузка»: current CPU, servers without data last.</summary>
    public double LoadRank => metrics?.CpuNow ?? -1;
    /// <summary>Null when ungrouped: sorts after named groups and shows as «Без группы».</summary>
    public string? GroupKey => string.IsNullOrWhiteSpace(profile.Group) ? null : profile.Group.Trim();
    public string GroupName => GroupKey ?? "Без группы";
    public string Status => states.Length == 0 ? "Не подключён" : states.Any(s => s == SessionState.Connected) ? $"Подключён · сессий: {states.Length}"
        : states.Any(s => s is SessionState.Connecting or SessionState.AwaitingTrust) ? "Подключение…" : "Связь потеряна";
    /// <summary>Sort key for the status column: active sessions first, then connecting, lost, idle.</summary>
    public int StatusRank => states.Length == 0 ? 3 : states.Any(s => s == SessionState.Connected) ? 0 : states.Any(s => s is SessionState.Connecting or SessionState.AwaitingTrust) ? 1 : 2;
}
