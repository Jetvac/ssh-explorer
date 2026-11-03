using System.Reflection;
using System.Runtime.InteropServices;

namespace SSHExplorer.App;

internal static class BuildInfo
{
    public static string Version { get; } = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    public static string DisplayVersion => Version.Split('+')[0];
    public static string Commit => Version.Contains('+') ? Version[(Version.IndexOf('+') + 1)..] : "unknown";
    public static string EnvironmentDescription => $"SSHExplorer/{Version}; {RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}; {RuntimeInformation.FrameworkDescription}";
}
