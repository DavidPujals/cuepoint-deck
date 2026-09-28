using System.Text.Json;

namespace SegmentDeck.Core.Resolume;

/// <summary>One clip slot as seen in the composition JSON, plus live state that updates arrive for.</summary>
public sealed class ClipInfo
{
    public int Layer { get; init; }
    public int Column { get; init; }
    public string LayerName { get; init; } = "";
    public long ClipId { get; init; }
    public string Name { get; init; } = "";
    public bool IsEmpty { get; init; }

    public string FilePath { get; init; } = "";
    public bool? FileExists { get; init; }
    public double DurationMs { get; init; }
    public double Fps { get; init; }
    public string TransportType { get; init; } = "";
    /// <summary>Restart | Continue | Relative. Decides the connect-and-seek order (see docs/RESOLUME_NOTES.md §6).</summary>
    public string RetriggerMode { get; init; } = "";

    public long ConnectedParamId { get; init; }
    public long PositionParamId { get; init; }
    public double PosMin { get; init; }
    public double PosMax { get; init; }
    public long SpeedParamId { get; init; }
    public long PlayDirectionParamId { get; init; }
    public long PlayModeAwayParamId { get; init; }

    // ---- live state, written by ResolumeConnection on its receive thread
    public string ConnectedState { get; internal set; } = "";
    public double? PositionMs { get; internal set; }
    public long PositionTimestamp { get; internal set; }
    public double Speed { get; internal set; } = 1.0;
    public string PlayDirection { get; internal set; } = ">";

    /// <summary>"Connected" or "Connected &amp; previewing": the clip is live on output.</summary>
    public bool IsConnected => ConnectedState.StartsWith("Connected", StringComparison.OrdinalIgnoreCase);
    public bool IsPaused => PlayDirection == "||";

    public string ConnectPath => $"/composition/layers/{Layer}/clips/{Column}/connect";
    public string Location => $"L{Layer} C{Column}";
    public string Display => IsEmpty ? $"{Location} (empty)" : $"{Location} {Name}";

    /// <summary>Position parameter units are not assumed: the value is scaled through the parameter's own range onto the
    /// file duration. On Arena 7.24 the range is already milliseconds, so this is an identity.</summary>
    public double RawToMs(double raw)
        => PosMax > PosMin && DurationMs > 0 ? (raw - PosMin) / (PosMax - PosMin) * DurationMs : raw;

    public double MsToRaw(double ms)
        => PosMax > PosMin && DurationMs > 0 ? PosMin + ms / DurationMs * (PosMax - PosMin) : ms;

    public double FrameMs => Fps > 0 ? 1000.0 / Fps : 1000.0 / 30;
}

public sealed class LayerInfo
{
    public int Index { get; init; }
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public IReadOnlyList<ClipInfo> Clips { get; init; } = Array.Empty<ClipInfo>();
    public ClipInfo? ConnectedClip => Clips.FirstOrDefault(c => c.IsConnected);
}

/// <summary>Parsed snapshot of the composition message. Replaced wholesale whenever Resolume re-sends it.</summary>
public sealed class Composition
{
    public string Name { get; init; } = "";
    public IReadOnlyList<LayerInfo> Layers { get; init; } = Array.Empty<LayerInfo>();
    public IReadOnlyList<ClipInfo> Clips { get; init; } = Array.Empty<ClipInfo>();
    public DateTime ReceivedAt { get; init; } = DateTime.Now;

    private readonly Dictionary<long, ClipInfo> _byClipId = new();
    private readonly Dictionary<long, ClipInfo> _byConnectedParam = new();
    private readonly Dictionary<long, ClipInfo> _byPositionParam = new();
    private readonly Dictionary<long, ClipInfo> _bySpeedParam = new();
    private readonly Dictionary<long, ClipInfo> _byPlayDirectionParam = new();

    public ClipInfo? ClipById(long clipId) => _byClipId.GetValueOrDefault(clipId);
    public ClipInfo? ByConnectedParam(long id) => _byConnectedParam.GetValueOrDefault(id);
    public ClipInfo? ByPositionParam(long id) => _byPositionParam.GetValueOrDefault(id);
    public ClipInfo? BySpeedParam(long id) => _bySpeedParam.GetValueOrDefault(id);
    public ClipInfo? ByPlayDirectionParam(long id) => _byPlayDirectionParam.GetValueOrDefault(id);
    public LayerInfo? Layer(int index) => Layers.FirstOrDefault(l => l.Index == index);
    public ClipInfo? ConnectedClipOn(int layer) => Layer(layer)?.ConnectedClip;
    public IEnumerable<ClipInfo> ConnectedClips => Clips.Where(c => c.IsConnected);

