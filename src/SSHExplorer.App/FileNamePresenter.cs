using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using SSHExplorer.App.ViewModels;

namespace SSHExplorer.App;

/// <summary>The editor is a real TextBox inside the name template in every view, including recycled tiles.</summary>
public sealed class FileNamePresenter : Grid
{
    public static readonly DependencyProperty WrapNameProperty = DependencyProperty.Register(nameof(WrapName), typeof(bool), typeof(FileNamePresenter), new PropertyMetadata(false, (d, e) =>
    {
        var presenter = (FileNamePresenter)d;
        presenter.label.TextWrapping = (bool)e.NewValue ? TextWrapping.Wrap : TextWrapping.NoWrap;
        presenter.label.TextAlignment = (bool)e.NewValue ? TextAlignment.Center : TextAlignment.Left;
    }));
    public bool WrapName { get => (bool)GetValue(WrapNameProperty); set => SetValue(WrapNameProperty, value); }
    readonly TextBlock label = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBox editor = new() { MinWidth = 40, MinHeight = 28, Padding = new Thickness(4, 2, 4, 2), VerticalContentAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    FileItemViewModel? item;
    FileNameEdit? edit;
    public FileNamePresenter()
    {
        VerticalAlignment = VerticalAlignment.Center;
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(FileItemViewModel.DisplayName)));
        Children.Add(label); Children.Add(editor);
        editor.TextChanged += (_, _) => { if (edit is not null && !edit.IsBusy && editor.Text != edit.Text) edit.Text = editor.Text; };
        editor.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key is not Key.Enter and not Key.Escape) return;
            e.Handled = true;
            var browser = ParentBrowser(); if (browser is null) return;
            if (e.Key == Key.Enter) await browser.CommitNameEditAsync(); else browser.CancelNameEdit();
        };
        DataContextChanged += (_, _) => Attach(); Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }
    FileBrowserView? ParentBrowser()
    {
        DependencyObject? parent = this;
        while (parent is not null) { if (parent is FileBrowserView browser) return browser; parent = VisualTreeHelper.GetParent(parent); }
        return null;
    }
    void Detach()
    {
        if (item is not null) item.PropertyChanged -= ItemChanged;
        if (edit is not null) edit.PropertyChanged -= EditChanged;
        item = null; edit = null;
    }
    void Attach()
    {
        Detach(); item = DataContext as FileItemViewModel;
        if (item is not null) item.PropertyChanged += ItemChanged;
        AttachEdit();
    }
    void ItemChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName is null or nameof(FileItemViewModel.Edit)) AttachEdit(); }
    void AttachEdit()
    {
        if (edit is not null) edit.PropertyChanged -= EditChanged;
        edit = item?.Edit;
        if (edit is not null) edit.PropertyChanged += EditChanged;
        Update();
    }
    void EditChanged(object? sender, PropertyChangedEventArgs e) => Update();
    void Update()
    {
        var editing = edit?.IsActive == true;
        label.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        editor.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        if (!editing) return;
        if (editor.Text != edit!.Text) editor.Text = edit.Text;
        editor.IsReadOnly = edit.IsBusy;
        editor.ToolTip = edit.IsBusy ? "Переименование…" : edit.Error.Length > 0 ? edit.Error : "Enter — сохранить · Esc — отменить";
        System.Windows.Automation.AutomationProperties.SetName(editor, "Имя: " + item!.Name);
        Ui.SetValidationMessage(editor, edit.Error);
        System.Windows.Automation.AutomationProperties.SetHelpText(editor, editor.ToolTip.ToString());
    }
    internal void FocusEditor(bool selectBaseName)
    {
        if (edit?.IsActive != true) return;
        editor.Focus();
        if (selectBaseName) editor.Select(0, edit.InitialSelectionLength);
    }
}
