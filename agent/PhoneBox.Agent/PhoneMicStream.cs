using System.Buffers.Binary;
using System.Text;

namespace PhoneBox.Agent;

/// <summary>
/// The audio-only wire format of scrcpy-server 5.0.1, as the agent starts it for the phone mic (see <see cref="PhoneMic"/>):
/// <c>video=false control=false audio=true audio_codec=raw tunnel_forward=true</c>. Checked against the server's source
/// (DesktopConnection.java, Streamer.java, AudioRawRecorder.java) and against the phone:
/// <list type="number">
/// <item>one dummy byte (0) once the socket is accepted, so a client can tell a live server from adb's empty accept;</item>
/// <item>64 bytes: the device name, UTF-8, NUL-padded;</item>
/// <item>4 bytes big-endian: the codec id, "\0raw" (0x00726177) for raw PCM; 0 means the phone could not capture audio,
///   1 a configuration error;</item>
/// <item>then packets: 8 bytes big-endian PTS with flags in the top bits (bit 62 = config packet), 4 bytes big-endian
///   length, then the payload. Raw payloads are PCM s16le, 48 kHz, stereo.</item>
/// </list>
/// </summary>
public static class ScrcpyAudio
{
    /// <summary>The server version the agent speaks to; the server refuses any other client version.</summary>
    public const string Version = "5.0.1";
    /// <summary>SHA-256 of the official scrcpy-server 5.0.1 (the file in scrcpy-win64-v5.0.1); no other file is pushed.</summary>
    public const string ServerSha256 = "764eb6f79811d5211fe9df341120882ba9994c7a61b897d7bf3fb662e53bc536";
    /// <summary>Where the agent keeps its copy on the phone, apart from the copies scrcpy and other tools push.</summary>
    public const string RemotePath = "/data/local/tmp/phonebox-mic.jar";
    public const uint CodecRaw = 0x00_72_61_77;
    public const int DeviceNameLength = 64;
    public const int MetaLength = DeviceNameLength + 4;
    public const int HeaderLength = 12;
    /// <summary>Raw packets are at most 4 KB (AudioConfig.MAX_READ_SIZE); anything far larger means a broken stream.</summary>
    public const int MaxPacket = 1 << 20;
    public const int SampleRate = 48_000, Channels = 2, BytesPerFrame = 4;
    public const int BytesPerMs = SampleRate * BytesPerFrame / 1000; // 192

    /// <summary>The scrcpy audio sources that capture the microphone (the names go into an adb shell line: nothing else is allowed).</summary>
    public static readonly string[] MicSources =
        ["mic-camcorder", "mic", "mic-unprocessed", "mic-voice-recognition", "mic-voice-communication", "voice-performance"];

    public static string SocketName(int scid) => $"scrcpy_{scid:x8}";

    /// <summary>
    /// The adb shell command line that starts the server; <paramref name="source"/> must be one of <see cref="MicSources"/>.
    /// cleanup=false: with cleanup the server starts a CleanUp process that deletes the jar at once, so every reconnect
    /// would need a push first; audio-only it changes nothing on the phone that would need restoring.
    /// </summary>
    public static string ServerCommand(int scid, string source)
    {
        if (scid < 0) throw new ArgumentOutOfRangeException(nameof(scid));
        if (!MicSources.Contains(source)) throw new ArgumentException("unknown audio source", nameof(source));
        return $"CLASSPATH={RemotePath} app_process / com.genymobile.scrcpy.Server {Version} scid={scid:x8} log_level=info"
            + $" video=false control=false audio=true audio_source={source} audio_codec=raw tunnel_forward=true cleanup=false power_on=false";
    }

    /// <summary>The device name and codec id that follow the dummy byte.</summary>
    public static (string Device, uint Codec) ParseMeta(ReadOnlySpan<byte> meta)
    {
        if (meta.Length < MetaLength) throw new ArgumentException("short meta", nameof(meta));
        var name = meta[..DeviceNameLength];
        var end = name.IndexOf((byte)0);
        var device = Encoding.UTF8.GetString(end < 0 ? name : name[..end]);
        return (device, BinaryPrimitives.ReadUInt32BigEndian(meta[DeviceNameLength..]));
    }

    /// <summary>Why a codec id is not a usable raw stream, or null when it is.</summary>
    public static string? CodecProblem(uint codec) => codec switch
    {
        CodecRaw => null,
        0 => "the phone could not capture audio (Android 11 or later is needed, or the source is busy)",
        1 => "the phone's scrcpy server reported a configuration error",
        _ => $"unexpected codec 0x{codec:x8}",
    };

    public readonly record struct PacketHeader(long Pts, bool Config, int Length);

    public static PacketHeader ParseHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderLength) throw new ArgumentException("short header", nameof(header));
        var ptsAndFlags = BinaryPrimitives.ReadInt64BigEndian(header);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
        if (length > MaxPacket) throw new InvalidDataException($"packet of {length} bytes");
        var config = (ptsAndFlags & (1L << 62)) != 0;
        return new PacketHeader(ptsAndFlags & ((1L << 61) - 1), config, (int)length);
    }
}

/// <summary>The phone answered but can't capture audio (Android 10 and older, or a source it refuses): retrying soon won't help.</summary>
public sealed class PhoneMicUnsupportedException(string message) : Exception(message);

