using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace SSHExplorer.App;

/// <summary>Shared password/passphrase/OTP input. Revealing changes presentation, never the value.</summary>
public sealed class SecretEntry:UserControl
{
    private readonly PasswordBox masked=new();
    private readonly TextBox visible=new(){Visibility=Visibility.Collapsed};
    private readonly ToggleButton reveal=new(){Content=Ui.Icon("\uE890",16,false),ToolTip="Показать пароль"};
    private bool syncing;
    public event EventHandler? ValueChanged;
    public int EditRevision {get;private set;}
    public bool IsRevealed=>reveal.IsChecked==true;
    public string Password
    {
        get=>masked.Password;
        set
        {
            syncing=true;
            try{masked.Password=value;visible.Text=value;}
            finally{syncing=false;}
        }
    }
    public SecretEntry(string accessibleName="Пароль")
    {
        Focusable=false;var grid=new Grid();grid.ColumnDefinitions.Add(new());grid.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        reveal.SetResourceReference(StyleProperty,"SecretRevealButtonStyle");
        masked.MinHeight=visible.MinHeight=34;grid.Children.Add(masked);grid.Children.Add(visible);Grid.SetColumn(reveal,1);grid.Children.Add(reveal);Content=grid;
        AutomationProperties.SetName(masked,accessibleName);AutomationProperties.SetName(visible,accessibleName+" — показан");AutomationProperties.SetName(reveal,"Показать пароль");
        masked.PasswordChanged+=(_,_)=>
        {
            if(syncing)return;syncing=true;try{visible.Text=masked.Password;}finally{syncing=false;}EditRevision++;ValueChanged?.Invoke(this,EventArgs.Empty);
        };
        visible.TextChanged+=(_,_)=>
        {
            if(syncing)return;syncing=true;try{masked.Password=visible.Text;}finally{syncing=false;}EditRevision++;ValueChanged?.Invoke(this,EventArgs.Empty);
        };
        reveal.Checked+=(_,_)=>SetRevealed(true);reveal.Unchecked+=(_,_)=>SetRevealed(false);
    }
    private void SetRevealed(bool value)
    {
        masked.Visibility=value?Visibility.Collapsed:Visibility.Visible;visible.Visibility=value?Visibility.Visible:Visibility.Collapsed;
        reveal.ToolTip=value?"Скрыть пароль":"Показать пароль";AutomationProperties.SetName(reveal,value?"Скрыть пароль":"Показать пароль");
        if(!syncing&&IsLoaded&&Window.GetWindow(this)?.IsVisible==true)FocusInput();
    }
    public void FocusInput(){if(IsRevealed){visible.Focus();visible.SelectAll();}else masked.Focus();}
    public void Clear(){syncing=true;try{masked.Clear();visible.Clear();reveal.IsChecked=false;}finally{syncing=false;}}
}
