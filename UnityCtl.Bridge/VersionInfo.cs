using System.Reflection;

namespace UnityCtl.Bridge;

internal static class VersionInfo
{
    public static string Version =>
        typeof(VersionInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";
}
