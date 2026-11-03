using SSHExplorer.Core;

namespace SSHExplorer.App.ViewModels;

/// <summary>A rename belongs to a captured object, never to a selection index or a recycled container.</summary>
public sealed class FileNameEdit(FileItemViewModel item) : ObservableObject
{
    string text = item.Name, error = "";
    bool busy, active = true;
    public FileItemViewModel Item { get; } = item;
    public string SourcePath { get; } = item.Entry.FullPath;
    public string Text { get => text; set { if (Set(ref text, value)) Error = ""; } }
    public string Error { get => error; private set => Set(ref error, value); }
    public bool IsBusy { get => busy; private set => Set(ref busy, value); }
    public bool IsActive { get => active; private set => Set(ref active, value); }
    public int InitialSelectionLength => SelectionLength(Item.Name, Item.Entry.IsDirectory);
    public static int SelectionLength(string name, bool directory)
    {
        var dot = name.LastIndexOf('.');
        return directory || name.StartsWith('.') || dot <= 0 || dot == name.Length - 1 ? name.Length : dot;
    }
    public bool Cancel()
    {
        if (IsBusy) return false;
        IsActive = false; Item.Edit = null; return true;
    }
    public async Task<string?> CommitAsync(Func<string, string, Task> rename)
    {
        if (!IsActive || IsBusy) return null;
        try { PosixPath.ValidateName(Text); }
        catch (ArgumentException ex) { Error = ex.Message; return null; }
        var target = PosixPath.Combine(PosixPath.Parent(SourcePath), Text);
        if (target == SourcePath) { Cancel(); return target; }
        IsBusy = true; Error = "";
        try
        {
            await rename(SourcePath, target);
            IsActive = false; Item.Edit = null; return target;
        }
        catch (Exception ex) { Error = ErrorMessages.Describe(ex); return null; }
        finally { IsBusy = false; }
    }
}
