using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace CuepointDeck.Core.Resolume;

/// <summary>One WebSocket session to ws://host:port/api/v1. Raises every message as raw text on a background thread
/// and tells its owner when the socket dies. Reconnecting is the owner's job (<see cref="ResolumeConnection"/>).</summary>
public sealed class ResolumeSocket : IDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly TaskCompletionSource<string> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>One whole message as UTF-8 JSON bytes, plus a Stopwatch timestamp taken the moment it was received.
    /// The bytes are only valid during the call: the buffer is reused for the next message, so handlers parse it there
    /// and then (no string copy, which matters for the position stream and for multi-megabyte compositions).</summary>
    public event Action<ReadOnlyMemory<byte>, long>? RawMessage;

    /// <summary>Completes with the reason when the socket closes for any reason.</summary>
    public Task<string> Closed => _closed.Task;
    public bool IsOpen => _ws.State == WebSocketState.Open;

    public ResolumeSocket() => _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);

    public async Task ConnectAsync(string host, int port, CancellationToken ct)
    {
        await _ws.ConnectAsync(new Uri($"ws://{host}:{port}/api/v1"), ct).ConfigureAwait(false);
        _ = Task.Run(ReceiveLoop);
    }

    private async Task ReceiveLoop()
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        try
        {
            while (!_cts.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buffer, _cts.Token).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        _closed.TrySetResult($"closed by Resolume ({r.CloseStatus})");
                        return;
                    }
                    ms.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);

                var stamp = System.Diagnostics.Stopwatch.GetTimestamp();
                RawMessage?.Invoke(new ReadOnlyMemory<byte>(ms.GetBuffer(), 0, (int)ms.Length), stamp);
                // Don't hold on to a one-off large buffer (a big composition) for the rest of the session.
                if (ms.Capacity > 4 * 1024 * 1024) { ms.SetLength(0); ms.Capacity = 64 * 1024; }
            }
            _closed.TrySetResult("socket no longer open");
        }
        catch (OperationCanceledException) { _closed.TrySetResult("stopped"); }
        catch (Exception ex) { _closed.TrySetResult(ex.Message); }
    }

    public async Task SendAsync(string json)
    {
        if (_ws.State != WebSocketState.Open) throw new InvalidOperationException("Not connected");
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendGate.WaitAsync().ConfigureAwait(false);
        try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false); }
        finally { _sendGate.Release(); }
    }

    public Task SubscribeAsync(string path) => SendAsync(JsonSerializer.Serialize(new { action = "subscribe", parameter = path }));
    public Task UnsubscribeAsync(string path) => SendAsync(JsonSerializer.Serialize(new { action = "unsubscribe", parameter = path }));
    public Task GetAsync(string path) => SendAsync(JsonSerializer.Serialize(new { action = "get", parameter = path }));
    public Task SetAsync(string path, object value) => SendAsync(JsonSerializer.Serialize(new { action = "set", parameter = path, value }));
    public Task TriggerAsync(string path) => SendAsync(JsonSerializer.Serialize(new { action = "trigger", parameter = path }));

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _ws.Abort(); } catch { }
        _ws.Dispose();
        _closed.TrySetResult("disposed");
    }
}
