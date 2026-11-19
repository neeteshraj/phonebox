using System.Text.Json;
using Microsoft.AspNetCore.Http;
using PhoneBox.Agent;
using Xunit;
using Xunit.Abstractions;

namespace PhoneBox.Agent.Tests;

/// <summary>Window switcher, clipboard, power, DDC and audio outputs (/api/system/*). Parsing runs on recorded output;
/// native checks are read-only and do nothing off macOS (nothing here locks, sleeps, switches or writes brightness).</summary>
public class SystemExtrasTests(ITestOutputHelper output)
{
    // Trimmed from `lsappinfo list` / `lsappinfo visibleProcessList` on macOS 27.
    private const string LsList = """
         1) "loginwindow" ASN:0x0-0x2002:
            bundleID="com.apple.loginwindow"
            bundle path="/System/Library/CoreServices/loginwindow.app"
            pid = 418 token=[sess=100021 pid=418 uid:0,0,4294967295 g:0,0 pV:953] type="UIElement" flavor=3 Version="3109" fileType="APPL" creator="lgnw" Arch=ARM64
         3) "universalaccessd" ASN:0x0-0xb00b:
            bundleID=[ NULL ]
            pid = 659 token=[sess=100021 pid=659 uid:501,501,501 g:20,20 pV:1607] !signalled type="BackgroundOnly" flavor=3 Version=[ NULL ]  fileType="????" creator="????" Arch=ARM64
         4) "Code" ASN:0x0-0x10010: (in front)
            bundleID="com.microsoft.VSCode"
            bundle path="/Applications/Visual Studio Code.app"
            executable path="/Applications/Visual Studio Code.app/Contents/MacOS/Code"
            pid = 696 token=[sess=100021 pid=696 uid:501,501,501 g:20,20 pV:1720] type="Foreground" flavor=3 Version="1.146.0" fileType="APPL" creator="????" Arch=ARM64
            parentASN="loginwindow" ASN:0x0-0x2002:
         6) "iTerm2" ASN:0x0-0x14014:
            bundleID="com.googlecode.iterm2"
            bundle path="/Applications/iTerm.app"
            pid = 704 token=[sess=100021 pid=704 uid:501,501,501 g:20,20 pV:1753] type="Foreground" flavor=3 Version="3.7.7" fileType="APPL" creator="ITRM" Arch=ARM64
        12) "Finder" ASN:0x0-0x1c01c:
            bundleID="com.apple.finder"
            bundle path="/System/Library/CoreServices/Finder.app"
            pid = 718 token=[sess=100021 pid=718 uid:501,501,501 g:20,20 pV:1786] type="Foreground" flavor=3 Version="1865" fileType="FNDR" creator="MACS" Arch=ARM64
        36) "QuickTime Player" ASN:0x0-0x510510:
            bundleID="com.apple.QuickTimePlayerX"
            bundle path="/System/Applications/QuickTime Player.app"
            pid = 9001 token=[sess=100021 pid=9001 uid:501,501,501 g:20,20 pV:1] type="Foreground" flavor=3 Version="1" fileType="APPL" Arch=ARM64
        """;
    // QuickTime Player is hidden (absent); Finder is in front of iTerm2.
    private const string LsVisible = "ASN:0x0-0x10010-\"Code\": ASN:0x0-0x1c01c-\"Finder\": ASN:0x0-0x14014-\"iTerm2\": ASN:0x0-0x3623620-\"Tailscale\": ";

    [Fact]
    public void LsappinfoListsDockAppsFrontmostFirst()
    {
        var apps = MacSystem.ParseLsappinfo(LsList, LsVisible);
        Assert.Equal(["com.microsoft.VSCode", "com.apple.finder", "com.googlecode.iterm2", "com.apple.QuickTimePlayerX"], apps.Select(a => a.App.BundleId));
        Assert.Equal(new RunningApp("com.microsoft.VSCode", "Code", 696, true, false), apps[0].App);
        Assert.Equal("/Applications/Visual Studio Code.app", apps[0].Path);
        Assert.True(apps[^1].App.Hidden);
        Assert.False(apps[1].App.Active);
        Assert.Empty(MacSystem.ParseLsappinfo("garbage\n", ""));
    }