/// <summary>
/// Between the phone and the sound card: two clocks that never quite agree. The phone writes PCM as it arrives and the
/// audio device reads at its own pace. Latency is kept from creeping up over hours: when more than
/// <see cref="MaxBytes"/> piles up, the oldest audio is dropped back to <see cref="TargetBytes"/>; when it runs dry,
/// silence is played until <see cref="TargetBytes"/> has gathered again. Muted, everything arriving is discarded.
/// </summary>
public sealed class PcmJitterBuffer
{
    private readonly Lock _gate = new();
    private readonly byte[] _ring;
    private int _start, _count;
    private bool _primed;
    private int _target, _max;
    private double _gain = 1;

    public PcmJitterBuffer(int targetMs = 50, int maxMs = 150, int capacityMs = 1000)
    {
        // at least the largest ceiling Configure allows (900 ms) plus room, so any target can always be reached
        _ring = new byte[Align(Math.Max(capacityMs, 1000) * ScrcpyAudio.BytesPerMs)];
        Configure(targetMs, maxMs, 1);
    }

    public int TargetBytes { get { lock (_gate) return _target; } }
    public int MaxBytes { get { lock (_gate) return _max; } }
    public bool Muted { get; set; }
    /// <summary>Bytes thrown away to hold the latency down.</summary>
    public long DroppedBytes { get; private set; }
    /// <summary>Times playback ran dry and filled in silence.</summary>
    public long Underruns { get; private set; }
    /// <summary>Peak level of the last read, 0..1 (0 while muted or silent).</summary>
    public float Peak { get; private set; }
    public int BufferedBytes { get { lock (_gate) return _count; } }
    public int BufferedMs => BufferedBytes / ScrcpyAudio.BytesPerMs;

    private static int Align(int bytes) => bytes / ScrcpyAudio.BytesPerFrame * ScrcpyAudio.BytesPerFrame;

    /// <summary>Target and ceiling in milliseconds (clamped to sane values), and a linear gain.</summary>
    public void Configure(int targetMs, int maxMs, double gain)
    {
        lock (_gate)
        {
            targetMs = Math.Clamp(targetMs, 10, 500);
            maxMs = Math.Clamp(maxMs, targetMs + 20, 900);
            _target = Align(targetMs * ScrcpyAudio.BytesPerMs);
            _max = Math.Min(Align(maxMs * ScrcpyAudio.BytesPerMs), _ring.Length);
            _gain = double.IsFinite(gain) ? Math.Clamp(gain, 0, 8) : 1;
        }
    }

    public void Clear()
    {
        lock (_gate) { _start = 0; _count = 0; _primed = false; }
    }

    public void Write(ReadOnlySpan<byte> pcm)
    {
        if (Muted) { Clear(); return; }
        lock (_gate)
        {
            // keep whole frames only: a stray odd tail would swap left/right or split a sample from here on
            pcm = pcm[..Align(pcm.Length)];
            if (pcm.Length > _ring.Length) pcm = pcm[^_ring.Length..];
            var overflow = _count + pcm.Length - _ring.Length;
            if (overflow > 0) Drop(overflow);
            var end = (_start + _count) % _ring.Length;
            var first = Math.Min(pcm.Length, _ring.Length - end);
            pcm[..first].CopyTo(_ring.AsSpan(end));
            pcm[first..].CopyTo(_ring);
            _count += pcm.Length;
            if (_count > _max) Drop(_count - _target);
        }
    }

    private void Drop(int bytes)
    {
        bytes = Math.Min(Align(bytes + ScrcpyAudio.BytesPerFrame - 1), _count);
        _start = (_start + bytes) % _ring.Length;
        _count -= bytes;
        DroppedBytes += bytes;
    }

    /// <summary>Fills <paramref name="dst"/> completely: audio while there is some, silence otherwise.</summary>
    public void Read(Span<byte> dst)
    {
        int copied = 0;
        double gain;
        lock (_gate)
        {
            gain = _gain;
            if (Muted) { _start = 0; _count = 0; _primed = false; }
            if (!_primed && _count >= _target && _count > 0) _primed = true;
            if (_primed)
            {
                copied = Align(Math.Min(_count, dst.Length));
                var first = Math.Min(copied, _ring.Length - _start);
                _ring.AsSpan(_start, first).CopyTo(dst);
                _ring.AsSpan(0, copied - first).CopyTo(dst[first..]);
                _start = (_start + copied) % _ring.Length;
                _count -= copied;
                if (copied < dst.Length) { _primed = false; Underruns++; }
            }
        }
        dst[copied..].Clear();
        Peak = ApplyGain(dst[..copied], gain);
        HoldPeak(Peak, Clock());
    }

    internal Func<long> Clock { get; set; } = () => Environment.TickCount64;
    private float _peakNow, _peakLast;
    private long _peakSince;

    /// <summary>The loudest peak of the last second or so, 0..1: steady enough to show on a slowly polled meter.</summary>
    public float HeldPeak => Clock() - _peakSince > 2000 ? 0 : Math.Max(_peakNow, _peakLast);

    private void HoldPeak(float peak, long now)
    {
        if (now - _peakSince >= 1000) { _peakLast = now - _peakSince < 2000 ? _peakNow : 0; _peakNow = 0; _peakSince = now; }
        _peakNow = Math.Max(_peakNow, peak);
    }


    /// <summary>Scales s16le samples in place (clipping at full scale) and returns the peak, 0..1.</summary>
    internal static float ApplyGain(Span<byte> pcm, double gain)
    {
        int peak = 0;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            int s = BinaryPrimitives.ReadInt16LittleEndian(pcm[i..]);
            if (gain != 1)
            {
                s = (int)Math.Clamp(Math.Round(s * gain), short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(pcm[i..], (short)s);
            }
            peak = Math.Max(peak, Math.Abs(s));
        }
        return Math.Min(1f, peak / 32768f);
    }
}
