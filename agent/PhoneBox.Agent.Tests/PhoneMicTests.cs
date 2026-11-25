using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>The phone mic: scrcpy-server 5.0.1's audio framing, and the buffer that keeps latency from creeping up.</summary>
public sealed class PhoneMicTests
{
    private static byte[] Meta(string device, uint codec)
    {
        var meta = new byte[ScrcpyAudio.MetaLength];
        Encoding.UTF8.GetBytes(device).CopyTo(meta, 0);
        BinaryPrimitives.WriteUInt32BigEndian(meta.AsSpan(64), codec);
        return meta;
    }

    private static byte[] Header(long ptsAndFlags, uint length)
    {
        var h = new byte[12];
        BinaryPrimitives.WriteInt64BigEndian(h, ptsAndFlags);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(8), length);
        return h;
    }

    [Fact]
    public void TheMetaGivesTheDeviceNameAndTheRawCodec()
    {
        // as read from the Galaxy A71: "SM-A715F", then 00 72 61 77
        var (device, codec) = ScrcpyAudio.ParseMeta(Meta("SM-A715F", 0x00726177));
        Assert.Equal("SM-A715F", device);
        Assert.Equal(ScrcpyAudio.CodecRaw, codec);
        Assert.Null(ScrcpyAudio.CodecProblem(codec));
        Assert.Equal("\0raw"u8.ToArray(), Meta("x", ScrcpyAudio.CodecRaw)[64..]);
    }

    [Fact]
    public void ADisabledOrBrokenStreamIsAProblem()
    {
        Assert.NotNull(ScrcpyAudio.CodecProblem(0));
        Assert.NotNull(ScrcpyAudio.CodecProblem(1));
        Assert.NotNull(ScrcpyAudio.CodecProblem(0x6f707573)); // "opus"
    }

    [Fact]
    public void PacketHeadersAreBigEndianWithFlagsOnTop()
    {
        var media = ScrcpyAudio.ParseHeader(Header(425575251335, 4096));
        Assert.Equal(new ScrcpyAudio.PacketHeader(425575251335, false, 4096), media);
        var config = ScrcpyAudio.ParseHeader(Header(1L << 62, 19));
        Assert.True(config.Config);
        Assert.Equal(19, config.Length);
        // a key-frame flag (bit 61) is not part of the timestamp
        Assert.Equal(1000, ScrcpyAudio.ParseHeader(Header((1L << 61) | 1000, 4)).Pts);
    }

    [Fact]
    public void AnAbsurdPacketLengthEndsTheStream() =>
        Assert.Throws<InvalidDataException>(() => ScrcpyAudio.ParseHeader(Header(0, uint.MaxValue)));

    [Fact]
    public void TheServerCommandTakesOnlyKnownSources()
    {
        var cmd = ScrcpyAudio.ServerCommand(0x1234abcd, "mic-camcorder");
        Assert.Contains("CLASSPATH=/data/local/tmp/phonebox-mic.jar app_process / com.genymobile.scrcpy.Server 5.0.1 scid=1234abcd", cmd);
        Assert.Contains("audio_source=mic-camcorder audio_codec=raw tunnel_forward=true", cmd);
        Assert.Contains("video=false control=false", cmd);
        Assert.Contains("cleanup=false", cmd); // with cleanup the server deletes its own jar and the next reconnect fails
        Assert.Equal("scrcpy_1234abcd", ScrcpyAudio.SocketName(0x1234abcd));
        Assert.Equal("scrcpy_0000000f", ScrcpyAudio.SocketName(15));
        // it goes into an adb shell line: anything else could run commands on the phone
        Assert.Throws<ArgumentException>(() => ScrcpyAudio.ServerCommand(1, "mic; reboot"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScrcpyAudio.ServerCommand(-1, "mic"));
    }

    private static byte[] Tone(int ms, short value = 1000)
    {
        var b = new byte[ms * ScrcpyAudio.BytesPerMs];
        for (var i = 0; i < b.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(i), value);
        return b;
    }

    [Fact]
    public void PlaybackWaitsForTheTargetThenPlaysWhatArrived()
    {
        var buf = new PcmJitterBuffer(targetMs: 50, maxMs: 150);
        var out1 = new byte[10 * ScrcpyAudio.BytesPerMs];
        buf.Write(Tone(20));
        buf.Read(out1);
        Assert.All(out1, b => Assert.Equal(0, b)); // still gathering: silence, nothing consumed
        Assert.Equal(20, buf.BufferedMs);
        buf.Write(Tone(40));
        buf.Read(out1);
        Assert.Equal(1000, BinaryPrimitives.ReadInt16LittleEndian(out1));
        Assert.Equal(50, buf.BufferedMs);
    }

    [Fact]
    public void ARunDryPlaysSilenceAndGathersAgain()
    {
        var buf = new PcmJitterBuffer(50, 150);
        buf.Write(Tone(60));
        var out1 = new byte[80 * ScrcpyAudio.BytesPerMs];
        buf.Read(out1);
        Assert.Equal(1000, BinaryPrimitives.ReadInt16LittleEndian(out1));
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(out1.AsSpan(70 * ScrcpyAudio.BytesPerMs)));
        Assert.Equal(1, buf.Underruns);
        buf.Write(Tone(30));
        var out2 = new byte[10 * ScrcpyAudio.BytesPerMs];
        buf.Read(out2);
        Assert.All(out2, b => Assert.Equal(0, b)); // below the target again: wait
    }

    [Fact]
    public void LatencyNeverPilesUpPastTheCeiling()
    {
        // the phone's clock a little fast for hours: more arrives than is played
        var buf = new PcmJitterBuffer(50, 150);
        var play = new byte[20 * ScrcpyAudio.BytesPerMs];
        for (var i = 0; i < 10_000; i++)
        {
            buf.Write(Tone(21));
            buf.Read(play);
            Assert.True(buf.BufferedMs <= 150);
        }
        Assert.True(buf.DroppedBytes > 0);
        Assert.Equal(0, buf.BufferedBytes % ScrcpyAudio.BytesPerFrame);
    }

    [Fact]
    public void WritesKeepWholeFrames()
    {
        var buf = new PcmJitterBuffer(10, 150);
        buf.Write(new byte[7]);
        Assert.Equal(4, buf.BufferedBytes);
    }

    [Fact]
    public void MutedPlaysSilenceAndKeepsNothing()
    {
        var buf = new PcmJitterBuffer(10, 150) { Muted = true };
        buf.Write(Tone(50));
        var out1 = new byte[20 * ScrcpyAudio.BytesPerMs];
        buf.Read(out1);
        Assert.All(out1, b => Assert.Equal(0, b));
        Assert.Equal(0, buf.BufferedBytes);
        buf.Muted = false;
        buf.Write(Tone(20));
        buf.Read(out1);
        Assert.Equal(1000, BinaryPrimitives.ReadInt16LittleEndian(out1));
    }

    [Fact]
    public void GainScalesAndClips()
    {
        var pcm = Tone(1, 20000);
        var peak = PcmJitterBuffer.ApplyGain(pcm, 2);
        Assert.Equal(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(pcm));
        Assert.True(peak > 0.99f);
        var quiet = Tone(1, -1000);
        PcmJitterBuffer.ApplyGain(quiet, 0.5);
        Assert.Equal(-500, BinaryPrimitives.ReadInt16LittleEndian(quiet));
    }

    [Fact]
    public void SettingsAreClampedToSaneValues()
    {
        var buf = new PcmJitterBuffer();
        buf.Configure(targetMs: 1, maxMs: 1, gain: double.NaN);
        Assert.Equal(10 * ScrcpyAudio.BytesPerMs, buf.TargetBytes);
        Assert.Equal(30 * ScrcpyAudio.BytesPerMs, buf.MaxBytes);
    }

    [Fact]
    public void TheRingAlwaysHoldsTheTarget()
    {
        // a capacity smaller than the target used to leave playback waiting for ever, playing silence
        var buf = new PcmJitterBuffer(targetMs: 150, maxMs: 200, capacityMs: 100);
        buf.Write(Enumerable.Repeat((byte)1, 160 * ScrcpyAudio.BytesPerMs).ToArray());
        var played = new byte[10 * ScrcpyAudio.BytesPerMs];
        buf.Read(played);
        Assert.All(played, b => Assert.Equal(1, b));
    }

    [Fact]
    public void ARefusalIsRetriedSoonOnceThenOnceAMinute()
    {
        // codec 0 also means "the source is busy for a moment": the first retry comes after 5 s
        Assert.Equal(PhoneMic.BusyRetryMs, PhoneMic.RefusedWaitMs(1));
        Assert.Equal(PhoneMic.UnsupportedRetryMs, PhoneMic.RefusedWaitMs(2));
        Assert.Equal(PhoneMic.UnsupportedRetryMs, PhoneMic.RefusedWaitMs(9));
    }

    [Fact]
    public void OnlyAStreamThatRanForAWhileStartsTheRetriesAfresh()
    {
        Assert.Equal(0, PhoneMic.NextFailures(5, PhoneMic.MinLiveMs));       // played long enough: a fresh loss
        Assert.Equal(6, PhoneMic.NextFailures(5, 300));                      // header then death: still failing
        Assert.Equal(1, PhoneMic.NextFailures(0, -1));                       // never played
        Assert.Equal(50, PhoneMic.RetryDelayMs(0));
        Assert.Equal(100, PhoneMic.RetryDelayMs(6));
        Assert.Equal(5000, PhoneMic.RetryDelayMs(40));
        // a server that keeps sending its header and dying backs off instead of restarting every 50 ms
        var failures = 0;
        for (var i = 0; i < 20; i++) failures = PhoneMic.NextFailures(failures, 100);
        Assert.Equal(5000, PhoneMic.RetryDelayMs(failures));
    }

    private sealed class Temp : IDisposable
    {
        public readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"phonebox-mic-{Guid.NewGuid():N}.json");
        public Temp(string json) => File.WriteAllText(Path, json);
        public void Dispose() { try { File.Delete(Path); } catch { } }
    }

    [Fact]
    public void TheButtonTogglesMuteAndSaysSo()
    {
        using var t = new Temp("""{ "phoneMic": { "enabled": true, "startMuted": true } }""");
        var mic = new PhoneMic(new ConfigStore(t.Path), null, NullLogger<PhoneMic>.Instance, null);
        Assert.True(mic.Muted);
        Assert.Equal("Off", mic.StateText); // the service has not run: no stream yet
        Assert.False(mic.Toggle());
        Assert.True(mic.Toggle());
    }

    [Fact]
    public void OnlyTheOfficialServerFileIsAccepted()
    {
        using var t = new Temp("not the server");
        Assert.False(PhoneMic.IsOfficialServer(t.Path));
    }

    [Fact]
    public void TheHeldPeakKeepsTheLoudestOfTheLastSecond()
    {
        long now = 10_000;
        var buf = new PcmJitterBuffer(10, 150) { Clock = () => now };
        var out1 = new byte[10 * ScrcpyAudio.BytesPerMs];
        buf.Write(Tone(20, 16000)); buf.Read(out1);
        now += 300; buf.Write(Tone(20, 100)); buf.Read(out1);
        Assert.True(buf.HeldPeak > 0.4f);            // the loud read is still held
        now += 1500; buf.Read(out1);                 // the next second: still held from the one before
        Assert.True(buf.HeldPeak > 0.4f);
        now += 1100; buf.Read(out1);                 // two seconds on, it has gone
        Assert.True(buf.HeldPeak < 0.01f);
        now += 5000;
        Assert.Equal(0, buf.HeldPeak);               // nothing read for a while (no output device): no level
    }

    private sealed class NoOutput : IPcmOutput
    {
        public string DeviceName => "test";
        public bool Faulted => false;
        public void Dispose() { }
    }

    [Fact]
    public void ThePhoneSeesTheMicOnlyWhileItIsOn()
    {
        using var off = new Temp("""{ "phoneMic": { "enabled": false } }""");
        Assert.Null(new PhoneMic(new ConfigStore(off.Path), null, NullLogger<PhoneMic>.Instance, (_, _, _) => new NoOutput()).PhoneState());
        using var on = new Temp("""{ "phoneMic": { "enabled": true } }""");
        var mic = new PhoneMic(new ConfigStore(on.Path), null, NullLogger<PhoneMic>.Instance, (_, _, _) => new NoOutput());
        Assert.True(mic.Enabled);
        Assert.Equal(-90, mic.LevelDb); // nothing played yet
        var json = System.Text.Json.JsonSerializer.Serialize(mic.PhoneState());
        Assert.Contains("\"state\":\"off\"", json);
        Assert.Contains("\"live\":false", json);
        // no output device on this platform: no phone mic at all
        Assert.False(new PhoneMic(new ConfigStore(on.Path), null, NullLogger<PhoneMic>.Instance, null).Enabled);
    }

    [Fact]
    public void ShortcutCommandsActOnTheMic()
    {
        using var on = new Temp("""{ "phoneMic": { "enabled": true }, "shortcuts": [ { "id": "m", "label": "Mic", "cmd": "builtin:phone-mic" } ] }""");
        var store = new ConfigStore(on.Path);
        var mic = new PhoneMic(store, null, NullLogger<PhoneMic>.Instance, (_, _, _) => new NoOutput());
        var before = PhoneDevices.Mic;
        PhoneDevices.Mic = mic;
        try
        {
            Assert.Null(Shortcuts.Run(store.Get(), "m", NullLogger.Instance));
            Assert.True(mic.Muted);
            Assert.Null(Shortcuts.Run(store.Get(), "m", NullLogger.Instance));
            Assert.False(mic.Muted);
            PhoneDevices.Run(PhoneDevices.MicCmd, "mute");
            Assert.True(mic.Muted);
            PhoneDevices.Run(PhoneDevices.MicCmd, "unmute");
            Assert.False(mic.Muted);
            // the key shows on only while audio flows, and it is a toggle key for the phone
            Assert.False(PhoneDevices.MicOn);
            Assert.Contains("\"toggle\":true", System.Text.Json.JsonSerializer.Serialize(store.Get().ToPublic()));
        }
        finally { PhoneDevices.Mic = before; }
    }

    [Fact]
    public void CommandsForWhatIsOffSayWhy()
    {
        var mic = PhoneDevices.Mic;
        PhoneDevices.Mic = null;
        try
        {
            Assert.Throws<InvalidOperationException>(() => PhoneDevices.Run(PhoneDevices.MicCmd, null));
            Assert.Null(PhoneDevices.MicOn);
            Assert.True(PhoneDevices.IsCommand("builtin:phone-mic"));
            Assert.False(PhoneDevices.IsCommand("builtin:mute"));
        }
        finally { PhoneDevices.Mic = mic; }
    }
}