    [Fact]
    public void AppJsonMatchesContract() =>
        Assert.Equal("""{"bundleId":"com.microsoft.VSCode","name":"Code","pid":123,"active":true,"hidden":false}""",
            JsonSerializer.Serialize(new RunningApp("com.microsoft.VSCode", "Code", 123, true, false), JsonSerializerOptions.Web));

    [Theory]
    [InlineData("com.microsoft.VSCode", true)]
    [InlineData("org.mozilla.firefox-esr_2", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("a\" || kMDItemFSName == \"*", false)]
    [InlineData("com.x; rm -rf ~", false)]
    [InlineData(".hidden", false)]
    public void BundleIdsAreValidatedBeforeReachingTools(string? id, bool ok) => Assert.Equal(ok, MacSystem.ValidBundleId(id));

    [Fact]
    public void WindowScriptsTakeValuesAsArguments()
    {
        var probe = MacSystem.WindowProbeArgs("com.evil\" & do shell script \"x", 4);
        Assert.Equal(["--", "com.evil\" & do shell script \"x", "4"], probe[^3..]);
        Assert.DoesNotContain(probe[..^3], a => a.Contains("com.evil"));
        Assert.Equal(["--", "com.apple.finder", "10", "-20", "300", "400"], MacSystem.WindowMoveArgs("com.apple.finder", (10, -20, 300, 400))[^6..]);
    }

    [Fact]
    public void WindowIsShrunkToFitAndCentred()
    {
        Assert.Equal((1800 + 1520, 400, 800, 1360), MacSystem.FitCentered((1800, 0, 3840, 2160), 800, 1360));
        Assert.Equal((0, 39, 1800, 1130), MacSystem.FitCentered((0, 39, 1800, 1130), 2500, 1600)); // bigger than the display
        Assert.Equal(((1800, 0, 3840, 2160), 1200, 800), MacSystem.ParseWindowProbe("1800 0 3840 2160 1200 800"));
        Assert.Equal(((0, -991, 10, 10), 5, 5), MacSystem.ParseWindowProbe("0 -991 10 10 5 5"));
        Assert.Null(MacSystem.ParseWindowProbe(""));
        Assert.Null(MacSystem.ParseWindowProbe("1 2 3"));
        Assert.Null(MacSystem.ParseWindowProbe("0 0 0 0 10 10"));
    }

    // ---------- DDC ----------

    private const string M1ddcList = "[1] Unknown Display (37D8832A-2D66-02CA-B9F7-8F30A301B230)\n[2] LG ULTRAFINE (DEC69F1C-04E7-48A9-8061-618E5D01C430)\n";

    [Fact]
    public void M1ddcListIsParsed() =>
        Assert.Equal([(1, "Unknown Display"), (2, "LG ULTRAFINE")], MacSystem.ParseM1ddcList(M1ddcList));

    [Theory]
    [InlineData("0\n", "0\n", null)]     // recorded from the LG UltraFine: a failed read prints 0
    [InlineData("0", "100", null)]     // also recorded from the LG, between two 30/100 reads: 0 is never trusted
    [InlineData("30\n", "100\n", 30)]  // the LG's real answer
    [InlineData("50", "100", 50)]
    [InlineData("30", "60", 50)]
    [InlineData("120", "100", null)]   // value above max
    [InlineData("-5", "100", null)]
    [InlineData(null, "100", null)]    // m1ddc failed
    [InlineData("Could not find a suitable external display.", "100", null)]
    public void DdcIsTrustedOnlyWithSaneReadings(string? value, string? max, int? percent) =>
        Assert.Equal(percent, MacSystem.DdcLevel(value, max)?.Percent);

    [Fact]
    public void DdcDisplaysMapByNameThenOrder()
    {
        var ddc = MacSystem.ParseM1ddcList(M1ddcList);
        Assert.Equal(new Dictionary<uint, int> { [4] = 2 }, MacSystem.MapDdc([(4, "lg ultrafine")], ddc));
        // a name system_profiler did not give: leftovers differ in count (1 vs 2), so no guess
        Assert.Empty(MacSystem.MapDdc([(4, "Display 2")], ddc));
        Assert.Equal(new Dictionary<uint, int> { [7] = 1, [9] = 3 },
            MacSystem.MapDdc([(7, "Display 1"), (9, "Display 2")], [(1, "A"), (3, "B")]));
    }

    // ---------- power / clipboard ----------

    [Fact]
    public void FocusShortcutsNeedBothExactNames()
    {
        Assert.True(MacSystem.HasFocusShortcuts("NFC\nPhoneBox Focus On\nPhoneBox Focus Off\n"));
        Assert.False(MacSystem.HasFocusShortcuts("PhoneBox Focus On\nphonebox focus off\n"));
        Assert.False(MacSystem.HasFocusShortcuts("PhoneBox Focus On 2\nPhoneBox Focus Off\n"));
        Assert.False(MacSystem.HasFocusShortcuts(null));
    }

    private static int Status(IResult r) => ((IStatusCodeHttpResult)r).StatusCode ?? 200;

    [Fact]
    public void BadArgumentsAreRejectedBeforeTouchingTheSystem()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Equal(400, Status(SystemControls.Caffeinate(null)));
        Assert.Equal(400, Status(SystemControls.Focus(null)));
        Assert.Equal(400, Status(SystemControls.SetClipboard(null)));
        Assert.Equal(400, Status(SystemControls.SetClipboard(new string('x', MacSystem.MaxClipboard + 1))));
        Assert.Equal(404, Status(SystemControls.Activate("bad id")));
        Assert.Equal(404, Status(SystemControls.Activate("com.example.not.running.phonebox")));
        Assert.Equal(404, Status(SystemControls.Move("com.example.not.running.phonebox", "1", null!)));
        Assert.Equal(404, Status(SystemControls.AudioOutput("no-such-device-uid")));
        Assert.Equal(404, Status(SystemControls.AppIcon("a\"b", new DefaultHttpContext().Response)));
    }

