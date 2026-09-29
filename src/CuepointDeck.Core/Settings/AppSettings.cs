using System.Text.Json.Serialization;

namespace CuepointDeck.Core.Settings;

public enum AppRole { Controller, Follow }
public enum TriggerMode { Queue, Cut }

public sealed class PathMapping
{
    /// <summary>Path prefix as Resolume reports it, e.g. <c>D:\Media</c>.</summary>
    public string From { get; set; } = "";
    /// <summary>Path prefix as this PC can reach it, e.g. <c>\\CITY-VISUALS\Media</c>.</summary>
    public string To { get; set; } = "";
}

/// <summary>Per-machine settings. Lives in %AppData%\CuepointDeck\settings.json, never in the library.</summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Hostname or IP of the Resolume PC. 127.0.0.1 rather than localhost: Arena listens on IPv4 only and
    /// "localhost" costs a 2 s IPv6 timeout on every connect.</summary>
    public string ResolumeHost { get; set; } = "127.0.0.1";
    public int ResolumePort { get; set; } = 8080;
    public AppRole Role { get; set; } = AppRole.Controller;
    public string LibraryPath { get; set; } = DefaultLibraryPath;
    public int SongLayer { get; set; } = 1;

    /// <summary>Latency offset per Resolume host, so a local and a remote setup can each be tuned.</summary>
    public Dictionary<string, int> LatencyOffsetMsByHost { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public const int DefaultLatencyOffsetMs = 40;

    public bool LaunchSongsFromSetlist { get; set; } = false;
    public TriggerMode DefaultTrigger { get; set; } = TriggerMode.Queue;
    public List<PathMapping> PathMappings { get; set; } = new();
    public string FfmpegPath { get; set; } = "";
    public bool AlwaysOnTop { get; set; } = false;
    /// <summary>Card size multiplier; 1.0 is about 240 px wide at 100% scaling.</summary>
    public double CardScale { get; set; } = 1.0;
    public string? ActiveSetlist { get; set; }

    [JsonIgnore]
    public int LatencyOffsetMs
    {
        get => LatencyOffsetMsByHost.TryGetValue(ResolumeHost, out var v) ? v : DefaultLatencyOffsetMs;
        set => LatencyOffsetMsByHost[ResolumeHost] = value;
    }

    public static string DefaultLibraryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CuepointDeck Library");

    public AppSettings Clone() => Json.Deserialize<AppSettings>(Json.Serialize(this))!;
}
