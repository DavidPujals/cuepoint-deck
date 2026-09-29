using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace CuepointDeck.Spike.Resolume;

/// <summary>REST calls against http://host:port/api/v1. Used for one-off reads and as an alternative
/// transport for seek and connect so the two can be compared.</summary>
public sealed class ResolumeRest
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly string _base;

    public ResolumeRest(string host, int port) => _base = $"http://{host}:{port}/api/v1";

    public async Task<string> GetProductAsync()
    {
        var p = await _http.GetFromJsonAsync<JsonElement>($"{_base}/product").ConfigureAwait(false);
        var name = p.TryGetProperty("name", out var n) ? n.GetString() : "?";
        string V(string k) => p.TryGetProperty(k, out var v) ? v.ToString() : "?";
        return $"{name} {V("major")}.{V("minor")}.{V("micro")} rev {V("revision")}";
    }

    public Task<string> GetCompositionRawAsync() => _http.GetStringAsync($"{_base}/composition");

    public Task<string> GetRawAsync(string path) => _http.GetStringAsync($"{_base}{path}");

    /// <summary>POST /composition/layers/{l}/clips/{c}/connect. Body omitted = short click (true then false).</summary>
    public async Task<int> ConnectClipAsync(int layer, int clip, bool? down = null)
    {
        var url = $"{_base}/composition/layers/{layer}/clips/{clip}/connect";
        HttpContent? body = down is null ? null : new StringContent(down.Value ? "true" : "false", Encoding.UTF8, "application/json");
        var r = await _http.PostAsync(url, body).ConfigureAwait(false);
        return (int)r.StatusCode;
    }

    /// <summary>POST /composition/layers/{l}/clear: disconnects whatever plays on that layer. Spike/testing only.</summary>
    public async Task<int> ClearLayerAsync(int layer)
    {
        var r = await _http.PostAsync($"{_base}/composition/layers/{layer}/clear", null).ConfigureAwait(false);
        return (int)r.StatusCode;
    }

    /// <summary>PUT /parameter/by-id/{id} with {"value": x}.</summary>
    public async Task<int> SetParameterByIdAsync(long id, double value)
    {
        var r = await _http.PutAsync($"{_base}/parameter/by-id/{id}",
            new StringContent(JsonSerializer.Serialize(new { value }), Encoding.UTF8, "application/json")).ConfigureAwait(false);
        return (int)r.StatusCode;
    }

    /// <summary>PUT /parameter/by-id/{id} with an arbitrary partial parameter body, e.g. new { @in = 60000.0 }.</summary>
    public async Task<int> SetParameterFieldsAsync(long id, object body)
    {
        var r = await _http.PutAsync($"{_base}/parameter/by-id/{id}",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")).ConfigureAwait(false);
        return (int)r.StatusCode;
    }

    /// <summary>PUT /composition/layers/{l}/clips/{c} with a partial clip body carrying only transport.position.</summary>
    public async Task<int> SetClipPositionAsync(int layer, int clip, double value)
    {
        var body = JsonSerializer.Serialize(new { transport = new { position = new { value } } });
        var r = await _http.PutAsync($"{_base}/composition/layers/{layer}/clips/{clip}",
            new StringContent(body, Encoding.UTF8, "application/json")).ConfigureAwait(false);
        return (int)r.StatusCode;
    }
}
