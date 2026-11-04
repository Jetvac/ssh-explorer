namespace SSHExplorer.App;

public static class CommandGlyphs
{
    public const double CommandSize = 16, NavigationSize = 20, TabSize = 16, SmallFileSize = 16;
    public static string View(string mode) => mode switch
    {
        "Huge" => "\uE8B9", "Large" => "\uE8B8", "Medium" => "\uE80A", "Small" => "\uE80B",
        "List" => "\uE8A1", "Details" => "\uE9D5", "Tile" => "\uE8FD", "Content" => "\uE8A5", _ => "\uE9D5"
    };
}
