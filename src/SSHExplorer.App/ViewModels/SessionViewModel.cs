using SSHExplorer.Core;

namespace SSHExplorer.App.ViewModels;

public sealed class SessionViewModel : ObservableObject
{
    string currentSection = "files", statusText = "Подключение…";
    bool isActive, canReconnect;
    public string CurrentSection { get => currentSection; set { if (value is not ("files" or "terminal" or "monitor")) throw new ArgumentException("Неизвестный раздел сессии."); Set(ref currentSection, value); } }
    public bool IsActive { get => isActive; set => Set(ref isActive, value); }
    public bool CanReconnect { get => canReconnect; private set => Set(ref canReconnect, value); }
    public string StatusText { get => statusText; set => Set(ref statusText, value); }
    public void Update(ServerSession session)
    {
        StatusText = session.State switch
        {
            SessionState.Connecting => "Подключение…",
            SessionState.AwaitingTrust => "Ожидание решения о доверии ключу сервера…",
            SessionState.Connected => $"{session.Profile.Username}@{session.Profile.Endpoint}" + (!session.SftpAvailable ? " · SFTP недоступен" : "") + (!session.ShellAvailable ? " · Shell недоступен" : ""),
            SessionState.Disconnected => "Связь потеряна · данные устарели. " + session.Error,
            SessionState.Closing => "Закрытие…",
            _ => "Сессия закрыта"
        };
        CanReconnect = session.State is SessionState.Disconnected or SessionState.Closed;
    }
}
