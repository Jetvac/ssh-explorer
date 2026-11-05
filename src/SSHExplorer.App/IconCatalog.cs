using System.Collections.Frozen;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace SSHExplorer.App;

/// <summary>
/// Named command/navigation icons. Two sources, one visual contract (16 DIP grid, 1 DIP stroke,
/// round caps/joins, optical centre on the grid):
/// <list type="bullet">
/// <item>System glyphs of Segoe Fluent Icons (fallback Segoe MDL2 Assets) for generic Windows
/// commands whose code points are verified against Microsoft's published list.</item>
/// <item>SSH Explorer vector icons for commands without a fitting system glyph — view modes,
/// server, transfers, documents, power, task-manager sections. Path data uses the SVG/WPF
/// shared mini-language, so <c>scripts/design/render-icons.py</c> renders the same geometry into
/// the review sheet.</item>
/// </list>
/// File and folder pictures are NOT here: they come from the Windows Shell
/// (<see cref="SystemFileIconProvider"/>). The application mark is <see cref="AppIcon"/>.
/// </summary>
public static class IconCatalog
{
    public const string Prefix = "icon:";
    // Vector keys (pass to Ui.Icon / Ui.CommandButton like a glyph string).
    public const string ViewHuge = Prefix + "view-huge", ViewLarge = Prefix + "view-large", ViewMedium = Prefix + "view-medium", ViewSmall = Prefix + "view-small",
        ViewList = Prefix + "view-list", ViewDetails = Prefix + "view-details", ViewTiles = Prefix + "view-tiles", ViewContent = Prefix + "view-content",
        TabList = Prefix + "tab-list", NewTab = Prefix + "new-tab", Documents = Prefix + "documents", Transfers = Prefix + "transfers",
        Server = Prefix + "server", Processes = Prefix + "processes", Performance = Prefix + "performance", Terminal = Prefix + "terminal",
        Power = Prefix + "power", Restart = Prefix + "restart", Reconnect = Prefix + "reconnect", Network = Prefix + "network", Storage = Prefix + "storage", Settings = Prefix + "settings", Docker = Prefix + "docker", Services = Prefix + "services", SplitRight = Prefix + "split-right", SplitDown = Prefix + "split-down", Firewall = Prefix + "firewall";

    public sealed record Entry(string Key, string Purpose, string Source, string? Glyph, string? PathData, string Style = "stroke");

