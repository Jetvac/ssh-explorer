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
    public IReadOnlyList<ServerProfileViewModel> VisibleProfiles { get => visibleProfiles; private set => Set(ref visibleProfiles, value); }
    public ServerProfileViewModel? SelectedProfile { get => selectedProfile; set { if (Set(ref selectedProfile, value)) Raise(nameof(HasSelectedProfile)); } }
    public bool HasSelectedProfile => SelectedProfile is not null;
    public bool HasProfiles => profiles.Count > 0;
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
        VisibleProfiles = profiles.Where(p => query.Length == 0 || (p.Name + " " + p.Host + " " + p.Username).Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .Select(p => new ServerProfileViewModel(p, sessions.Where(s => s.ProfileId == p.Id).Select(s => s.State).ToArray())).ToList();
        SelectedProfile = VisibleProfiles.FirstOrDefault(p => p.Profile.Id == selectedId);
        Raise(nameof(HasProfiles)); Raise(nameof(ShowEmptyState)); Raise(nameof(EmptyStateTitle)); Raise(nameof(EmptyStateMessage));
        StatusText = profiles.Count == 0 ? "Добавьте сервер, чтобы открыть файлы, терминал и диспетчер задач. Подключения идут напрямую по SSH."
            : $"Сохранено серверов: {profiles.Count}     Открыто сессий: {sessions.Length}";
    }
}

public sealed class ServerProfileViewModel(ServerProfile profile, SessionState[] states)
{
    public ServerProfile Profile => profile;
    public string Name => profile.Name;
    public string Endpoint => profile.Endpoint;
    public string Username => profile.Username;
    public string Status => states.Length == 0 ? "Не подключён" : states.Any(s => s == SessionState.Connected) ? $"Подключён · сессий: {states.Length}"
        : states.Any(s => s is SessionState.Connecting or SessionState.AwaitingTrust) ? "Подключение…" : "Связь потеряна";
    /// <summary>Sort key for the status column: active sessions first, then connecting, lost, idle.</summary>
    public int StatusRank => states.Length == 0 ? 3 : states.Any(s => s == SessionState.Connected) ? 0 : states.Any(s => s is SessionState.Connecting or SessionState.AwaitingTrust) ? 1 : 2;
}
