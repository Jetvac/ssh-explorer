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
    public ServerProfileViewModel? SelectedProfile { get => selectedProfile; set => Set(ref selectedProfile, value); }
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
}