    // Verified Segoe Fluent Icons / MDL2 code points (learn.microsoft.com segoe-fluent-icons-font).
    public static readonly Entry[] SystemGlyphs =
    [
        new("add", "Добавить / новая вкладка", "Segoe Fluent Icons U+E710 Add", "", null),
        new("back", "Назад", "Segoe Fluent Icons U+E72B Back", "", null),
        new("forward", "Вперёд", "Segoe Fluent Icons U+E72A Forward", "", null),
        new("up", "Вверх", "Segoe Fluent Icons U+E74A Up", "", null),
        new("refresh", "Обновить", "Segoe Fluent Icons U+E72C Refresh", "", null),
        new("home", "Главная", "Segoe Fluent Icons U+E80F Home", "", null),
        new("folder", "Файлы", "Segoe Fluent Icons U+E8B7 Folder", "", null),
        new("cut", "Вырезать", "Segoe Fluent Icons U+E8C6 Cut", "", null),
        new("copy", "Копировать", "Segoe Fluent Icons U+E8C8 Copy", "", null),
        new("paste", "Вставить", "Segoe Fluent Icons U+E77F Paste", "", null),
        new("rename", "Переименовать", "Segoe Fluent Icons U+E8AC Rename", "", null),
        new("delete", "Удалить", "Segoe Fluent Icons U+E74D Delete", "", null),
        new("upload", "Загрузить на сервер", "Segoe Fluent Icons U+E898 Upload", "", null),
        new("download", "Скачать", "Segoe Fluent Icons U+E896 Download", "", null),
        new("sort", "Сортировать", "Segoe Fluent Icons U+E8CB Sort", "", null),
        new("more", "Ещё", "Segoe Fluent Icons U+E712 More", "", null),
        new("settings", "Параметры", "Segoe Fluent Icons U+E713 Setting", "", null),
        new("close", "Закрыть", "Segoe Fluent Icons U+E711 Cancel", "", null),
        new("search", "Поиск", "Segoe Fluent Icons U+E721 Search", "", null),
        new("document", "Документ", "Segoe Fluent Icons U+E8A5 Document", "", null),
        new("pin", "Закрепить", "Segoe Fluent Icons U+E718 Pin", "", null),
        new("info", "Свойства / сведения", "Segoe Fluent Icons U+E946 Info", "", null),
        new("undo", "Отменить", "Segoe Fluent Icons U+E7A7 Undo", "", null),
        new("edit", "Редактировать", "Segoe Fluent Icons U+E70F Edit", "", null),
        new("play", "Продолжить", "Segoe Fluent Icons U+E768 Play", "", null),
        new("pause", "Пауза", "Segoe Fluent Icons U+E769 Pause", "", null),
        new("remove", "Уменьшить", "Segoe Fluent Icons U+E738 Remove", "", null),
        new("check", "Готово", "Segoe Fluent Icons U+E73E CheckMark", "", null),
        new("chevron-down", "Раскрыть", "Segoe Fluent Icons U+E70D ChevronDown", "", null),
        new("chevron-right", "Разделитель пути", "Segoe Fluent Icons U+E76C ChevronRight", "", null),
        new("down", "Следующее", "Segoe Fluent Icons U+E74B Down", "", null),
        new("view", "Показать", "Segoe Fluent Icons U+E890 View", "", null),
        new("command-prompt", "Терминал", "Segoe Fluent Icons U+E756 CommandPrompt", "", null),
    ];

