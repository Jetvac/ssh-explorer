using System.Text;

namespace SSHExplorer.Core;

public sealed record DocumentTextFormat(string Encoding, bool Bom, string Newlines, bool FinalNewline)
{
    public string Description => $"{Encoding}{(Bom ? " с BOM" : " без BOM")}, {Newlines}{(FinalNewline ? ", перевод строки в конце" : "")}";
    public bool NeedsConfirmation(DocumentTextFormat other) => Encoding != other.Encoding || Bom != other.Bom ||
        (Newlines != "нет" && other.Newlines != "нет" && Newlines != other.Newlines);
}

public static class DocumentText
{
    public const int MaximumBytes = 32 * 1024 * 1024;
    public static readonly string[] Extensions = [".txt", ".log", ".conf", ".cfg", ".ini", ".json", ".yaml", ".yml", ".toml", ".xml", ".md", ".sh", ".service", ".env", ".csv", ".py", ".js", ".cs", ".bat", ".ps1"];
    static DocumentText() => System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    public static (string Text, DocumentTextFormat Format) InspectSaved(byte[] bytes, string? originalEncoding)
    {
        if (originalEncoding is not null && !originalEncoding.StartsWith("utf", StringComparison.OrdinalIgnoreCase) && bytes.Any(b => b >= 128))
            try { return Decode(bytes); } catch (IOException) { }
        return Decode(bytes, originalEncoding);
    }
    public static (string Text, DocumentTextFormat Format) Decode(byte[] bytes, string? explicitEncoding = null)
    {
        if (bytes.Length > MaximumBytes) throw new IOException("Документ больше 32 МиБ. Скачайте его отдельно и отправьте через очередь передач.");
        int bom = 0; string name;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) { name = "utf-8"; bom = 3; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 }) || bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) throw new IOException("UTF-32 требует отдельного выбора редактора/локальной копии.");
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { name = "utf-16"; bom = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { name = "utf-16BE"; bom = 2; }
        else if (explicitEncoding is not null) name = explicitEncoding;
        else
        {
            var sample = Math.Min(bytes.Length, 8192); int even = 0, odd = 0;
            for (int i = 0; i < sample; i++) if (bytes[i] == 0) { if (i % 2 == 0) even++; else odd++; }
            name = sample >= 4 && bytes.Length % 2 == 0 && odd > sample / 5 && even < sample / 30 ? "utf-16" :
                sample >= 4 && bytes.Length % 2 == 0 && even > sample / 5 && odd < sample / 30 ? "utf-16BE" : "utf-8";
        }
        string text;
        try { text = System.Text.Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes, bom, bytes.Length - bom); }
        catch (DecoderFallbackException ex) { throw new IOException("Кодировка не распознана уверенно. Выберите её явно; исходные байты сохранены.", ex); }
        if (text.Contains('\0') || text.Count(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t' and not '\f') > Math.Max(0, text.Length / 100))
            throw new IOException("Содержимое похоже на двоичные данные. Скачайте файл отдельно.");
        int lf = 0, crlf = 0, cr = 0;
        for (int i = 0; i < text.Length; i++) { if (text[i] == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; } else cr++; } else if (text[i] == '\n') lf++; }
        var endings = new[] { (lf, "LF"), (crlf, "CRLF"), (cr, "CR") }.Where(x => x.Item1 > 0).Select(x => x.Item2).ToArray();
        return (text, new(name.ToLowerInvariant(), bom != 0, endings.Length == 0 ? "нет" : string.Join("+", endings), text.EndsWith('\n') || text.EndsWith('\r')));
    }
}
