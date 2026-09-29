using CuepointDeck.Core.Logging;
using CuepointDeck.Core.Storage;

namespace CuepointDeck.Core.Settings;

/// <summary>Loads and saves <see cref="AppSettings"/>. A missing file gives defaults; a corrupt file is set aside
/// with a timestamped name, logged, and replaced by defaults so the app always starts.</summary>
public sealed class SettingsStore
{
    public string Path { get; }

    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CuepointDeck", "settings.json");

    public SettingsStore(string? path = null) => Path = path ?? DefaultPath;

    public AppSettings Load()
    {
        if (!File.Exists(Path))
        {
            Log.Info($"No settings file at {Path}; using defaults");
            return new AppSettings();
        }
        try
        {
            var settings = Json.Deserialize<AppSettings>(File.ReadAllText(Path)) ?? new AppSettings();
            settings.LatencyOffsetMsByHost = new Dictionary<string, int>(settings.LatencyOffsetMsByHost, StringComparer.OrdinalIgnoreCase);
            settings.PathMappings ??= new List<PathMapping>();
            if (string.IsNullOrWhiteSpace(settings.LibraryPath)) settings.LibraryPath = AppSettings.DefaultLibraryPath;
            if (settings.SongLayer < 1) settings.SongLayer = 1;
            if (settings.ResolumePort is < 1 or > 65535) settings.ResolumePort = 8080;
            if (string.IsNullOrWhiteSpace(settings.ResolumeHost)) settings.ResolumeHost = "127.0.0.1";
            return settings;
        }
        catch (Exception ex)
        {
            var aside = $"{Path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            try { File.Move(Path, aside, overwrite: true); } catch { }
            Log.Error($"Settings file could not be read and was moved to {aside}; using defaults", ex);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            SafeFile.WriteAllText(Path, Json.Serialize(settings));
            Log.Info($"Settings saved to {Path}");
        }
        catch (Exception ex) { Log.Error("Saving settings", ex); throw; }
    }
}
