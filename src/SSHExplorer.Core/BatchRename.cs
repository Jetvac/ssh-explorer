using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SSHExplorer.Core;

public enum RenameCase { AsIs, Lower, Upper }

public sealed record BatchRenameOptions
{
    /// <summary>New name pattern: {name} — name without extension, {ext} — extension without dot, {n} — counter.</summary>
    public string Pattern { get; init; } = "{name}{ext}";
    public string Find { get; init; } = "";
    public string Replace { get; init; } = "";
    public bool UseRegex { get; init; }
    public RenameCase Case { get; init; }
    public int Start { get; init; } = 1;
    public int Step { get; init; } = 1;
    public int Digits { get; init; } = 1;
}

public enum RenameStatus { Ok, Unchanged, Invalid, Duplicate, Exists }

public sealed record RenamePlanItem(string Directory, string OldName, string NewName, RenameStatus Status, string Message)
{
    public string OldPath => PosixPath.Combine(Directory, OldName);
    public string NewPath => PosixPath.Combine(Directory, NewName);
}

/// <summary>
/// Plans a batch rename and checks it before anything changes: invalid names, duplicates inside the batch and collisions
/// with other objects of the folder are errors; renames that swap names inside the batch are allowed and executed
/// through temporary names so that nothing is overwritten.
/// </summary>
public static class BatchRename
{
    public static IReadOnlyList<RenamePlanItem> Plan(IReadOnlyList<(string Directory, string Name, bool IsDirectory)> items, BatchRenameOptions options, Func<string, IReadOnlySet<string>> namesInDirectory)
    {
        Regex? regex = null; string? regexError = null;
        if (options.UseRegex && options.Find.Length > 0)
        {
            try { regex = new Regex(options.Find, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)); }
            catch (ArgumentException ex) { regexError = "Регулярное выражение: " + ex.Message; }
        }
        var draft = new List<(string Dir, string Old, string New, string? Error)>();
        int counter = options.Start;
        foreach (var (dir, name, isDirectory) in items)
        {
            if (regexError is not null) { draft.Add((dir, name, name, regexError)); continue; }
            var (stem, ext) = Split(name, isDirectory);
            string text;
            try
            {
                text = options.Pattern
                    .Replace("{name}", stem, StringComparison.Ordinal)
                    .Replace("{ext}", ext, StringComparison.Ordinal)
                    .Replace("{n}", counter.ToString(CultureInfo.InvariantCulture).PadLeft(Math.Clamp(options.Digits, 1, 9), '0'), StringComparison.Ordinal);
                if (options.Find.Length > 0) text = regex is not null ? regex.Replace(text, options.Replace) : text.Replace(options.Find, options.Replace, StringComparison.Ordinal);
            }
            catch (RegexMatchTimeoutException) { draft.Add((dir, name, name, "Регулярное выражение выполняется слишком долго.")); counter += options.Step; continue; }
            text = options.Case switch { RenameCase.Lower => text.ToLowerInvariant(), RenameCase.Upper => text.ToUpperInvariant(), _ => text };
            draft.Add((dir, name, text, Validate(text)));
            counter += options.Step;
        }
        var result = new List<RenamePlanItem>();
        foreach (var group in draft.GroupBy(d => d.Dir))
        {
            var others = namesInDirectory(group.Key);
            var sources = group.Select(d => d.Old).ToHashSet(StringComparer.Ordinal);
            var targets = group.Where(d => d.Error is null && d.New != d.Old).GroupBy(d => d.New, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var d in group)
            {
                if (d.Error is not null) result.Add(new(d.Dir, d.Old, d.New, RenameStatus.Invalid, d.Error));
                else if (d.New == d.Old) result.Add(new(d.Dir, d.Old, d.New, RenameStatus.Unchanged, "Без изменений"));
                else if (targets.Contains(d.New)) result.Add(new(d.Dir, d.Old, d.New, RenameStatus.Duplicate, "Такое же новое имя у другого объекта"));
                // A name already in the folder is a collision unless that object is renamed away in the same batch.
                else if (others.Contains(d.New) && !sources.Contains(d.New)) result.Add(new(d.Dir, d.Old, d.New, RenameStatus.Exists, "В папке уже есть объект с этим именем"));
                else result.Add(new(d.Dir, d.Old, d.New, RenameStatus.Ok, "Будет переименован"));
            }
        }
        return result;
    }

    /// <summary>
    /// Ordered operations for the valid items: direct renames when the target is free, otherwise source → temporary name → target
    /// (covers swaps and chains). <paramref name="temporary"/> must return a name not used in the folder.
    /// </summary>
    public static IReadOnlyList<(string From, string To)> Operations(IReadOnlyList<RenamePlanItem> plan, Func<string, string> temporary)
    {
        var ok = plan.Where(p => p.Status == RenameStatus.Ok).ToArray();
        var sources = ok.Select(p => p.OldPath).ToHashSet(StringComparer.Ordinal);
        var toTemporary = new List<(string, string)>(); var direct = new List<(string, string)>(); var fromTemporary = new List<(string, string)>();
        foreach (var p in ok)
        {
            // A target that is still another item's current name is reached through a temporary name.
            if (sources.Contains(p.NewPath)) { var tmp = PosixPath.Combine(p.Directory, temporary(p.Directory)); toTemporary.Add((p.OldPath, tmp)); fromTemporary.Add((tmp, p.NewPath)); }
            else direct.Add((p.OldPath, p.NewPath));
        }
        // 1) move blocked sources aside; 2) free targets; 3) every original name is free now, finish the moves.
        return [.. toTemporary, .. direct, .. fromTemporary];
    }

    static (string Stem, string Ext) Split(string name, bool isDirectory)
    {
        if (isDirectory) return (name, "");
        int dot = name.LastIndexOf('.');
        return dot > 0 ? (name[..dot], name[dot..]) : (name, "");
    }

    static string? Validate(string name)
    {
        if (name.Length == 0) return "Пустое имя";
        if (name is "." or "..") return "Имена «.» и «..» недопустимы";
        if (name.Contains('/') || name.Contains('\0')) return "Имя не может содержать «/» или нулевой символ";
        if (Encoding.UTF8.GetByteCount(name) > 255) return "Имя длиннее 255 байт";
        return null;
    }
}
