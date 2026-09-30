using System.Reflection;
using System.Text.Json;
using Hollowbound.Simulation;

namespace Hollowbound;

internal static class BuildInfo
{
    private static readonly Assembly _assembly = typeof(BuildInfo).Assembly;

    public static string InformationalVersion { get; } =
        _assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? _assembly.GetName().Version?.ToString() ?? "unknown";

    public static string Version { get; } = InformationalVersion.Split('+')[0];
    public static string? SourceCommit { get; } = ReadMetadata("SourceCommit");
    public static bool? Dirty { get; } = bool.TryParse(ReadMetadata("BuildDirty"), out bool dirty) ? dirty : null;
    public static string Configuration { get; } =
        _assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown";

    public static string WindowTitle => $"Hollowbound {Version}" + (Dirty switch
    {
        true => " (local changes)",
        false => string.Empty,
        null => " (source unverified)"
    });

    public static void WriteVersion()
    {
        Console.WriteLine($"Hollowbound {InformationalVersion}");
        Console.WriteLine($"Source commit: {SourceCommit ?? "unknown"}");
        Console.WriteLine($"Working tree at build: {(Dirty switch { true => "dirty", false => "clean", null => "unknown" })}");
        Console.WriteLine($"Configuration: {Configuration}; snapshot: v{WorldSnapshot.CurrentVersion}; analytics: v{AnalyticsLogger.SchemaVersion}");
    }

    public static void WriteJson()
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Product = "Hollowbound",
            Version,
            InformationalVersion,
            SourceCommit,
            Dirty,
            Configuration,
            TargetFramework = ReadMetadata("BuildTargetFramework"),
            RuntimeIdentifier = ReadMetadata("BuildRuntimeIdentifier"),
            SdkVersion = ReadMetadata("BuildSdk"),
            SnapshotVersion = WorldSnapshot.CurrentVersion,
            AnalyticsSchema = AnalyticsLogger.SchemaVersion
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true }));
    }

    private static string? ReadMetadata(string key)
    {
        string? value = _assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == key)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
