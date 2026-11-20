using System.Text.Json;
using Microsoft.AspNetCore.Http;
using PhoneBox.Agent;
using Xunit;

namespace PhoneBox.Agent.Tests;

public class RemoteDesktopTests
{
    // `ffmpeg -f avfoundation -list_devices true -i ""` on a MacBook Pro (macOS 27) with an iPhone nearby and one display
    private const string DeviceList = """
        [AVFoundation indev @ 0x7adec10140] AVFoundation video devices:
        [AVFoundation indev @ 0x7adec10140] [0] MacBook Pro Camera
        [AVFoundation indev @ 0x7adec10140] [1] Someone’s iPhone Camera
        [AVFoundation indev @ 0x7adec10140] [2] Someone’s iPhone Desk View Camera
        [AVFoundation indev @ 0x7adec10140] [3] MacBook Pro Desk View Camera
        [AVFoundation indev @ 0x7adec10140] [4] Capture screen 0
        [AVFoundation indev @ 0x7adec10140] [5] Capture screen 1
        [AVFoundation indev @ 0x7adec10140] AVFoundation audio devices:
        [AVFoundation indev @ 0x7adec10140] [0] MacBook Pro Microphone
        [in#0 @ 0x7adec10000] Error opening input: Input/output error
        """;

    [Fact]
    public void ScreensParseFromTheDeviceList()
    {
        var map = RemoteDesktop.ParseScreens(DeviceList);
        Assert.Equal(new Dictionary<int, int> { [0] = 4, [1] = 5 }, map);
        Assert.Empty(RemoteDesktop.ParseScreens(""));
    }

    [Fact]
    public void DisplaysMapToScreensInActiveListOrder()
    {
        var screens = RemoteDesktop.ParseScreens(DeviceList);
        uint[] active = [1, 3]; // CGGetActiveDisplayList: the main display first
        Assert.Equal(4, RemoteDesktop.ScreenDevice(active, screens, 1));
        Assert.Equal(5, RemoteDesktop.ScreenDevice(active, screens, 3));
        Assert.Null(RemoteDesktop.ScreenDevice(active, screens, 9));
        Assert.Null(RemoteDesktop.ScreenDevice([1, 3, 7], screens, 7)); // ffmpeg doesn't list a third screen
    }

    [Fact]
    public void NormalisedPointsBecomeGlobalPoints()
    {
        Assert.Equal((900.0, 584.5), RemoteDesktop.ToGlobal((0, 0, 1800, 1169), 0.5, 0.5));
        Assert.Equal((0.0, 0.0), RemoteDesktop.ToGlobal((0, 0, 1800, 1169), 0, 0));
        Assert.Equal((1799.0, 1168.0), RemoteDesktop.ToGlobal((0, 0, 1800, 1169), 1, 1)); // stays on this display
        // an external display to the left of and above the main one
        Assert.Equal((-1920.0, -1080.0), RemoteDesktop.ToGlobal((-1920, -1080, 1920, 1080), 0, 0));
        Assert.Equal((-960.0, -540.0), RemoteDesktop.ToGlobal((-1920, -1080, 1920, 1080), 0.5, 0.5));
        Assert.Equal((-1.0, -1.0), RemoteDesktop.ToGlobal((-1920, -1080, 1920, 1080), 1, 1));
    }

    [Fact]
    public void FfmpegFramesSplitAcrossReads()
    {
        byte[] a = [0xFF, 0xD8, 1, 2, 0xFF, 0x00, 3, 0xFF, 0xD9], b = [0xFF, 0xD8, 9, 0xFF, 0xD9];
        var stream = a.Concat(b).ToArray();
        var got = new List<byte[]>();
        var split = new JpegSplitter(got.Add);
        int[] cuts = [0, 1, 5, 8, 10, 13, stream.Length]; // an FF alone, a marker torn in two, a frame across reads
        for (var i = 1; i < cuts.Length; i++) split.Push(stream.AsSpan(cuts[i - 1], cuts[i] - cuts[i - 1]));
        Assert.Equal([a, b], got);
    }

