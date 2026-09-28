using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace SegmentDeck.Spike.Resolume;

/// <summary>One clip slot in the composition, flattened for the grid. Everything is parsed defensively:
/// any property Resolume doesn't send is left null or empty rather than throwing.</summary>
public sealed class ClipInfo : INotifyPropertyChanged
{
    public int Layer { get; init; }
    public int Column { get; init; }
    public string LayerName { get; init; } = "";
    public long ClipId { get; init; }
    public string Name { get; init; } = "";
    public bool IsEmpty { get; init; }

    public string FilePath { get; init; } = "";
    public bool? FileExists { get; init; }
    public double? DurationMs { get; init; }
    public double? Fps { get; init; }
    public string TransportType { get; init; } = "";

    public long ConnectedParamId { get; init; }
    public string[] ConnectedOptions { get; init; } = Array.Empty<string>();

    public long PositionParamId { get; init; }
    public double? PosMin { get; init; }
    public double? PosMax { get; init; }
    public long SpeedParamId { get; init; }
    public double? SpeedMin { get; init; }
    public double? SpeedMax { get; init; }
    public double? TransportDurationValue { get; init; }
    public double? TransportDurationMax { get; init; }

    public string TriggerStyle { get; init; } = "";
    public string FaderStart { get; init; } = "";
    public string BeatSnap { get; init; } = "";
    public string PlayMode { get; init; } = "";
    public string PlayDirection { get; init; } = "";
    public string Target { get; init; } = "";

    private string _connected = "";
    public string Connected { get => _connected; set { if (_connected == value) return; _connected = value; OnChanged(); OnChanged(nameof(IsConnected)); } }

    private double? _posValue;
    public double? PosValue { get => _posValue; set { _posValue = value; OnChanged(); } }

    private double? _speedValue;
    public double? SpeedValue { get => _speedValue; set { _speedValue = value; OnChanged(); } }

    /// <summary>"Connected" or "Connected &amp; previewing". Previewing alone is not live on output.</summary>
    public bool IsConnected => Connected.StartsWith("Connected", StringComparison.OrdinalIgnoreCase);

    public string BasePath => $"/composition/layers/{Layer}/clips/{Column}";
    public string ConnectedPath => BasePath + "/connected";
    public string PositionPath => BasePath + "/transport/position";
    public string SpeedPath => BasePath + "/transport/controls/speed";
    public string ConnectPath => BasePath + "/connect";
    public string Display => IsEmpty ? $"L{Layer} C{Column} (empty)" : $"L{Layer} C{Column} {Name}";
    public string DurationText => DurationMs is double d ? TimeSpan.FromMilliseconds(d).ToString(@"mm\:ss\.fff") : "";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public static class CompositionParser
{
    public static List<ClipInfo> Parse(JsonElement composition)
    {
        var list = new List<ClipInfo>();
        if (!composition.TryGetProperty("layers", out var layers) || layers.ValueKind != JsonValueKind.Array) return list;

        int layerIndex = 0;
        foreach (var layer in layers.EnumerateArray())
        {
            layerIndex++;
            var layerName = Str(layer, "name");
            if (!layer.TryGetProperty("clips", out var clips) || clips.ValueKind != JsonValueKind.Array) continue;

            int col = 0;
            foreach (var clip in clips.EnumerateArray())
            {
                col++;
                list.Add(ParseClip(clip, layerIndex, col, layerName));
            }
        }
        return list;
    }

    public static ClipInfo ParseClip(JsonElement clip, int layer, int col, string layerName)
    {
        JsonElement video = default, fileinfo = default, transport = default, controls = default;
        bool hasVideo = clip.TryGetProperty("video", out video) && video.ValueKind == JsonValueKind.Object;
        bool hasFile = hasVideo && video.TryGetProperty("fileinfo", out fileinfo) && fileinfo.ValueKind == JsonValueKind.Object;
        bool hasTransport = clip.TryGetProperty("transport", out transport) && transport.ValueKind == JsonValueKind.Object;
        bool hasControls = hasTransport && transport.TryGetProperty("controls", out controls) && controls.ValueKind == JsonValueKind.Object;

        double? fps = null;
        if (hasFile && fileinfo.TryGetProperty("framerate", out var fr) && fr.ValueKind == JsonValueKind.Object)
        {
            var num = Num(fr, "num"); var den = Num(fr, "denom");
            if (num is > 0 && den is > 0) fps = num / den;
        }

        var name = Str(clip, "name");
        var connected = clip.TryGetProperty("connected", out var conn) ? conn : default;
        var pos = hasTransport && transport.TryGetProperty("position", out var p) ? p : default;
        var speed = hasControls && controls.TryGetProperty("speed", out var s) ? s : default;
        var dur = hasControls && controls.TryGetProperty("duration", out var d) ? d : default;

        var info = new ClipInfo
        {
            Layer = layer,
            Column = col,
            LayerName = layerName,
            ClipId = Id(clip),
            Name = name,
            IsEmpty = !hasFile && string.IsNullOrEmpty(name),
            FilePath = hasFile ? Str(fileinfo, "path", raw: true) : "",
            FileExists = hasFile && fileinfo.TryGetProperty("exists", out var ex) && ex.ValueKind is JsonValueKind.True or JsonValueKind.False ? ex.GetBoolean() : null,
            DurationMs = hasFile ? Num(fileinfo, "duration_ms") : null,
            Fps = fps,
            TransportType = Str(clip, "transporttype"),
            ConnectedParamId = Id(connected),
            ConnectedOptions = Options(connected),
            PositionParamId = Id(pos),
            PosMin = Num(pos, "min"),
            PosMax = Num(pos, "max"),
            SpeedParamId = Id(speed),
            SpeedMin = Num(speed, "min"),
            SpeedMax = Num(speed, "max"),
            TransportDurationValue = Num(dur, "value"),
            TransportDurationMax = Num(dur, "max"),
            TriggerStyle = Str(clip, "triggerstyle"),
            FaderStart = Str(clip, "faderstart"),
            BeatSnap = Str(clip, "beatsnap"),
            Target = Str(clip, "target"),
            PlayMode = hasControls ? Str(controls, "playmode") : "",
            PlayDirection = hasControls ? Str(controls, "playdirection") : "",
        };
        info.Connected = ParamValueString(connected);
        info.PosValue = Num(pos, "value");
        info.SpeedValue = Num(speed, "value");
        return info;
    }

    /// <summary>Reads a parameter's "value" as text. Works for a parameter object or a bare primitive.</summary>
    public static string ParamValueString(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Object)
            return el.TryGetProperty("value", out var v) ? v.ToString() : "";
        return el.ValueKind == JsonValueKind.Undefined ? "" : el.ToString();
    }

    private static string Str(JsonElement obj, string prop, bool raw = false)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var el)) return "";
        if (raw || el.ValueKind != JsonValueKind.Object) return el.ValueKind == JsonValueKind.Null ? "" : el.ToString();
        return ParamValueString(el);
    }

    public static double? Num(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var el)) return null;
        return el.ValueKind == JsonValueKind.Number ? el.GetDouble() : null;
    }

    private static long Id(JsonElement obj)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt64() : 0;

    private static string[] Options(JsonElement param)
    {
        if (param.ValueKind != JsonValueKind.Object || !param.TryGetProperty("options", out var o) || o.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return o.EnumerateArray().Select(x => x.ToString()).ToArray();
    }
}
