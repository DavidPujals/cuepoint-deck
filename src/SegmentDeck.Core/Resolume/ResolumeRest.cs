using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SegmentDeck.Core.Resolume;

/// <summary>REST calls against http://host:port/api/v1. The WebSocket is the main channel; REST is for one-off reads
/// and for writes the WebSocket cannot express (the position in-point). Nothing here can stop or pause output.</summary>
public sealed class ResolumeRest : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly string _base;

    public ResolumeRest(string host, int port) => _base = $"http://{host}:{port}/api/v1";

    public async Task<string> GetProductAsync(CancellationToken ct = default)
    {
        var p = await _http.GetFromJsonAsync<JsonElement>($"{_base}/product", ct).ConfigureAwait(false);
        string V(string k) => p.TryGetProperty(k, out var v) ? v.ToString() : "?";
        return $"{V("name")} {V("major")}.{V("minor")}.{V("micro")} rev {V("revision")}";
    }

    public Task<string> GetCompositionRawAsync(CancellationToken ct = default) => _http.GetStringAsync($"{_base}/composition", ct);
    public Task<string> GetRawAsync(string path, CancellationToken ct = default) => _http.GetStringAsync($"{_base}{path}", ct);

    /// <summary>POST /composition/layers/{l}/clips/{c}/connect: a short click on the clip.</summary>
    public async Task<int> ConnectClipAsync(int layer, int clip)
    {
        var r = await _http.PostAsync($"{_base}/composition/layers/{layer}/clips/{clip}/connect", null).ConfigureAwait(false);
        return (int)r.StatusCode;
    }

    /// <summary>PUT /parameter/by-id/{id} with {"value": x}.</summary>
    public Task<int> SetParameterByIdAsync(long id, object value) => SetParameterFieldsAsync(id, new { value });

    /// <summary>PUT /parameter/by-id/{id} with a partial parameter body, e.g. new { @in = 60000.0 }.</summary>
    public async Task<int> SetParameterFieldsAsync(long id, object body)
    {
        var r = await _http.PutAsync($"{_base}/parameter/by-id/{id}",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")).ConfigureAwait(false);
        return (int)r.StatusCode;
    }

    public void Dispose() => _http.Dispose();
}