    public static Composition Parse(JsonElement root)
    {
        var layers = new List<LayerInfo>();
        var all = new List<ClipInfo>();
        if (root.TryGetProperty("layers", out var layersEl) && layersEl.ValueKind == JsonValueKind.Array)
        {
            int li = 0;
            foreach (var layerEl in layersEl.EnumerateArray())
            {
                li++;
                var layerName = ParamString(layerEl, "name");
                var clips = new List<ClipInfo>();
                if (layerEl.TryGetProperty("clips", out var clipsEl) && clipsEl.ValueKind == JsonValueKind.Array)
                {
                    int ci = 0;
                    foreach (var clipEl in clipsEl.EnumerateArray())
                    {
                        ci++;
                        var clip = ParseClip(clipEl, li, ci, layerName);
                        clips.Add(clip);
                        all.Add(clip);
                    }
                }
                layers.Add(new LayerInfo { Index = li, Id = Id(layerEl), Name = layerName, Clips = clips });
            }
        }

        var comp = new Composition { Name = ParamString(root, "name"), Layers = layers, Clips = all };
        foreach (var c in all)
        {
            comp._byClipId[c.ClipId] = c;
            if (c.ConnectedParamId != 0) comp._byConnectedParam[c.ConnectedParamId] = c;
            if (c.PositionParamId != 0) comp._byPositionParam[c.PositionParamId] = c;
            if (c.SpeedParamId != 0) comp._bySpeedParam[c.SpeedParamId] = c;
            if (c.PlayDirectionParamId != 0) comp._byPlayDirectionParam[c.PlayDirectionParamId] = c;
        }
        return comp;
    }

    private static ClipInfo ParseClip(JsonElement clip, int layer, int column, string layerName)
    {
        var video = Prop(clip, "video");
        var fileinfo = Prop(video, "fileinfo");
        var transport = Prop(clip, "transport");
        var position = Prop(transport, "position");
        var controls = Prop(transport, "controls");
        var speed = Prop(controls, "speed");
        var playdir = Prop(controls, "playdirection");
        var playmodeaway = Prop(controls, "playmodeaway");
        var connected = Prop(clip, "connected");

        double fps = 0;
        var fr = Prop(fileinfo, "framerate");
        if (Num(fr, "num") is > 0 and var num && Num(fr, "denom") is > 0 and var den) fps = num / den;

        var name = ParamString(clip, "name");
        var hasFile = fileinfo.ValueKind == JsonValueKind.Object;
        var info = new ClipInfo
        {
            Layer = layer,
            Column = column,
            LayerName = layerName,
            ClipId = Id(clip),
            Name = name,
            IsEmpty = !hasFile && name.Length == 0,
            FilePath = hasFile ? Str(fileinfo, "path") : "",
            FileExists = hasFile && fileinfo.TryGetProperty("exists", out var ex) && ex.ValueKind is JsonValueKind.True or JsonValueKind.False ? ex.GetBoolean() : null,
            DurationMs = Num(fileinfo, "duration_ms") ?? 0,
            Fps = fps,
            TransportType = ParamString(clip, "transporttype"),
            RetriggerMode = ParamString(controls, "playmodeaway"),
            ConnectedParamId = Id(connected),
            PositionParamId = Id(position),
            PosMin = Num(position, "min") ?? 0,
            PosMax = Num(position, "max") ?? 0,
            SpeedParamId = Id(speed),
            PlayDirectionParamId = Id(playdir),
            PlayModeAwayParamId = Id(playmodeaway),
        };
        info.ConnectedState = ParamString(clip, "connected");
        info.Speed = Num(speed, "value") ?? 1.0;
        info.PlayDirection = ParamString(controls, "playdirection");
        if (Num(position, "value") is double pv) info.PositionMs = info.RawToMs(pv);
        return info;
    }

    // ---- tiny JSON helpers, all tolerant of missing properties
    private static JsonElement Prop(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var el) ? el : default;

    public static double? Num(JsonElement obj, string prop)
    {
        var el = Prop(obj, prop);
        return el.ValueKind == JsonValueKind.Number ? el.GetDouble() : null;
    }

    private static string Str(JsonElement obj, string prop)
    {
        var el = Prop(obj, prop);
        return el.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "" : el.ToString();
    }

    /// <summary>Reads a parameter's value as text: "name": {"value": "..."} → "...".</summary>
    private static string ParamString(JsonElement obj, string prop)
    {
        var el = Prop(obj, prop);
        if (el.ValueKind == JsonValueKind.Object) return Str(el, "value");
        return el.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "" : el.ToString();
    }

    private static long Id(JsonElement obj)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt64() : 0;
}
