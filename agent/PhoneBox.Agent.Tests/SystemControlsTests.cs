using System.Text.Json;
using Microsoft.AspNetCore.Http;
using PhoneBox.Agent;
using Xunit;
using Xunit.Abstractions;

namespace PhoneBox.Agent.Tests;

public class SystemControlsTests(ITestOutputHelper output)
{
    // Trimmed from `system_profiler SPDisplaysDataType -json` on an M5 Pro MacBook Pro with an LG monitor.
    private const string Profiler = """
        { "SPDisplaysDataType": [ { "_name": "Apple M5 Pro", "spdisplays_ndrvs": [
          { "_name": "Color LCD", "_spdisplays_displayID": "1", "_spdisplays_resolution": "1800 x 1169 @ 120.00Hz",
            "spdisplays_connection_type": "spdisplays_internal" },
          { "_name": "LG ULTRAFINE", "_spdisplays_displayID": "4", "_spdisplays_resolution": "3840 x 2160 @ 60.00Hz" } ] } ] }
        """;

    [Fact]
    public void ProfilerNamesMatchDisplaysById()
    {
        var map = MacSystem.ParseProfiler(Profiler, [(4, false), (1, true)]);
        Assert.Equal(("LG ULTRAFINE", 60), map[4]);
        Assert.Equal(("Color LCD", 120), map[1]);
    }

    [Fact]
    public void ProfilerWithoutIdsMatchesInOrderByKind()
    {
        var json = Profiler.Replace("\"_spdisplays_displayID\": \"1\",", "").Replace("\"_spdisplays_displayID\": \"4\",", "");
        var map = MacSystem.ParseProfiler(json, [(9, false), (2, true), (7, false)]);
        Assert.Equal("LG ULTRAFINE", map[9].Name);
        Assert.Equal("Color LCD", map[2].Name);
        Assert.False(map.ContainsKey(7)); // falls back to "Display N"
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"SPDisplaysDataType\": 3}")]
    public void BadProfilerOutputMapsNothing(string json) => Assert.Empty(MacSystem.ParseProfiler(json, [(1, true)]));

    [Fact]
    public void QueryIsCleanedAndCapped()
    {
        Assert.Equal("", SystemControls.CleanQuery(null));
        Assert.Equal("ab", SystemControls.CleanQuery("a\nb\r"));
        Assert.NotNull(SystemControls.CleanQuery(new string('x', 200)));
        Assert.Null(SystemControls.CleanQuery(new string('x', 201)));
    }

    [Fact]
    public void SpotlightQueryIsAnArgumentNotScript()
    {
        const string evil = "\" & do shell script \"rm -rf ~\" & \"";
        var args = MacSystem.SpotlightArgs(evil);
        Assert.Equal(evil, args[^1]);
        Assert.Equal("--", args[^2]);
        Assert.DoesNotContain(args[..^1], a => a.Contains("rm -rf"));
    }

    private static int Status(IResult r) => ((IStatusCodeHttpResult)r).StatusCode ?? 200;

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(null)]
    public void OutOfRangeLevelsAreRejectedBeforeTouchingHardware(int? value)
    {
        Assert.Equal(400, Status(SystemControls.Brightness("1", value, null!)));
        Assert.Equal(400, Status(SystemControls.Keyboard(value)));
    }

    [Fact]
    public void OverlongSearchIsRejected() => Assert.Equal(400, Status(SystemControls.Search(new string('x', 201))));

    [Theory]
    [InlineData("/api/system/controls")]
    [InlineData("/api/system/display/focus")]
    [InlineData("/api/system/display/brightness")]
    [InlineData("/api/system/keyboard")]
    [InlineData("/api/system/search")]
    public void PhoneMayCallSystemControlsOverWifi(string path) => Assert.True(Lan.AllowedOverLan(new PathString(path)));

    /// <summary>Read-only check against the real hardware; does nothing off macOS.</summary>
    [Fact]
    public void MacSnapshotReadsRealHardware()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var json = JsonSerializer.Serialize(SystemControls.Snapshot(null!), JsonSerializerOptions.Web);
        output.WriteLine(json);
        var displays = MacSystem.Displays();
        Assert.NotEmpty(displays);
        Assert.Single(displays, d => d.Main);
    }
}