    [Theory]
    [InlineData("esc", 53, false)]
    [InlineData("tab", 48, false)]
    [InlineData("enter", 36, false)]
    [InlineData("backspace", 51, false)]
    [InlineData("delete", 117, false)]
    [InlineData("space", 49, false)]
    [InlineData("up", 126, false)]
    [InlineData("down", 125, false)]
    [InlineData("left", 123, false)]
    [InlineData("right", 124, false)]
    [InlineData("home", 115, false)]
    [InlineData("end", 119, false)]
    [InlineData("pageup", 116, false)]
    [InlineData("pagedown", 121, false)]
    [InlineData("f1", 122, false)]
    [InlineData("F12", 111, false)]
    [InlineData("a", 0, false)]
    [InlineData("c", 8, false)]
    [InlineData("C", 8, true)]
    [InlineData("z", 6, false)]
    [InlineData("1", 18, false)]
    [InlineData("0", 29, false)]
    [InlineData("!", 18, true)]
    [InlineData("/", 44, false)]
    [InlineData("?", 44, true)]
    [InlineData("`", 50, false)]
    [InlineData(" ", 49, false)]
    public void KeyNamesMapToMacKeyCodes(string key, int code, bool shift) =>
        Assert.Equal(((ushort)code, shift), RemoteDesktop.KeyCode(key));

    [Theory]
    [InlineData("f13")]
    [InlineData("é")]
    [InlineData("playpause")]
    [InlineData("nope")]
    [InlineData("")]
    public void UnknownKeysHaveNoCode(string key) => Assert.Null(RemoteDesktop.KeyCode(key));

    [Fact]
    public void ModifiersBecomeFlags()
    {
        Assert.Equal(0UL, RemoteDesktop.ModFlags(null));
        Assert.Equal(MacSystem.CmdFlag | MacSystem.ShiftFlag, RemoteDesktop.ModFlags("cmd, shift"));
        Assert.Equal(MacSystem.AltFlag | MacSystem.CtrlFlag, RemoteDesktop.ModFlags("alt,ctrl"));
        Assert.Equal(0x100000UL, MacSystem.CmdFlag);
        Assert.Null(RemoteDesktop.ModFlags("cmd,hyper"));
    }

    [Fact]
    public void TextIsChunkedWithoutSplittingSurrogatesAndReturnsForLineBreaks()
    {
        Assert.Equal(["hi", "\n", "there"], RemoteDesktop.TextChunks("hi\r\nthere"));
        var long_ = new string('x', 45);
        Assert.Equal([new string('x', 20), new string('x', 20), "xxxxx"], RemoteDesktop.TextChunks(long_));
        var emoji = new string('x', 19) + "😀"; // the pair would straddle 20
        Assert.Equal([new string('x', 19), "😀"], RemoteDesktop.TextChunks(emoji));
        Assert.All(RemoteDesktop.TextChunks(new string('y', 2000)), c => Assert.True(c.Length <= 20));
    }

