using System.Diagnostics;
using CuepointDeck.Core.Media;
using Xunit;

namespace CuepointDeck.Core.Tests;

public class ChildProcessJobTests
{
    [Fact]
    public void Helper_processes_join_the_job_that_ends_them_with_the_app()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 5 127.0.0.1 > nul") { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            Assert.False(ChildProcessJob.Contains(p));
            ChildProcessJob.Add(p);
            Assert.True(ChildProcessJob.Contains(p));
        }
        finally { try { p.Kill(true); } catch { } }
    }
}
