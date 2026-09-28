using System.Text.Json;

namespace SegmentDeck.Core.Resolume;

/// <summary>A parsed WebSocket message from Arena. For parameter_* messages the parameter's own fields sit at the
/// root (id, value, min, max, options, path). Errors carry only path and error.</summary>
public sealed class ParameterMessage
{
    public string Type { get; init; } = "";
    public string Path { get; init; } = "";
    public long Id { get; init; }
    public JsonElement Value { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public string? Error { get; init; }
    public JsonElement Root { get; init; }

    public bool IsComposition => Type == "composition";
    public double? Number => Value.ValueKind == JsonValueKind.Number ? Value.GetDouble() : null;
    public string ValueString => Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "" : Value.ToString();

    public static ParameterMessage Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return new ParameterMessage { Type = "(non-object)", Root = root };

        string type = root.TryGetProperty("type", out var t) ? t.ToString()
                    : root.TryGetProperty("layers", out _) ? "composition"
                    : root.TryGetProperty("error", out _) ? "error"
                    : "(untyped)";

        var value = root.TryGetProperty("value", out var v) ? v : default;
        long id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64() : 0;
        // Older/other shapes nest the parameter object under "value"; accept that too.
        if (value.ValueKind == JsonValueKind.Object && type.StartsWith("parameter_"))
        {
            if (id == 0 && value.TryGetProperty("id", out var nid) && nid.ValueKind == JsonValueKind.Number) id = nid.GetInt64();
            if (value.TryGetProperty("value", out var inner)) value = inner;
        }

        return new ParameterMessage
        {
            Type = type,
            Path = root.TryGetProperty("path", out var p) ? p.ToString() : "",
            Id = id,
            Value = value,
            Min = Composition.Num(root, "min"),
            Max = Composition.Num(root, "max"),
            Error = root.TryGetProperty("error", out var e) ? e.ToString() : null,
            Root = root,
        };
    }
}
