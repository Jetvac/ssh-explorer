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
public sealed class ProfileDialog:Window
{
    private readonly ProfileViewModel _model;
    private readonly PasswordBox _secret=new();
    private readonly Dictionary<string,Control> _fields=[];
    private readonly CancellationTokenSource _lifetime=new();
    private readonly Expander _advanced=new();
    private readonly Button _save;
    public ProfileViewModel Model=>_model;
    public ServerProfile? SavedProfile=>_model.SavedProfile;
    public ProfileDialog(IProfilePersistence persistence,ServerProfile? old=null,Window? owner=null)
    {
        _model=new(persistence,old);DataContext=_model;
        Title=old is null?"Добавить сервер":"Изменить сервер";Width=540;MinWidth=430;
        MaxHeight=Math.Max(320,SystemParameters.WorkArea.Height-48);SizeToContent=SizeToContent.Height;
        ResizeMode=ResizeMode.CanResize;ShowInTaskbar=false;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        if(owner is not null&&(owner.IsLoaded||new System.Windows.Interop.WindowInteropHelper(owner).Handle!=IntPtr.Zero)){Owner=owner;WindowStartupLocation=WindowStartupLocation.CenterOwner;}
        var root=new DockPanel();root.SetResourceReference(Panel.BackgroundProperty,"Surface");
        var footer=new StackPanel{Margin=new(24,14,24,18)};DockPanel.SetDock(footer,Dock.Bottom);
        var status=Ui.Text("",12);status.TextWrapping=TextWrapping.Wrap;status.Margin=new(0,0,0,10);status.SetBinding(TextBlock.TextProperty,new Binding(nameof(_model.Status)));footer.Children.Add(status);
        _save=Ui.Button("Сохранить",()=>{});_save.IsDefault=true;_save.MinWidth=112;_save.SetBinding(IsEnabledProperty,new Binding(nameof(_model.CanEdit)));_save.Click+=SaveClicked;
        if(TryFindResource("PrimaryButtonStyle")is Style primary)_save.Style=primary;
        var cancel=Ui.Button("Отмена",()=>DialogResult=false);cancel.IsCancel=true;cancel.MinWidth=100;cancel.SetBinding(IsEnabledProperty,new Binding(nameof(_model.CanEdit)));
        var buttons=Ui.Row(cancel,_save);buttons.HorizontalAlignment=HorizontalAlignment.Right;footer.Children.Add(buttons);root.Children.Add(footer);
        var panel=new StackPanel{Margin=new(24,18,24,0)};panel.SetBinding(IsEnabledProperty,new Binding(nameof(_model.CanEdit)));
        panel.Children.Add(Ui.Text(old is null?"Новый сервер":"Параметры сервера",20,true));
        panel.Children.Add(Field("Название",nameof(_model.ProfileName),nameof(_model.NameError)));
        var endpoint=new Grid();endpoint.ColumnDefinitions.Add(new());endpoint.ColumnDefinitions.Add(new(){Width=new GridLength(110)});
        var host=Field("Адрес сервера",nameof(_model.Host),nameof(_model.HostError));host.Margin=new(0,0,12,0);endpoint.Children.Add(host);
        var port=Field("Порт",nameof(_model.Port),nameof(_model.PortError));Grid.SetColumn(port,1);endpoint.Children.Add(port);panel.Children.Add(endpoint);
        panel.Children.Add(Field("Пользователь Linux",nameof(_model.Username),nameof(_model.UserError)));
        panel.Children.Add(Label("Способ входа"));
        var authentication=new ComboBox{ItemsSource=new[]{"Пароль","Приватный ключ","Keyboard-interactive"},MinHeight=34,Margin=new(0,0,0,10)};
        authentication.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedIndexProperty,new Binding(nameof(_model.AuthenticationIndex)){Mode=BindingMode.TwoWay});AutomationProperties.SetName(authentication,"Способ входа");_fields["Authentication"]=authentication;panel.Children.Add(authentication);
        var keyPanel=new StackPanel();keyPanel.SetBinding(VisibilityProperty,new Binding(nameof(_model.IsKeyAuthentication)){Converter=new BooleanToVisibilityConverter()});
        var keyField=Field("Приватный ключ",nameof(_model.PrivateKeyPath),nameof(_model.KeyError));keyPanel.Children.Add(keyField);
        var choose=Ui.Button("Выбрать файл…",()=>{var dialog=new OpenFileDialog{Title="Приватный ключ SSH",CheckFileExists=true};if(dialog.ShowDialog(this)==true)_model.PrivateKeyPath=dialog.FileName;});choose.HorizontalAlignment=HorizontalAlignment.Left;choose.Margin=new(0,0,0,8);keyPanel.Children.Add(choose);panel.Children.Add(keyPanel);
        var secretLabel=Label("");secretLabel.SetBinding(TextBlock.TextProperty,new Binding(nameof(_model.SecretLabel)));panel.Children.Add(secretLabel);
        _secret.MinHeight=34;_secret.Margin=new(0,0,0,2);AutomationProperties.SetName(_secret,"Пароль или парольная фраза ключа");_secret.PasswordChanged+=(_,_)=>_model.SecretChanged();_fields["Secret"]=_secret;panel.Children.Add(_secret);panel.Children.Add(Error(nameof(_model.SecretError)));
        var secretInfo=Ui.Text("",12);secretInfo.TextWrapping=TextWrapping.Wrap;secretInfo.SetResourceReference(TextBlock.ForegroundProperty,"Muted");secretInfo.SetBinding(TextBlock.TextProperty,new Binding(nameof(_model.SecretExplanation)));secretInfo.Margin=new(0,2,0,4);panel.Children.Add(secretInfo);
        var remember=new CheckBox{Content="Сохранять секрет с защитой Windows",Margin=new(0,6,0,8)};remember.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,new Binding(nameof(_model.SaveSecret)){Mode=BindingMode.TwoWay});AutomationProperties.SetName(remember,"Сохранять пароль или парольную фразу с защитой Windows");panel.Children.Add(remember);
        _advanced.Header="Дополнительные параметры";_advanced.Margin=new(0,4,0,0);var advanced=new StackPanel{Margin=new(0,8,0,0)};
        advanced.Children.Add(Field("Стартовая папка",nameof(_model.InitialPath),nameof(_model.InitialPathError)));
        var timeout=Field("Таймаут подключения, 3–120 секунд",nameof(_model.Timeout),nameof(_model.TimeoutError));timeout.HorizontalAlignment=HorizontalAlignment.Left;timeout.Width=245;advanced.Children.Add(timeout);_advanced.Content=advanced;panel.Children.Add(_advanced);
        root.Children.Add(new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});Content=root;
        Loaded+=async(_,_)=>
        {
            try
            {
                int originalAuthentication=_model.AuthenticationIndex;
                string? saved=await _model.LoadSecretAsync(_lifetime.Token);
                if(_lifetime.IsCancellationRequested)return;
                if(_model.AuthenticationIndex==originalAuthentication&&string.IsNullOrEmpty(_secret.Password)&&saved is not null)_secret.Password=saved;
                _fields[nameof(_model.ProfileName)].Focus();
            }
            catch(OperationCanceledException)when(_lifetime.IsCancellationRequested){}
        };
        Closing+=(_,e)=>{if(_model.IsSaving){e.Cancel=true;return;}_lifetime.Cancel();};
        Closed+=(_,_)=>{_secret.Clear();_lifetime.Dispose();};
    }
    static TextBlock Label(string text){var label=Ui.Text(text,12);label.Margin=new(0,9,0,5);label.SetResourceReference(TextBlock.ForegroundProperty,"Muted");return label;}
    FrameworkElement Field(string title,string property,string error)
    {
        var panel=new StackPanel();panel.Children.Add(Label(title));var input=new TextBox{MinHeight=34};input.SetBinding(TextBox.TextProperty,new Binding(property){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});AutomationProperties.SetName(input,title);_fields[property]=input;panel.Children.Add(input);panel.Children.Add(Error(error));return panel;
    }
    TextBlock Error(string property)
    {
        var text=Ui.Text("",12);text.TextWrapping=TextWrapping.Wrap;text.Margin=new(0,4,0,0);text.SetBinding(TextBlock.TextProperty,new Binding(property));text.SetResourceReference(TextBlock.ForegroundProperty,"Ink");
        var style=new Style(typeof(TextBlock));var empty=new DataTrigger{Binding=new Binding(property),Value=""};empty.Setters.Add(new Setter(VisibilityProperty,Visibility.Collapsed));style.Triggers.Add(empty);text.Style=style;return text;
    }
    async void SaveClicked(object sender,RoutedEventArgs args)
    {
        if(await _model.SaveAsync(_secret.Password,_lifetime.Token))
        {
            if(_model.CleanupWarning is not null)MessageBox.Show(this,_model.CleanupWarning,"Профиль сохранён",MessageBoxButton.OK,MessageBoxImage.Warning);
            DialogResult=true;
        }
        else if(_model.FirstInvalidField is string key)
        {
            if(key is nameof(_model.Timeout) or nameof(_model.InitialPath))_advanced.IsExpanded=true;
            if(_fields.TryGetValue(key,out var field)){field.Focus();field.BringIntoView();}
        }
    }
}