    private static Func<string, string?> Q(string query)
    {
        var parsed = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query);
        return k => parsed.TryGetValue(k, out var v) ? v.ToString() : null;
    }

    private static RemoteInput? Parse(string query) => RemoteDesktop.ParseInput(Q(query), id => id is 1 or 4).Input;

    [Fact]
    public void InputArgumentsAreValidated()
    {
        Assert.Equal(new RemoteInput("move", 1, 0.25, 0.75, true), Parse("type=move&display=1&x=0.25&y=0.75"));
        Assert.Equal(1, Parse("type=down&display=4&x=0&y=1&button=right")!.Button);
        Assert.Equal(new RemoteInput("scroll", Dx: -3, Dy: 120), Parse("type=scroll&dx=-3&dy=120"));
        Assert.True(Parse("type=scroll&dy=5&display=1&x=0.5&y=0.5")!.HasPoint);
        Assert.Equal(new RemoteInput("key", Key: 8, Flags: MacSystem.CmdFlag), Parse("type=key&key=c&mods=cmd"));
        Assert.Equal(new RemoteInput("text", Text: "é"), Parse("type=key&key=%C3%A9")); // no US key: typed
        Assert.Equal("hello world", Parse("type=text&text=hello%20world")!.Text);

        Assert.Null(Parse("type=fly"));
        Assert.Null(Parse(""));
        Assert.Null(Parse("type=click&display=2&x=0.5&y=0.5"));     // unknown display
        Assert.Null(Parse("type=click&display=1&x=1.5&y=0.5"));     // out of 0..1
        Assert.Null(Parse("type=click&display=1&x=NaN&y=0.5"));
        Assert.Null(Parse("type=click&display=1&y=0.5"));
        Assert.Null(Parse("type=down&display=1&x=0&y=0&button=middle"));
        Assert.Null(Parse("type=scroll&dy=99999"));
        Assert.Null(Parse("type=key&key=nope"));
        Assert.Null(Parse("type=key&key=c&mods=hyper"));
        Assert.Null(Parse("type=key&key=%C3%A9&mods=cmd"));        // typed characters can't carry modifiers
        Assert.Null(Parse("type=text"));
        Assert.Null(Parse("type=text&text=" + new string('a', 2001)));
        Assert.NotNull(Parse("type=text&text=" + new string('a', 2000)));
    }

    private static string Error(object body) => JsonSerializer.SerializeToElement(body).GetProperty("error").GetString()!;

    [Fact]
    public void InputIsRefusedWhileOffOrUntrusted()
    {
        var ran = new List<RemoteInput>();
        var (status, body) = RemoteDesktop.Input(false, () => true, Q("type=key&key=a"), ran.Add);
        Assert.Equal(403, status);
        Assert.Equal(RemoteDesktop.OffError, Error(body));

        (status, body) = RemoteDesktop.Input(true, () => false, Q("type=key&key=a"), ran.Add);
        Assert.Equal(403, status);
        Assert.Equal("Allow PhoneBoxAgent in System Settings › Privacy & Security › Accessibility", Error(body));

        (status, _) = RemoteDesktop.Input(true, () => true, Q("type=key&key=nope"), ran.Add);
        Assert.Equal(400, status);
        Assert.Empty(ran);

        (status, body) = RemoteDesktop.Input(true, () => true, Q("type=key&key=a&mods=shift"), ran.Add);
        Assert.Equal(200, status);
        Assert.True(JsonSerializer.SerializeToElement(body).GetProperty("ok").GetBoolean());
        Assert.Equal(new RemoteInput("key", Key: 0, Flags: MacSystem.ShiftFlag), Assert.Single(ran));
    }

    [Fact]
    public void FrameGateChecksOffDisplayAndPermission()
    {
        var asked = false;
        Assert.Equal((403, RemoteDesktop.OffError), RemoteDesktop.FrameGate(false, true, () => asked = true));
        Assert.False(asked); // no permission check (or prompt) while off
        Assert.Equal(404, RemoteDesktop.FrameGate(true, false, () => true).Status);
        Assert.Equal((409, RemoteDesktop.ScreenError), RemoteDesktop.FrameGate(true, true, () => false));
        Assert.Equal((200, (string?)null), RemoteDesktop.FrameGate(true, true, () => true));
    }

    [Fact]
    public void FrameSizeAndQualityAreClampedAndMapped()
    {
        Assert.Equal(1280, RemoteDesktop.ClampWidth(null));
        Assert.Equal(320, RemoteDesktop.ClampWidth(10));
        Assert.Equal(2560, RemoteDesktop.ClampWidth(5000));
        Assert.Equal(60, RemoteDesktop.ClampQuality(null));
        Assert.Equal(2, RemoteDesktop.Qv(90));
        Assert.Equal(11, RemoteDesktop.Qv(60));
        Assert.Equal(20, RemoteDesktop.Qv(30));
        var args = RemoteCapture.Args(4, 1280, 60);
        Assert.Equal("4:none", args[Array.IndexOf(args, "-i") + 1]);
        Assert.Equal("scale='min(1280,iw)':-2", args[Array.IndexOf(args, "-vf") + 1]);
        Assert.Equal("mjpeg", args[Array.IndexOf(args, "-f", Array.IndexOf(args, "-i")) + 1]);
    }

    [Fact]
    public void RemoteDesktopIsOffByDefaultAndReachableOverWifi()
    {
        Assert.False(new AgentConfig().Remote.Enabled);
        Assert.True(Lan.AllowedOverLan("/api/remote/frame"));
        Assert.True(Lan.AllowedOverLan("/api/remote/input"));
        Assert.True(Lan.AllowedOverLan("/api/remote/info"));
    }

    [Fact]
    public void TrackpadWheelDeltaBecomesMacPixels()
    {
        Assert.Equal(40, KeySender.MacScrollPixels(120));
        Assert.Equal(-40, KeySender.MacScrollPixels(-120));
    }
}
