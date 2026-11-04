namespace SSHExplorer.App;

public static class CommandGlyphs
{
    public const double CommandSize = 16, NavigationSize = 20, TabSize = 16, SmallFileSize = 16;
    /// <summary>View-mode icon; see <see cref="IconCatalog.View"/> (beta.5 mapped "Small" to U+E80B CallControl).</summary>
    public static string View(string mode) => IconCatalog.View(mode);
}
