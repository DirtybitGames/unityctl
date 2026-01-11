using System.Reflection;

namespace UnityCtl.Bridge;

internal static class VersionInfo
{
    public static string Version
    {
        get
        {
            var informationalVersion = typeof(VersionInfo).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "unknown";

            // Strip build metadata (everything after '+') added by .NET SDK
            // e.g., "0.3.1+abc123def" -> "0.3.1"
            var plusIndex = informationalVersion.IndexOf('+');
            return plusIndex >= 0 ? informationalVersion[..plusIndex] : informationalVersion;
        }
    }
}
