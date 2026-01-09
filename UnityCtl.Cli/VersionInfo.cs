using System.Reflection;

namespace UnityCtl.Cli;

internal static class VersionInfo
{
    public static string Version =>
        typeof(VersionInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";
}
