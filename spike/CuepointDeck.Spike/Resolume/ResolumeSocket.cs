using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace CuepointDeck.Spike.Resolume;

/// <summary>Thin wrapper over ClientWebSocket for ws://host:port/api/v1.
/// Raises every message as raw text on a background thread. No reconnect: the spike is driven by hand.</summary>
public sealed class ResolumeSocket : IDisposable
{
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    /// <summary>Raw JSON text of every server message plus a Stopwatch timestamp taken the moment it was received.</summary>
    public event Action<string, long>? RawMessage;
    public event Action<string>? Closed;

    public bool IsOpen => _ws?.State == WebSocketState.Open;

    public async Task ConnectAsync(string host, int port, CancellationToken ct)
    {
        Dispose();
        _ws = new ClientWebSocket();
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        _cts = new CancellationTokenSource();
        await _ws.ConnectAsync(new Uri($"ws://{host}:{port}/api/v1"), ct).ConfigureAwait(false);
        var ws = _ws;
        var token = _cts.Token;
        _ = Task.Run(() => ReceiveLoop(ws, token), token);
    }

    private async Task ReceiveLoop(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        Closed?.Invoke($"server closed ({r.CloseStatus}: {r.CloseStatusDescription})");
                        return;
                    }
                    ms.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);

                var stamp = System.Diagnostics.Stopwatch.GetTimestamp();
                RawMessage?.Invoke(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length), stamp);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Closed?.Invoke(ex.Message); }
    }

    public async Task SendAsync(string json)
    {
        var ws = _ws ?? throw new InvalidOperationException("Not connected");
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendGate.WaitAsync().ConfigureAwait(false);
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false); }
        finally { _sendGate.Release(); }
    }

    public Task SubscribeAsync(string path) => SendAsync(JsonSerializer.Serialize(new { action = "subscribe", parameter = path }));
    public Task UnsubscribeAsync(string path) => SendAsync(JsonSerializer.Serialize(new { action = "unsubscribe", parameter = path }));
    public Task GetAsync(string path) => SendAsync(JsonSerializer.Serialize(new { action = "get", parameter = path }));
    public Task SetAsync(string path, object value) => SendAsync(JsonSerializer.Serialize(new { action = "set", parameter = path, value }));
    public Task TriggerAsync(string path) => SendAsync(JsonSerializer.Serialize(new { action = "trigger", parameter = path }));

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _ws?.Abort(); } catch { }
        _ws?.Dispose();
        _ws = null;
        _cts = null;
    }
}