    // SSH Explorer vector set, 16x16 grid. Coordinates at .5 keep 1 DIP strokes crisp at 100 %.
    public static readonly Entry[] Vectors =
    [
        new("view-huge", "Огромные значки: один крупный эскиз", "SSH Explorer vector", null, "M3.5 2.5H12.5A1 1 0 0 1 13.5 3.5V12.5A1 1 0 0 1 12.5 13.5H3.5A1 1 0 0 1 2.5 12.5V3.5A1 1 0 0 1 3.5 2.5Z M4.5 11.5L7 8.5L9 10.5L10.5 9L12 11.5"),
        new("view-large", "Крупные значки: сетка 2×2", "SSH Explorer vector", null, "M3.5 2.5H6A1 1 0 0 1 7 3.5V6A1 1 0 0 1 6 7H3.5A1 1 0 0 1 2.5 6V3.5A1 1 0 0 1 3.5 2.5Z M10 2.5H12.5A1 1 0 0 1 13.5 3.5V6A1 1 0 0 1 12.5 7H10A1 1 0 0 1 9 6V3.5A1 1 0 0 1 10 2.5Z M3.5 9H6A1 1 0 0 1 7 10V12.5A1 1 0 0 1 6 13.5H3.5A1 1 0 0 1 2.5 12.5V10A1 1 0 0 1 3.5 9Z M10 9H12.5A1 1 0 0 1 13.5 10V12.5A1 1 0 0 1 12.5 13.5H10A1 1 0 0 1 9 12.5V10A1 1 0 0 1 10 9Z"),
        new("view-medium", "Обычные значки: сетка 3×3", "SSH Explorer vector", null, "M2.5 2.5H4.5V4.5H2.5Z M7 2.5H9V4.5H7Z M11.5 2.5H13.5V4.5H11.5Z M2.5 7H4.5V9H2.5Z M7 7H9V9H7Z M11.5 7H13.5V9H11.5Z M2.5 11.5H4.5V13.5H2.5Z M7 11.5H9V13.5H7Z M11.5 11.5H13.5V13.5H11.5Z"),
        new("view-small", "Мелкие значки: строки значок+имя в две колонки", "SSH Explorer vector", null, "M1.5 3H3V4.5H1.5Z M5 3.75H7 M1.5 7.25H3V8.75H1.5Z M5 8H7 M1.5 11.5H3V13H1.5Z M5 12.25H7 M9 3H10.5V4.5H9Z M12.5 3.75H14.5 M9 7.25H10.5V8.75H9Z M12.5 8H14.5 M9 11.5H10.5V13H9Z M12.5 12.25H14.5"),
        new("view-list", "Список: одна колонка коротких строк", "SSH Explorer vector", null, "M2.5 3.5H3.5 M6 3.5H13.5 M2.5 8H3.5 M6 8H13.5 M2.5 12.5H3.5 M6 12.5H13.5"),
        new("view-details", "Таблица: строки с колонками и заголовком", "SSH Explorer vector", null, "M1.5 3H14.5 M1.5 6.5H8.5 M10.5 6.5H14.5 M1.5 9.5H8.5 M10.5 9.5H14.5 M1.5 12.5H8.5 M10.5 12.5H14.5"),
        new("view-tiles", "Плитка: значок слева и две строки сведений", "SSH Explorer vector", null, "M2.5 2H5.5A1 1 0 0 1 6.5 3V6A1 1 0 0 1 5.5 7H2.5A1 1 0 0 1 1.5 6V3A1 1 0 0 1 2.5 2Z M8.5 3.5H14.5 M8.5 5.5H12.5 M2.5 9H5.5A1 1 0 0 1 6.5 10V13A1 1 0 0 1 5.5 14H2.5A1 1 0 0 1 1.5 13V10A1 1 0 0 1 2.5 9Z M8.5 10.5H14.5 M8.5 12.5H12.5"),
        new("view-content", "Содержимое: строки с разделителями", "SSH Explorer vector", null, "M2.5 2H4.5A1 1 0 0 1 5.5 3V5A1 1 0 0 1 4.5 6H2.5A1 1 0 0 1 1.5 5V3A1 1 0 0 1 2.5 2Z M7.5 3H14.5 M7.5 5H11 M1.5 8H14.5 M2.5 10H4.5A1 1 0 0 1 5.5 11V13A1 1 0 0 1 4.5 14H2.5A1 1 0 0 1 1.5 13V11A1 1 0 0 1 2.5 10Z M7.5 11H14.5 M7.5 13H11"),
        new("tab-list", "Все вкладки", "SSH Explorer vector", null, "M3 3.5H13A1.5 1.5 0 0 1 14.5 5V12A1.5 1.5 0 0 1 13 13.5H3A1.5 1.5 0 0 1 1.5 12V5A1.5 1.5 0 0 1 3 3.5Z M1.5 6.5H14.5 M5.5 3.5V6.5 M9.5 3.5V6.5"),
        new("new-tab", "Новая вкладка", "SSH Explorer vector", null, "M3 3.5H13A1.5 1.5 0 0 1 14.5 5V12A1.5 1.5 0 0 1 13 13.5H3A1.5 1.5 0 0 1 1.5 12V5A1.5 1.5 0 0 1 3 3.5Z M8 6.5V11 M5.75 8.75H10.25"),
        new("documents", "Редактируемые документы", "SSH Explorer vector", null, "M10 14.5H4A1.5 1.5 0 0 1 2.5 13V3A1.5 1.5 0 0 1 4 1.5H8.5L11.5 4.5V7 M8.5 1.5V4.5H11.5 M8.5 13.5L13 9L14.5 10.5L10 15H8.5Z"),
        new("transfers", "Передачи", "SSH Explorer vector", null, "M5 13.5V2.5 M2 5.5L5 2.5L8 5.5 M11 2.5V13.5 M8 10.5L11 13.5L14 10.5"),
        new("server", "Сервер", "SSH Explorer vector", null, "M3.5 2H12.5A1 1 0 0 1 13.5 3V6A1 1 0 0 1 12.5 7H3.5A1 1 0 0 1 2.5 6V3A1 1 0 0 1 3.5 2Z M3.5 9H12.5A1 1 0 0 1 13.5 10V13A1 1 0 0 1 12.5 14H3.5A1 1 0 0 1 2.5 13V10A1 1 0 0 1 3.5 9Z M5 4.5H5.5 M5 11.5H5.5 M8 4.5H11 M8 11.5H11"),
        new("processes", "Процессы", "SSH Explorer vector", null, "M3 2.5H13A1.5 1.5 0 0 1 14.5 4V12A1.5 1.5 0 0 1 13 13.5H3A1.5 1.5 0 0 1 1.5 12V4A1.5 1.5 0 0 1 3 2.5Z M4 6H5 M7 6H12 M4 8.5H5 M7 8.5H12 M4 11H5 M7 11H10"),
        new("performance", "Производительность", "SSH Explorer vector", null, "M1.5 13.5H14.5 M2.5 11L5.5 7L8 9.5L11.5 4L13.5 6.5"),
        new("terminal", "Терминал", "SSH Explorer vector", null, "M3 2.5H13A1.5 1.5 0 0 1 14.5 4V12A1.5 1.5 0 0 1 13 13.5H3A1.5 1.5 0 0 1 1.5 12V4A1.5 1.5 0 0 1 3 2.5Z M4.5 6L6.75 8.25L4.5 10.5 M8.5 10.5H11.5"),
        new("power", "Выключить", "SSH Explorer vector", null, "M8 1.5V7.5 M4.6 3.9A5.5 5.5 0 1 0 11.4 3.9"),
        new("restart", "Перезагрузить", "SSH Explorer vector", null, "M13.5 8A5.5 5.5 0 1 1 11.9 4.1 M12.5 1.5V4.5H9.5"),
        new("reconnect", "Переподключиться", "SSH Explorer vector", null, "M6 1.5V4.5 M10 1.5V4.5 M4 4.5H12V7A4 4 0 0 1 4 7Z M8 11V14.5"),
        new("network", "Сеть", "SSH Explorer vector", null, "M6.5 1.5H9.5V4.5H6.5Z M2 11.5H5V14.5H2Z M11 11.5H14V14.5H11Z M8 4.5V8 M3.5 11.5V8H12.5V11.5"),
        new("settings", "Параметры", "SSH Explorer vector", null, "M6.79 3.15L7.1 1.56L8.9 1.56L9.21 3.15A5 5 0 0 1 10.58 3.71L11.91 2.81L13.19 4.09L12.29 5.42A5 5 0 0 1 12.85 6.79L14.44 7.1L14.44 8.9L12.85 9.21A5 5 0 0 1 12.29 10.58L13.19 11.91L11.91 13.19L10.58 12.29A5 5 0 0 1 9.21 12.85L8.9 14.44L7.1 14.44L6.79 12.85A5 5 0 0 1 5.42 12.29L4.09 13.19L2.81 11.91L3.71 10.58A5 5 0 0 1 3.15 9.21L1.56 8.9L1.56 7.1L3.15 6.79A5 5 0 0 1 3.71 5.42L2.81 4.09L4.09 2.81L5.42 3.71A5 5 0 0 1 6.79 3.15Z M10.25 8A2.25 2.25 0 1 1 5.75 8A2.25 2.25 0 1 1 10.25 8Z"),
        new("docker", "Docker", "SSH Explorer vector", null, "M1.5 8.5H14.5L13.6 11.9A2 2 0 0 1 11.7 13.5H4.3A2 2 0 0 1 2.4 11.9Z M3.5 5.5H6V8.5H3.5Z M6.5 5.5H9V8.5H6.5Z M9.5 5.5H12V8.5H9.5Z M6.5 2.5H9V5.5H6.5Z"),
        new("services", "Службы", "SSH Explorer vector", null, "M3 2.5H13A1.5 1.5 0 0 1 14.5 4V12A1.5 1.5 0 0 1 13 13.5H3A1.5 1.5 0 0 1 1.5 12V4A1.5 1.5 0 0 1 3 2.5Z M7.5 8A1.5 1.5 0 1 1 4.5 8A1.5 1.5 0 1 1 7.5 8Z M6 4.75V5.75 M6 10.25V11.25 M2.75 8H3.75 M8.25 8H9.25 M10.5 6.5H12.5 M10.5 9.5H12.5"),
        new("split-right", "Разделить вправо", "SSH Explorer vector", null, "M3 2.5H13A1.5 1.5 0 0 1 14.5 4V12A1.5 1.5 0 0 1 13 13.5H3A1.5 1.5 0 0 1 1.5 12V4A1.5 1.5 0 0 1 3 2.5Z M8 2.5V13.5"),
        new("split-down", "Разделить вниз", "SSH Explorer vector", null, "M3 2.5H13A1.5 1.5 0 0 1 14.5 4V12A1.5 1.5 0 0 1 13 13.5H3A1.5 1.5 0 0 1 1.5 12V4A1.5 1.5 0 0 1 3 2.5Z M1.5 8H14.5"),
        new("firewall", "Файервол", "SSH Explorer vector", null, "M8 1.75L13.5 3.75V7.5C13.5 10.9 11.2 13.4 8 14.5C4.8 13.4 2.5 10.9 2.5 7.5V3.75Z M5.5 8L7.25 9.75L10.75 6.25"),
        new("storage", "Хранилища", "SSH Explorer vector", null, "M3 4.5H13A1.5 1.5 0 0 1 14.5 6V10A1.5 1.5 0 0 1 13 11.5H3A1.5 1.5 0 0 1 1.5 10V6A1.5 1.5 0 0 1 3 4.5Z M11 8H11.5 M4 8H8"),
    ];

