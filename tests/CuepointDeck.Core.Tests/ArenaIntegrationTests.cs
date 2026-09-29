using System.Diagnostics;
using CuepointDeck.Core.Resolume;
using Xunit;
using Xunit.Abstractions;

namespace CuepointDeck.Core.Tests;

/// <summary>Runs only when CUEPOINTDECK_ARENA_TESTS=1 and Arena is installed locally. It launches Arena if needed,
/// connects, kills Arena, and checks the connection comes back on its own after Arena restarts.
/// Do not run this on the show PC during a service.</summary>
public class ArenaIntegrationTests
{
    private readonly ITestOutputHelper _out;
    public ArenaIntegrationTests(ITestOutputHelper output) => _out = output;

    private const string ArenaExe = @"C:\Program Files\Resolume Arena\Arena.exe";
    private static bool Enabled => Environment.GetEnvironmentVariable("CUEPOINTDECK_ARENA_TESTS") == "1" && File.Exists(ArenaExe);

    [Fact]
    public async Task Reconnects_after_arena_is_killed_and_restarted()
    {
        if (!Enabled) { _out.WriteLine("skipped: set CUEPOINTDECK_ARENA_TESTS=1 with Arena installed"); return; }

        await EnsureArenaAsync();

        var states = new List<(ConnectionState State, DateTime At)>();
        int compositions = 0;
        await using var conn = new ResolumeConnection("localhost", 8080);
        conn.StateChanged += s => { lock (states) states.Add((s, DateTime.Now)); _out.WriteLine($"{DateTime.Now:HH:mm:ss.fff} state {s}"); };
        conn.CompositionChanged += c => { Interlocked.Increment(ref compositions); _out.WriteLine($"composition \"{c.Name}\" {c.Layers.Count} layers"); };

        var sw = Stopwatch.StartNew();
        conn.Start();
        await WaitFor(() => conn.State == ConnectionState.Connected, 15_000);
        _out.WriteLine($"connected in {sw.ElapsedMilliseconds} ms: {conn.ProductInfo}");
        Assert.Equal("127.0.0.1", conn.EffectiveHost);
        Assert.NotNull(conn.Composition);
        Assert.Equal(1, compositions);

        // Kill Resolume while connected.
        foreach (var p in Process.GetProcessesByName("Arena")) { p.Kill(); p.WaitForExit(10_000); }
        await WaitFor(() => conn.State != ConnectionState.Connected, 10_000);
        _out.WriteLine("arena killed; state " + conn.State);

        // It must keep retrying quietly, not give up.
        await Task.Delay(5_000);
        Assert.NotEqual(ConnectionState.Connected, conn.State);

        // Restart Arena; the connection must recover without help.
        sw.Restart();
        Process.Start(new ProcessStartInfo(ArenaExe) { UseShellExecute = true });
        await WaitFor(() => conn.State == ConnectionState.Connected && compositions >= 2, 90_000);
        _out.WriteLine($"recovered {sw.ElapsedMilliseconds} ms after relaunch; composition re-read: {compositions >= 2}");
        Assert.NotNull(conn.Composition);

        await conn.StopAsync();
        Assert.Equal(ConnectionState.Disconnected, conn.State);
    }

    private static async Task EnsureArenaAsync()
    {
        if (Process.GetProcessesByName("Arena").Length == 0)
            Process.Start(new ProcessStartInfo(ArenaExe) { UseShellExecute = true });
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.Now.AddSeconds(90);
        while (DateTime.Now < deadline)
        {
            try { await http.GetStringAsync("http://127.0.0.1:8080/api/v1/product"); return; } catch { }
            await Task.Delay(1000);
        }
        throw new TimeoutException("Arena's webserver did not come up");
    }

    private static async Task WaitFor(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.Now > deadline) throw new TimeoutException("condition not met in time");
            await Task.Delay(100);
        }
    }
}
