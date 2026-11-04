using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using SSHExplorer.Core;

namespace SSHExplorer.App.ViewModels;

public sealed class ProfileViewModel:System.ComponentModel.INotifyPropertyChanged
{
    private const string ValidationSummary="Проверьте отмеченные поля. Введённые значения сохранены.";
    private readonly IProfilePersistence _persistence;
    private readonly Guid _id;
    private readonly List<string> _bookmarks;
    /// <summary>Jump host chain and Home group survive editing even before they have their own fields.</summary>
    string _proxyJump="",_group="";
    readonly List<DockerTemplate> _dockerTemplates;
    readonly List<SavedScript> _scripts;
    public string ProxyJump{get=>_proxyJump;set=>Set(ref _proxyJump,value);}
    public string Group{get=>_group;set=>Set(ref _group,value);}
    public string ProxyJumpError=>Error(nameof(ProxyJump));
    public string GroupError=>Error(nameof(Group));
    private readonly Dictionary<string,string> _errors=[];
    private string _profileName,_host,_port,_username,_keyPath,_initialPath,_timeout,_status="";
    private int _authenticationIndex;
    private bool _saveSecret,_isSaving,_hasStoredSecret;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public ProfileViewModel(IProfilePersistence persistence,ServerProfile? old=null)
    {
        _persistence=persistence;_id=old?.Id??Guid.NewGuid();_bookmarks=old?.Bookmarks.ToList()??[];ProxyJump=old?.ProxyJump??"";Group=old?.Group??"";_dockerTemplates=old?.DockerTemplates.Select(t=>t.Clone()).ToList()??[];_scripts=old?.Scripts.Select(s=>s.Clone()).ToList()??[];
        _profileName=old?.Name??"";_host=old?.Host??"";_port=(old?.Port??22).ToString(CultureInfo.InvariantCulture);
        _username=old?.Username??"";_keyPath=old?.PrivateKeyPath??"";_initialPath=old?.InitialPath??"~";
        _timeout=(old?.TimeoutSeconds??20).ToString(CultureInfo.InvariantCulture);_authenticationIndex=(int)(old?.Authentication??AuthenticationKind.Password);_saveSecret=old?.SaveSecret??false;
    }
    void Changed([System.Runtime.CompilerServices.CallerMemberName]string? property=null)=>PropertyChanged?.Invoke(this,new(property));
    void Set<T>(ref T field,T value,[System.Runtime.CompilerServices.CallerMemberName]string? property=null)
    {
        if(EqualityComparer<T>.Default.Equals(field,value))return;field=value;Changed(property);
        if(property is not nameof(Status) and not nameof(IsSaving))ClearError(property!);
    }
    public string ProfileName{get=>_profileName;set=>Set(ref _profileName,value);}
    public string Host{get=>_host;set=>Set(ref _host,value);}
    public string Port{get=>_port;set=>Set(ref _port,value);}
    public string Username{get=>_username;set=>Set(ref _username,value);}
    public string PrivateKeyPath{get=>_keyPath;set=>Set(ref _keyPath,value);}
    public string InitialPath{get=>_initialPath;set=>Set(ref _initialPath,value);}
    public string Timeout{get=>_timeout;set=>Set(ref _timeout,value);}
    public int AuthenticationIndex
    {
        get=>_authenticationIndex;
        set{if(_authenticationIndex!=value)_hasStoredSecret=false;Set(ref _authenticationIndex,value);Changed(nameof(IsKeyAuthentication));Changed(nameof(UsesSecret));Changed(nameof(SecretLabel));Changed(nameof(SecretExplanation));ClearError(nameof(PrivateKeyPath));ClearError("Secret");}
    }
    public bool SaveSecret{get=>_saveSecret;set{Set(ref _saveSecret,value);Changed(nameof(SecretExplanation));ClearError("Secret");}}
    public bool IsKeyAuthentication=>AuthenticationIndex==(int)AuthenticationKind.PrivateKey;
    /// <summary>An SSH agent signs for the user: there is no password or passphrase to enter or store.</summary>
    public bool UsesSecret=>AuthenticationIndex!=(int)AuthenticationKind.Agent;
    public string SecretLabel=>IsKeyAuthentication?"Парольная фраза ключа (если задана)":AuthenticationIndex==2?"Пароль для первоначального запроса":"Пароль";
    public string SecretExplanation=>(IsKeyAuthentication?"Для незашифрованного ключа парольная фраза не нужна. ":AuthenticationIndex==2?"Дополнительные запросы и одноразовые коды вводятся отдельно и не сохраняются. ":"Пустое поле можно заполнить при подключении. ")+
        (SaveSecret?"«Сохранить» записывает введённый секрет без проверки. «Сохранить и подключиться» обновит его только после успешного входа.":"«Сохранить» записывает только профиль; пароль потребуется при будущем входе. «Сохранить и подключиться» использует введённое значение в этой попытке.")+
        (_hasStoredSecret?" Пустое поле при отдельном сохранении оставит прежний защищённый секрет.":"");
    public bool IsSaving{get=>_isSaving;private set{Set(ref _isSaving,value);Changed(nameof(CanEdit));}}
    public bool CanEdit=>!IsSaving;
    public string Status{get=>_status;private set=>Set(ref _status,value);}
    public ServerProfile? SavedProfile{get;private set;}
    public string? CleanupWarning{get;private set;}
    public string NameError=>Error(nameof(ProfileName));public string HostError=>Error(nameof(Host));public string PortError=>Error(nameof(Port));
    public string UserError=>Error(nameof(Username));public string KeyError=>Error(nameof(PrivateKeyPath));public string InitialPathError=>Error(nameof(InitialPath));
    public string TimeoutError=>Error(nameof(Timeout));public string SecretError=>Error("Secret");
    public string? FirstInvalidField=>_errors.Keys.FirstOrDefault();
    string Error(string name)=>_errors.GetValueOrDefault(name,"");
    void ClearError(string name)
    {
        if(!_errors.Remove(name))return;
        RaiseErrors();
        if(_errors.Count==0&&Status==ValidationSummary)Status="";
    }
    void RaiseErrors(){foreach(var name in new[]{nameof(NameError),nameof(HostError),nameof(PortError),nameof(UserError),nameof(KeyError),nameof(InitialPathError),nameof(ProxyJumpError),nameof(TimeoutError),nameof(SecretError)})Changed(name);}
    public void SecretChanged()=>ClearError("Secret");
    public bool Validate(string secret)
        =>Validate(secret,deferSecretUntilAuthentication:false);
    private bool Validate(string secret,bool deferSecretUntilAuthentication)
    {
        _errors.Clear();
        if(string.IsNullOrWhiteSpace(ProfileName))_errors[nameof(ProfileName)]="Введите название сервера.";
        if(string.IsNullOrWhiteSpace(Host))_errors[nameof(Host)]="Введите адрес сервера.";
        else if(!TryNormalizeHost(Host,out _))_errors[nameof(Host)]="Укажите DNS-имя, IPv4 или IPv6 без схемы, порта и пути.";
        if(!int.TryParse(Port.Trim(),NumberStyles.None,CultureInfo.InvariantCulture,out int port)||port is <1 or >65535)_errors[nameof(Port)]="Порт: число от 1 до 65535.";
        if(string.IsNullOrWhiteSpace(Username))_errors[nameof(Username)]="Введите имя пользователя Linux.";
        else if(Username.Any(char.IsControl))_errors[nameof(Username)]="Имя пользователя содержит управляющие символы.";
        if(AuthenticationIndex is <0 or >3)_errors["Authentication"]="Выберите способ входа.";
        if(IsKeyAuthentication&&(string.IsNullOrWhiteSpace(PrivateKeyPath)||!File.Exists(PrivateKeyPath.Trim())))_errors[nameof(PrivateKeyPath)]="Выберите существующий локальный файл приватного ключа.";
        if(!int.TryParse(Timeout.Trim(),NumberStyles.None,CultureInfo.InvariantCulture,out int timeout)||timeout is <3 or >120)_errors[nameof(Timeout)]="Таймаут: число от 3 до 120 секунд.";
        try{_ = PosixPath.Normalize(string.IsNullOrWhiteSpace(InitialPath)?"~":InitialPath.Trim());}
        catch(ArgumentException){_errors[nameof(InitialPath)]="Используйте абсолютный POSIX-путь или ~ домашней папки.";}
        try{_ = ProxyJumpSpec.Parse(ProxyJump);}
        catch(ArgumentException ex){_errors[nameof(ProxyJump)]=ex.Message;}
        if(ProxyJump.Trim().Length>0&&(AuthenticationKind)AuthenticationIndex is AuthenticationKind.Password or AuthenticationKind.KeyboardInteractive&&ProxyJumpSpec.Parse(ProxyJump.Trim()) is {Count:>0} hops&&hops[^1] is {User:not null} or {Port:not null})
            _errors[nameof(ProxyJump)]="Для входа по паролю последний промежуточный узел укажите именем его профиля: пароль этого сервера не передаётся другому узлу.";
        if(SaveSecret&&UsesSecret&&!IsKeyAuthentication&&string.IsNullOrEmpty(secret)&&!_hasStoredSecret&&!deferSecretUntilAuthentication)_errors["Secret"]="Введите пароль для сохранения или отключите сохранение секрета.";
        RaiseErrors();Status=_errors.Count==0?"":ValidationSummary;return _errors.Count==0;
    }
    public async Task<string?> LoadSecretAsync(CancellationToken ct=default)
    {
        if(!SaveSecret)return null;
        int authentication=AuthenticationIndex;
        try{var secret=await _persistence.ReadSecretAsync(_id,ct);if(AuthenticationIndex!=authentication)return null;_hasStoredSecret=secret is not null;Changed(nameof(SecretExplanation));return secret;}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or CryptographicException or FormatException)
        {Status="Не удалось прочитать сохранённый секрет. Введите новый пароль или отключите его сохранение.";_ = LocalDiagnostics.RecordAsync("profile.secret.read.failed",ex);return null;}
    }
    public async Task<bool> SaveAsync(string secret,CancellationToken ct=default)
        =>await SaveCoreAsync(secret,false,ct);
    public async Task<ProfileSubmission?> SubmitAsync(string secret,bool connectNow,bool secretWasProvided=true,CancellationToken ct=default)
    {
        if(!await SaveCoreAsync(secret,connectNow,ct))return null;
        return new ProfileSubmission(SavedProfile!,connectNow,connectNow&&(secretWasProvided||IsKeyAuthentication)?new SessionCredentials(secret):null){CleanupWarning=CleanupWarning};
    }
    private async Task<bool> SaveCoreAsync(string secret,bool deferSecretUntilAuthentication,CancellationToken ct)
    {
        if(IsSaving||!Validate(secret,deferSecretUntilAuthentication))return false;
        IsSaving=true;Status="Сохранение…";
        try
        {
            TryNormalizeHost(Host,out string host);
            var profile=new ServerProfile{Id=_id,Name=ProfileName.Trim(),Host=host,Port=int.Parse(Port.Trim(),CultureInfo.InvariantCulture),Username=Username.Trim(),Authentication=(AuthenticationKind)AuthenticationIndex,PrivateKeyPath=IsKeyAuthentication?PrivateKeyPath.Trim():"",InitialPath=string.IsNullOrWhiteSpace(InitialPath)?"~":InitialPath.Trim(),TimeoutSeconds=int.Parse(Timeout.Trim(),CultureInfo.InvariantCulture),SaveSecret=SaveSecret&&UsesSecret,Bookmarks=_bookmarks.ToList(),ProxyJump=ProxyJump.Trim(),Group=Group.Trim(),DockerTemplates=_dockerTemplates.Select(t=>t.Clone()).ToList(),Scripts=_scripts.Select(s=>s.Clone()).ToList()};
            var result=deferSecretUntilAuthentication
                ?await _persistence.SaveForConnectionAsync(profile,ct)
                :await _persistence.SaveAsync(profile,SaveSecret&&!(secret.Length==0&&_hasStoredSecret)?secret:null,ct);
            SavedProfile=result.Profile;CleanupWarning=result.CleanupWarning;Status=CleanupWarning??"Профиль сохранён.";return true;
        }
        catch(OperationCanceledException){Status="Сохранение отменено. Ввод сохранён.";return false;}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException or InvalidOperationException or NotSupportedException)
        {Status="Не удалось сохранить профиль и защищённый секрет. Проверьте доступ к папке настроек. Введённые значения сохранены.";_ = LocalDiagnostics.RecordAsync("profile.save.failed",ex);return false;}
        finally{IsSaving=false;}
    }
    public static bool TryNormalizeHost(string value,out string host)
    {
        host=value.Trim();if(host.StartsWith('[')&&host.EndsWith(']'))host=host[1..^1];
        if(host.Length==0||host.Any(char.IsWhiteSpace)||host.Any(char.IsControl)||host.IndexOfAny(['/','\\','@','[',']'])>=0)return false;
        if(host.Contains(':'))return IPAddress.TryParse(host,out var ipv6)&&ipv6.AddressFamily==AddressFamily.InterNetworkV6;
        if(IPAddress.TryParse(host,out var address))return address.AddressFamily==AddressFamily.InterNetwork;
        if(host.Contains('.')&&host.All(c=>char.IsAsciiDigit(c)||c=='.'))return false;
        try
        {
            string ascii=new IdnMapping().GetAscii(host.TrimEnd('.'));
            return ascii.Length is >0 and <=253&&ascii.Split('.').All(label=>label.Length is >0 and <=63&&char.IsAsciiLetterOrDigit(label[0])&&char.IsAsciiLetterOrDigit(label[^1])&&label.All(c=>char.IsAsciiLetterOrDigit(c)||c=='-'));
        }
        catch(ArgumentException){return false;}
    }
}
