using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using SSHExplorer.App.ViewModels;
using SSHExplorer.Core;

namespace SSHExplorer.App;

/// <summary>A compact, owner-bound form. Expected validation/persistence errors stay inline.</summary>
public sealed class ProfileDialog:Window,ICancellableModal
{
    private readonly ProfileViewModel _model;
    private readonly SecretEntry _secret=new("Пароль или парольная фраза ключа");
    private readonly Dictionary<string,Control> _fields=[];
    private readonly CancellationTokenSource _lifetime=new();
    private readonly Expander _advanced=new();
    private readonly Button _save;
    private readonly bool _allowConnect;
    private bool _cancelRequested,_loadedSavedSecret;
    public ProfileViewModel Model=>_model;
    public ServerProfile? SavedProfile=>_model.SavedProfile;
    public ProfileSubmission? Submission{get;private set;}
    public ProfileDialog(IProfilePersistence persistence,ServerProfile? old=null,Window? owner=null,bool allowConnect=false)
    {
        _allowConnect=allowConnect;
        _model=new(persistence,old);DataContext=_model;
        SetResourceReference(StyleProperty,"DialogWindowStyle");
        Title=old is null?"Добавить сервер":"Изменить сервер";Width=540;MinWidth=430;
        MaxHeight=Math.Max(320,SystemParameters.WorkArea.Height-48);SizeToContent=SizeToContent.Height;
        ResizeMode=ResizeMode.CanResize;ShowInTaskbar=false;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        if(owner is not null&&(owner.IsLoaded||new System.Windows.Interop.WindowInteropHelper(owner).Handle!=IntPtr.Zero)){Owner=owner;WindowStartupLocation=WindowStartupLocation.CenterOwner;}
        var root=new DockPanel();root.SetResourceReference(Panel.BackgroundProperty,"Paper");
        var footer=new StackPanel();var footerSurface=new Border{Child=footer};footerSurface.SetResourceReference(StyleProperty,"DialogFooterStyle");DockPanel.SetDock(footerSurface,Dock.Bottom);
        var status=Ui.Text("",12);status.TextWrapping=TextWrapping.Wrap;status.Margin=new(0,0,0,10);status.SetBinding(TextBlock.TextProperty,new Binding(nameof(_model.Status)));status.SetBinding(VisibilityProperty,new Binding(nameof(_model.Status)){Converter=new NonEmptyTextVisibilityConverter()});AutomationProperties.SetLiveSetting(status,AutomationLiveSetting.Polite);footer.Children.Add(status);
        _save=Ui.Button("Сохранить",()=>{});_save.IsDefault=!allowConnect;_save.MinWidth=104;_save.SetBinding(IsEnabledProperty,new Binding(nameof(_model.CanEdit)));_save.Click+=SaveClicked;
        var cancel=Ui.Button("Отмена",()=>DialogResult=false);cancel.IsCancel=true;cancel.MinWidth=100;cancel.SetBinding(IsEnabledProperty,new Binding(nameof(_model.CanEdit)));
        var buttons=new DialogActionPanel();buttons.Children.Add(cancel);buttons.Children.Add(_save);
        if(allowConnect)
        {
            var connect=Ui.Button("Сохранить и подключиться",()=>{});connect.IsDefault=true;connect.MinWidth=198;connect.SetBinding(IsEnabledProperty,new Binding(nameof(_model.CanEdit)));connect.Click+=ConnectClicked;
            if(TryFindResource("PrimaryButtonStyle")is Style primary)connect.Style=primary;buttons.Children.Add(connect);
        }
        else if(TryFindResource("PrimaryButtonStyle")is Style primary)_save.Style=primary;
        footer.Children.Add(buttons);root.Children.Add(footerSurface);
        var panel=new StackPanel{Margin=new(24,18,24,0)};panel.SetBinding(IsEnabledProperty,new Binding(nameof(_model.CanEdit)));
        var title=Ui.Text(old is null?"Новый сервер":"Параметры сервера",18,true);title.SetResourceReference(StyleProperty,"DialogTitleStyle");panel.Children.Add(title);
        panel.Children.Add(Field("Название",nameof(_model.ProfileName),nameof(_model.NameError)));
        var endpoint=new Grid();endpoint.ColumnDefinitions.Add(new());endpoint.ColumnDefinitions.Add(new(){Width=new GridLength(110)});
        var host=Field("Адрес сервера",nameof(_model.Host),nameof(_model.HostError));host.Margin=new(0,0,12,0);endpoint.Children.Add(host);
        var port=Field("Порт",nameof(_model.Port),nameof(_model.PortError));Grid.SetColumn(port,1);endpoint.Children.Add(port);panel.Children.Add(endpoint);
        panel.Children.Add(Field("Пользователь Linux",nameof(_model.Username),nameof(_model.UserError)));
        panel.Children.Add(Label("Способ входа"));
        var authentication=new ComboBox{ItemsSource=new[]{"Пароль","Приватный ключ","Keyboard-interactive","SSH-агент (OpenSSH, Pageant-совместимый)"},MinHeight=34,Margin=new(0,0,0,10)};
        authentication.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedIndexProperty,new Binding(nameof(_model.AuthenticationIndex)){Mode=BindingMode.TwoWay});AutomationProperties.SetName(authentication,"Способ входа");_fields["Authentication"]=authentication;panel.Children.Add(authentication);
        var keyPanel=new StackPanel();keyPanel.SetBinding(VisibilityProperty,new Binding(nameof(_model.IsKeyAuthentication)){Converter=new BooleanToVisibilityConverter()});
        var keyField=Field("Приватный ключ",nameof(_model.PrivateKeyPath),nameof(_model.KeyError));keyPanel.Children.Add(keyField);
        var choose=Ui.Button("Выбрать файл…",()=>{var dialog=new OpenFileDialog{Title="Приватный ключ SSH",CheckFileExists=true};if(dialog.ShowDialog(this)==true)_model.PrivateKeyPath=dialog.FileName;});choose.HorizontalAlignment=HorizontalAlignment.Left;choose.Margin=new(0,0,0,8);keyPanel.Children.Add(choose);panel.Children.Add(keyPanel);
        var secretLabel=Label("");secretLabel.SetBinding(TextBlock.TextProperty,new Binding(nameof(_model.SecretLabel)));panel.Children.Add(secretLabel);
        _secret.MinHeight=34;_secret.Margin=new(0,0,0,2);_secret.ValueChanged+=(_,_)=>_model.SecretChanged();_fields["Secret"]=_secret;BindError(_secret,nameof(_model.SecretError));panel.Children.Add(_secret);panel.Children.Add(Error(nameof(_model.SecretError)));
        _model.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(_model.AuthenticationIndex)){_secret.Clear();_loadedSavedSecret=false;}};
        var remember=new CheckBox{Content=new TextBlock{Text="Сохранять секрет с защитой Windows",TextWrapping=TextWrapping.Wrap},Margin=new(0,6,0,4)};remember.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,new Binding(nameof(_model.SaveSecret)){Mode=BindingMode.TwoWay});AutomationProperties.SetName(remember,"Сохранять пароль или парольную фразу с защитой Windows");AutomationProperties.SetHelpText(remember,"Необязательная защищённая копия DPAPI доступна только текущей учётной записи Windows.");panel.Children.Add(remember);
        var secretInfo=Ui.Text("",12);secretInfo.TextWrapping=TextWrapping.Wrap;secretInfo.SetResourceReference(TextBlock.ForegroundProperty,"Muted");secretInfo.Margin=new(0,2,0,4);
        void UpdateSecretSummary()=>secretInfo.Text=_model.SaveSecret?"«Сохранить» запишет секрет без проверки входа. «Сохранить и подключиться» — после успешного входа.":"Сохраняется только профиль. Введённый секрет можно использовать для подключения.";
        UpdateSecretSummary();_model.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(_model.SaveSecret))UpdateSecretSummary();};panel.Children.Add(secretInfo);
        var explanation=Ui.Text("",12);explanation.TextWrapping=TextWrapping.Wrap;explanation.SetResourceReference(TextBlock.ForegroundProperty,"Muted");explanation.SetBinding(TextBlock.TextProperty,new Binding(nameof(_model.SecretExplanation)));
        var secretDetails=new Expander{Header="Подробнее о сохранении секрета",Content=explanation,Margin=new(0,2,0,6)};panel.Children.Add(secretDetails);
        // With an SSH agent there is no secret: hide every secret control and say where the keys come from.
        var usesSecret=new Binding(nameof(_model.UsesSecret)){Converter=new BooleanToVisibilityConverter()};
        foreach(var element in new FrameworkElement[]{secretLabel,_secret,remember,secretInfo,secretDetails})element.SetBinding(VisibilityProperty,usesSecret);
        var agentNote=Ui.Text("Ключи и подписи предоставляет SSH-агент: агент OpenSSH для Windows или сокет из SSH_AUTH_SOCK. Приватный ключ и парольная фраза не попадают в приложение; агент может запросить подтверждение.",12);
        agentNote.Name="AgentNote";agentNote.TextWrapping=TextWrapping.Wrap;agentNote.SetResourceReference(TextBlock.ForegroundProperty,"Muted");agentNote.Margin=new(0,0,0,8);
        agentNote.SetBinding(VisibilityProperty,new Binding(nameof(_model.UsesSecret)){Converter=new InverseBooleanToVisibilityConverter()});panel.Children.Insert(panel.Children.IndexOf(secretLabel),agentNote);
        _advanced.Header="Дополнительные параметры";_advanced.Margin=new(0,4,0,0);var advanced=new StackPanel{Margin=new(0,8,0,0)};
        advanced.Children.Add(Field("Стартовая папка",nameof(_model.InitialPath),nameof(_model.InitialPathError)));
        advanced.Children.Add(Field("Подключаться через (промежуточный сервер, ProxyJump)",nameof(_model.ProxyJump),nameof(_model.ProxyJumpError)));
        // Picking a saved server is the usual case; typing user@host:port or a chain stays possible.
        var pickJump=Ui.FlyoutButton(IconCatalog.Network,"Выбрать сервер",()=>
        {
            var menu=new ContextMenu();menu.Items.Add(Ui.Menu("Напрямую — без промежуточного сервера",()=>_model.ProxyJump=""));
            var others=(App.Store?.Profiles??Enumerable.Empty<ServerProfile>()).Where(p=>p.Id!=old?.Id&&!p.Name.Contains(',')).OrderBy(p=>p.Name,StringComparer.CurrentCultureIgnoreCase).ToArray();
            if(others.Length>0)menu.Items.Add(new Separator());
            foreach(var p in others){var name=p.Name;menu.Items.Add(Ui.Menu(name+"   "+p.Username+"@"+p.Endpoint+(p.ProxyJump.Length>0?" (сам через "+p.ProxyJump+")":""),()=>_model.ProxyJump=name));}
            if(others.Length==0)menu.Items.Add(new MenuItem{Header="Других сохранённых серверов нет",IsEnabled=false});
            return menu;
        },"Подставить сохранённый сервер как промежуточный");
        pickJump.Name="ProxyJumpPick";pickJump.HorizontalAlignment=HorizontalAlignment.Left;pickJump.Margin=new(0,-4,0,6);advanced.Children.Add(pickJump);
        var jumpHint=Ui.Text("Выберите сохранённый сервер или введите пользователь@узел:порт; через запятую — цепочка до трёх узлов. Ключ каждого узла проверяется отдельно. Чтобы закрыть прямой SSH к этому серверу, после подключения откройте «Файервол» → «Доступ только через сервер».",12);jumpHint.TextWrapping=TextWrapping.Wrap;jumpHint.SetResourceReference(TextBlock.ForegroundProperty,"Muted");jumpHint.Margin=new(0,-6,0,10);advanced.Children.Add(jumpHint);
        advanced.Children.Add(Field("Группа на «Главной»",nameof(_model.Group),nameof(_model.GroupError)));
        var timeout=Field("Таймаут подключения, 3–120 секунд",nameof(_model.Timeout),nameof(_model.TimeoutError));timeout.HorizontalAlignment=HorizontalAlignment.Left;timeout.Width=245;advanced.Children.Add(timeout);_advanced.Content=advanced;panel.Children.Add(_advanced);
        // A profile that already routes through jump hosts shows that route without an extra click.
        if(_model.ProxyJump.Trim().Length>0)_advanced.IsExpanded=true;
        root.Children.Add(new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});Content=root;
        Loaded+=async(_,_)=>
        {
            try
            {
                _fields[nameof(_model.ProfileName)].Focus();
                int originalAuthentication=_model.AuthenticationIndex;
                int originalRevision=_secret.EditRevision;
                string? saved=await _model.LoadSecretAsync(_lifetime.Token);
                if(_lifetime.IsCancellationRequested)return;
                if(_model.AuthenticationIndex==originalAuthentication&&_secret.EditRevision==originalRevision&&saved is not null){_secret.Password=saved;_loadedSavedSecret=true;}
            }
            catch(OperationCanceledException)when(_lifetime.IsCancellationRequested){}
        };
        Closing+=(_,e)=>{if(_model.IsSaving&&!_cancelRequested){e.Cancel=true;return;}_lifetime.Cancel();};
        Closed+=(_,_)=>{_secret.Clear();_lifetime.Dispose();};
    }
    static TextBlock Label(string text){var label=Ui.Text(text,12);label.Margin=new(0,9,0,5);label.SetResourceReference(TextBlock.ForegroundProperty,"Muted");return label;}
    FrameworkElement Field(string title,string property,string error)
    {
        var panel=new StackPanel();panel.Children.Add(Label(title));var input=new TextBox{MinHeight=34};input.SetBinding(TextBox.TextProperty,new Binding(property){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});AutomationProperties.SetName(input,title);BindError(input,error);_fields[property]=input;panel.Children.Add(input);panel.Children.Add(Error(error));return panel;
    }
    static void BindError(FrameworkElement field,string property)
    {
        field.SetBinding(Ui.HasValidationErrorProperty,new Binding(property){Converter=new NonEmptyTextBooleanConverter()});
        field.SetBinding(AutomationProperties.HelpTextProperty,new Binding(property));
    }
    TextBlock Error(string property)
    {
        var text=Ui.Text("",12);text.SetBinding(TextBlock.TextProperty,new Binding(property));
        var style=new Style(typeof(TextBlock),(Style)FindResource("ValidationMessageStyle"));var empty=new DataTrigger{Binding=new Binding(property),Value=""};empty.Setters.Add(new Setter(VisibilityProperty,Visibility.Collapsed));style.Triggers.Add(empty);text.Style=style;return text;
    }
    async void SaveClicked(object sender,RoutedEventArgs args)
        =>await SubmitAsync(false);
    async void ConnectClicked(object sender,RoutedEventArgs args)
        =>await SubmitAsync(true);
    async Task SubmitAsync(bool connectNow)
    {
        var result=await _model.SubmitAsync(_secret.Password,connectNow&&_allowConnect,_secret.EditRevision>0||_loadedSavedSecret,_lifetime.Token);
        if(_lifetime.IsCancellationRequested||!IsVisible)return;
        if(result is not null)
        {
            Submission=result;
            DialogResult=true;
        }
        else if(_model.FirstInvalidField is string key)
        {
            if(key is nameof(_model.Timeout) or nameof(_model.InitialPath) or nameof(_model.ProxyJump))_advanced.IsExpanded=true;
            if(_fields.TryGetValue(key,out var field)){if(field is SecretEntry secret)secret.FocusInput();else field.Focus();field.BringIntoView();}
        }
    }
    public void CancelModal(){_cancelRequested=true;_lifetime.Cancel();if(IsVisible)Close();}
}