    static readonly FrozenDictionary<string, Entry> vectors = Vectors.ToFrozenDictionary(entry => Prefix + entry.Key, StringComparer.Ordinal);
    static readonly Dictionary<string, Geometry> geometries = new(StringComparer.Ordinal);

    public static bool IsVector(string key) => key.StartsWith(Prefix, StringComparison.Ordinal);
    public static bool TryGet(string key, out Entry entry) => vectors.TryGetValue(key, out entry!);
    public static IEnumerable<string> VectorKeys => vectors.Keys;

    public static Geometry Geometry(string key)
    {
        if (geometries.TryGetValue(key, out var cached)) return cached;
        if (!vectors.TryGetValue(key, out var entry) || entry.PathData is null) throw new KeyNotFoundException("Unknown vector icon " + key);
        var geometry = System.Windows.Media.Geometry.Parse(entry.PathData); geometry.Freeze();
        geometries[key] = geometry; return geometry;
    }

    /// <summary>Icon matching a file view mode; each mode shows the arrangement it produces.</summary>
    public static string View(string mode) => mode switch
    {
        "Huge" => ViewHuge, "Large" => ViewLarge, "Medium" => ViewMedium, "Small" => ViewSmall,
        "List" => ViewList, "Details" => ViewDetails, "Tile" => ViewTiles, "Content" => ViewContent, _ => ViewDetails,
    };
}

