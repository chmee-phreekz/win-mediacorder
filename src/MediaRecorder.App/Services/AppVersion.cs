using System.Linq;
using System.Reflection;

namespace MediaRecorder.App.Services;

/// <summary>Versionsnummer aus Version.props (Repository-Wurzel), z.B. "Agora 1.50".</summary>
public static class AppVersion
{
    private static string? Get(string key) =>
        typeof(AppVersion).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value;

    public static string Letter => Get("MrVersionLetter") ?? "?";
    public static string Name => Get("MrVersionName") ?? "";
    public static string SubVersion => Get("MrSubVersion") ?? "";

    /// <summary>Anzeige in der Titelleiste.</summary>
    public static string Display => $"{Name} {SubVersion}".Trim();
}
