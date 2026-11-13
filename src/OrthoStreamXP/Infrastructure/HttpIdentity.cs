using System.Reflection;

namespace OrthoStreamXP;

/// <summary>
/// How OrthoStreamXP names itself to web servers: one User-Agent, <c>orthostreamxp/&lt;version&gt;</c>, for the tile
/// downloads, the connection test, the mesh downloads and SimBrief, so a provider sees one program at one version.
/// </summary>
internal static class HttpIdentity
{
    /// <summary>The app's version without the build's commit ("1.3.0-rc.8"); "0" when the assembly has none.</summary>
    public static string Version { get; } =
        typeof(HttpIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";

    public static string UserAgent { get; } = $"orthostreamxp/{Version}";
}