/// <summary>Draws a <see cref="IconCatalog"/> vector with the inherited text foreground.</summary>
public sealed class VectorIcon : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(VectorIcon), new FrameworkPropertyMetadata(SystemColors.ControlTextBrush, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(VectorIcon), new FrameworkPropertyMetadata(16d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public string Key { get; }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double IconSize { get => (double)GetValue(IconSizeProperty); set => SetValue(IconSizeProperty, value); }
    public VectorIcon(string key, double size) { Key = key; IconSize = size; SnapsToDevicePixels = true; UseLayoutRounding = true; Focusable = false; }
    protected override Size MeasureOverride(Size availableSize) => new(IconSize, IconSize);
    protected override void OnRender(DrawingContext drawing)
    {
        var geometry = IconCatalog.Geometry(Key);
        double scale = IconSize / 16, strokeDip = IconSize >= 19 ? 1.25 : 1.0;
        var pen = new Pen(Foreground, strokeDip / scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        // An inherited foreground may be animated or data-bound (menus, hover states) and then
        // cannot be frozen; freezing is only an optimisation, so skip it in that case.
        if (pen.CanFreeze) pen.Freeze();
        drawing.PushTransform(new ScaleTransform(scale, scale));
        drawing.DrawGeometry(null, pen, geometry);
        drawing.Pop();
    }
    public override string ToString() => Key.ToString(CultureInfo.InvariantCulture);
}
