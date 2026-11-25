using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class ProcessMonitorTests
{
    private static ProcessMonitor NewMonitor() =>
        new(NullLogger<ProcessMonitor>.Instance, new SessionMonitor(NullLogger<SessionMonitor>.Instance));

    private static void SampleTwice(ProcessMonitor mon)
    {
        mon.Sample();
        mon.Sample();
    }

    [Fact]
    public void KernelTaskAndWindowServerAreProtectedFromKill()
    {
        Assert.Contains("kernel_task", UnixProc.Protected);
        Assert.Contains("WindowServer", UnixProc.Protected);

        var mon = NewMonitor();
        var byName = mon.Kill("kernel_task");
        Assert.Equal(0, byName.Killed);
        Assert.Contains("protected", byName.Error, StringComparison.OrdinalIgnoreCase);

        byName = mon.Kill("WindowServer");
        Assert.Equal(0, byName.Killed);
        Assert.Contains("protected", byName.Error, StringComparison.OrdinalIgnoreCase);

        // Request spelling must not bypass Ordinal protected entries (e.g. "Finder").
        byName = mon.Kill("finder");
        Assert.Equal(0, byName.Killed);
        Assert.Contains("protected", byName.Error, StringComparison.OrdinalIgnoreCase);

        var byPid = mon.KillPid(0);
        Assert.Equal(0, byPid.Killed);
        Assert.Contains("protected", byPid.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PID 0", byPid.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void MacOsKernelTaskAppearsInSnapshotWhenEnumerated()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var mon = NewMonitor();
        SampleTwice(mon);
        var groups = mon.Snapshot();
        var kernel = Assert.Single(groups, g => g.Name.Equals("kernel_task", StringComparison.Ordinal));
        Assert.True(kernel.Locked);
        Assert.True(kernel.MemMb > 0); // wired memory via vm_stat

        // GetProcessesByName("kernel_task") misses PID 0 when ProcessName is empty; Detail must still resolve it.
        var detail = mon.Detail("kernel_task");
        var instance = Assert.Single(detail.Instances, i => i.Pid == 0);
        Assert.True(instance.Locked);
        Assert.True(instance.MemMb > 0);
        Assert.True(detail.Locked);
    }

    [Fact]
    public void MacOsWindowServerHasPositiveMemoryWhenRunning()
    {
        if (!OperatingSystem.IsMacOS()) return;
        // Skip only when the OS process is absent; a missing snapshot entry while it runs is a failure.
        if (!WindowServerIsRunning()) return;

        var mon = NewMonitor();
        SampleTwice(mon);
        var windowServer = Assert.Single(mon.Snapshot(),
            g => g.Name.Equals("WindowServer", StringComparison.Ordinal));
        Assert.True(windowServer.MemMb > 0);
    }

    /// <summary>True when a live process is WindowServer by .NET name or <c>ps</c> comm — not via Snapshot().</summary>
    private static bool WindowServerIsRunning()
    {
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.ProcessName.Equals("WindowServer", StringComparison.OrdinalIgnoreCase)) return true;
                    if ((UnixProc.Comm(p.Id) ?? "").Equals("WindowServer", StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { }
            }
        }
        return false;
    }

    [Fact]
    public void MacOsKernelWiredBytes_ReadsVmStat()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.True(UnixProc.KernelWiredBytes() > 0);
    }

    [Fact]
    public void NonMacOsDoesNotListPid0InSnapshot()
    {
        if (OperatingSystem.IsMacOS()) return;

        var mon = NewMonitor();
        SampleTwice(mon);
        Assert.DoesNotContain(mon.Snapshot(), g => g.Name.Equals("Idle", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(mon.Snapshot(), g => g.Name.Equals("kernel_task", StringComparison.OrdinalIgnoreCase));
    }
}
