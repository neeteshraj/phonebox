using PhoneBox.Agent;
using Xunit;

public sealed class UnixProcTests
{
    [Theory]
    [InlineData("(WindowServer)", "", "WindowServer")]
    [InlineData("/System/Library/PrivateFrameworks/SkyLight.framework/Resources/WindowServer",
        "/System/Library/PrivateFrameworks/SkyLight.framework/Resources/WindowServer", "WindowServer")]
    [InlineData("Cursor Helper (Renderer)", "", "Cursor Helper (Renderer)")]
    [InlineData("  (launchd)  ", "", "launchd")]
    public void SplitPsComm_KeepsPathsSeparateFromCanonicalNames(string raw, string path, string comm)
    {
        var got = UnixProc.SplitPsComm(raw);
        Assert.Equal(path, got.Path);
        Assert.Equal(comm, got.Comm);
    }

    [Fact]
    public void ParsePsCommLine_ParenthesizedCommandIsCanonicalNameNotPath()
    {
        var row = UnixProc.ParsePsCommLine("  88   0  12345 (WindowServer)");
        Assert.NotNull(row);
        Assert.Equal(88, row.Value.Pid);
        Assert.Equal(0u, row.Value.Uid);
        Assert.Equal(12345, row.Value.RssKb);
        Assert.Equal("", row.Value.Path);
        Assert.Equal("WindowServer", row.Value.Comm);
        Assert.Contains(row.Value.Comm, UnixProc.Protected);
    }
}