    [Theory]
    [InlineData(0x626c746eu, "builtin")]
    [InlineData(0x75736220u, "usb")]
    [InlineData(0x626c7565u, "bluetooth")]
    [InlineData(0x626c6561u, "bluetooth")]
    [InlineData(0x68646d69u, "hdmi")]
    [InlineData(0x64707274u, "displayport")]
    [InlineData(0x61697270u, "airplay")]
    [InlineData(0x76697274u, "virtual")]
    [InlineData(0x67727570u, "virtual")]
    [InlineData(0x7468756eu, "other")] // thunderbolt
    [InlineData(0u, "other")]
    public void TransportTypesMapToContractNames(uint fourcc, string name) => Assert.Equal(name, MacAudioProbe.TransportName(fourcc));

    [Theory]
    [InlineData("/api/system/apps")]
    [InlineData("/api/system/apps/icon")]
    [InlineData("/api/system/apps/move")]
    [InlineData("/api/system/clipboard")]
    [InlineData("/api/system/lock")]
    [InlineData("/api/system/audio/output")]
    public void PhoneMayCallTheNewEndpointsOverWifi(string path) => Assert.True(Lan.AllowedOverLan(new PathString(path)));

    // ---------- read-only checks against this Mac ----------

    [Fact]
    public void MacReadsRealAppsAudioAndLockSymbol()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var apps = MacSystem.RunningApps();
        output.WriteLine(JsonSerializer.Serialize(apps, JsonSerializerOptions.Web));
        Assert.NotEmpty(apps);
        Assert.Contains(apps, a => a.BundleId == "com.apple.finder");

        var outputs = MacAudioProbe.Outputs();
        output.WriteLine(JsonSerializer.Serialize(new { outputs }, JsonSerializerOptions.Web));
        Assert.NotEmpty(outputs);
        Assert.Single(outputs, o => o.Current);

        Assert.NotEqual(IntPtr.Zero, MacSystem.LockSymbol()); // resolved, never called

        var icon = MacSystem.AppIcon("com.apple.finder");
        Assert.NotNull(icon);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], icon![..4]);
    }
}
