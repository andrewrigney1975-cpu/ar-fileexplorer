using System.Reflection;

namespace FileExplorer.Helpers;

/// Reads the "AppVersion" assembly metadata attribute the csproj's SetBuildNumber target stamps
/// in at build time (major.minor.build, e.g. "1.00.037"). Falls back to a placeholder if a debug
/// run somehow bypassed that target (should not happen for a normal build).
public static class AppVersionInfo
{
    public static string Version { get; } =
        Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "AppVersion")?.Value
        ?? "1.00.000";
}
