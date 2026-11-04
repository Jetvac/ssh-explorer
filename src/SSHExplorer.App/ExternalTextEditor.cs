using System.Diagnostics;
using System.IO;

namespace SSHExplorer.App;

public static class ExternalTextEditor
{
    public static string SystemNotepad => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
    public static async Task LaunchAsync(string workingFile, string? preferredExecutable)
    {
        string executable = string.IsNullOrWhiteSpace(preferredExecutable) ? SystemNotepad : preferredExecutable;
        if (!Path.IsPathFullyQualified(executable) || executable.StartsWith(@"\\", StringComparison.Ordinal) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
            throw new IOException("Редактор не найден. Выберите установленный EXE в параметрах или сохраните локальную копию.");
        if (!Path.IsPathFullyQualified(workingFile) || !File.Exists(workingFile)) throw new IOException("Рабочая копия отсутствует. Восстановите её или выберите сохранённую копию.");
        await Task.Run(() =>
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false };
            start.ArgumentList.Add(workingFile);
            using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить редактор.");
            // A tabbed Notepad may hand off and exit immediately. Never wait, kill, or infer a save from this process.
        });
    }
}
