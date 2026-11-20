using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>A validated /api/remote/input request. Button 0 = left, 1 = right; Dx/Dy are pixels.</summary>
internal sealed record RemoteInput(string Type, uint Display = 0, double X = 0, double Y = 0, bool HasPoint = false,
    int Button = 0, int Dx = 0, int Dy = 0, ushort Key = 0, ulong Flags = 0, string? Text = null);

/// <summary>
/// /api/remote/*: TeamViewer-style remote desktop for macOS. The phone pulls JPEG frames of one display
/// (<see cref="RemoteCapture"/>) and posts pointer / keyboard input as query strings, which become CGEvents
/// (<see cref="MacSystem"/>). Everything but /info answers 403 while Settings › Remote desktop is off.
/// </summary>
internal static partial class RemoteDesktop
{
    internal const string OffError = "Remote desktop is off: turn it on in Settings";
    internal const string InputError = "Allow PhoneBoxAgent in System Settings › Privacy & Security › Accessibility";
    internal const string ScreenError = "Allow PhoneBoxAgent in System Settings › Privacy & Security › Screen Recording";
    internal const int MaxText = 2000, MaxScroll = 10_000;
    internal const int MinWidth = 320, MaxWidth = 2560, DefaultWidth = 1280, MinQuality = 30, MaxQuality = 90, DefaultQuality = 60;

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/remote/info", (ConfigStore cfg, RemoteCapture cap) => Results.Json(Info(cfg.Get().Remote.Enabled, cap)));
        app.MapGet("/api/remote/frame", async (string? display, int? w, int? q, long? since, ConfigStore cfg, RemoteCapture cap, HttpResponse res, CancellationToken ct) =>
        {
            var displays = MacSystem.RemoteDisplays();
            var target = displays.FirstOrDefault(d => d.Id.ToString(CultureInfo.InvariantCulture) == display);
            var (status, error) = FrameGate(cfg.Get().Remote.Enabled, target is not null, () => MacSystem.ScreenAccess());
            if (status == 409) MacSystem.AskScreenAccess();
            if (status == 404) return Results.NotFound(new { error = "unknown display" });
            if (error is not null) return Results.Json(new { error }, statusCode: status);
            var frame = await cap.FrameAsync(displays, target!, ClampWidth(w), ClampQuality(q), since, ct);
            res.Headers.CacheControl = "no-store";
            if (frame is null) return Results.Json(new { error = "Could not capture the screen" }, statusCode: 503);
            res.Headers["X-Frame-Seq"] = frame.Value.Seq.ToString(CultureInfo.InvariantCulture);
            return frame.Value.Jpeg is { } jpeg ? Results.File(jpeg, "image/jpeg") : Results.StatusCode(304);
        });
        app.MapPost("/api/remote/input", (HttpRequest req, ConfigStore cfg) =>
        {
            var (status, body) = Input(cfg.Get().Remote.Enabled, MacSystem.InputTrusted, k => req.Query[k].FirstOrDefault(), Execute);
            if (status == 403 && cfg.Get().Remote.Enabled) MacSystem.AskInputTrust();
            return Results.Json(body, statusCode: status);
        });
    }

    internal static object Info(bool enabled, RemoteCapture cap) => new
    {
        enabled,
        displays = MacSystem.RemoteDisplays().Select(d => new
        {
            id = d.Id.ToString(CultureInfo.InvariantCulture), name = d.Name, width = (int)Math.Round(d.W), height = (int)Math.Round(d.H), main = d.Main,
        }),
        screenPermission = MacSystem.ScreenAccess(),
        inputPermission = MacSystem.InputTrusted(),
        capture = cap.Method(),
    };

    internal static int ClampWidth(int? w) => Math.Clamp(w ?? DefaultWidth, MinWidth, MaxWidth);
    internal static int ClampQuality(int? q) => Math.Clamp(q ?? DefaultQuality, MinQuality, MaxQuality);

    /// <summary>JPEG quality 30..90 as ffmpeg's -q:v (2 best .. 31 worst): 90 → 2, 60 → 11, 30 → 20.</summary>
    internal static int Qv(int quality) => (int)Math.Round(2 + (MaxQuality - Math.Clamp(quality, MinQuality, MaxQuality)) * 18.0 / 60);

    /// <summary>403 while off, 404 for an unknown display, 409 without Screen Recording; 200 = go ahead.</summary>
    internal static (int Status, string? Error) FrameGate(bool enabled, bool knownDisplay, Func<bool> screenAccess) =>
        !enabled ? (403, OffError) : !knownDisplay ? (404, "unknown display") : !screenAccess() ? (409, ScreenError) : (200, null);

    /// <summary>The input endpoint: gates, validation, then <paramref name="execute"/> (CGEvents; a fake in the tests).</summary>
    internal static (int Status, object Body) Input(bool enabled, Func<bool> trusted, Func<string, string?> query, Action<RemoteInput> execute)
    {
        if (!enabled) return (403, new { error = OffError });
        if (!trusted()) return (403, new { error = InputError });
        var (input, error) = ParseInput(query, id => MacSystem.DisplayRect(id) is not null);
        if (input is null) return (400, new { error });
        try { execute(input); }
        catch (Exception ex) { return (500, new { error = ex.Message }); }
        return (200, new { ok = true });
    }

    private static readonly string[] PointTypes = ["move", "down", "up", "click", "rightclick", "dblclick"];

    /// <summary>Validates the query of /api/remote/input; null and a message when something is wrong.</summary>
    internal static (RemoteInput? Input, string? Error) ParseInput(Func<string, string?> q, Func<uint, bool> displayExists)
    {
        var type = q("type")?.ToLowerInvariant();
        if (type is not ("move" or "down" or "up" or "click" or "rightclick" or "dblclick" or "scroll" or "key" or "text"))
            return (null, "type must be move, down, up, click, rightclick, dblclick, scroll, key or text");

        var input = new RemoteInput(type);
        var needsPoint = PointTypes.Contains(type);
        if (needsPoint || (type == "scroll" && (q("x") is not null || q("y") is not null)))
        {
            if (!uint.TryParse(q("display"), NumberStyles.None, CultureInfo.InvariantCulture, out var display) || !displayExists(display)) return (null, "unknown display");
            if (Unit(q("x")) is not { } x || Unit(q("y")) is not { } y) return (null, "x and y must be numbers from 0 to 1");
            input = input with { Display = display, X = x, Y = y, HasPoint = true };
        }

        switch (type)
        {
            case "down" or "up":
                var button = q("button")?.ToLowerInvariant() ?? "left";
                if (button is not ("left" or "right")) return (null, "button must be left or right");
                return (input with { Button = button == "right" ? 1 : 0 }, null);
            case "scroll":
                if (Pixels(q("dx")) is not { } dx || Pixels(q("dy")) is not { } dy) return (null, $"dx and dy must be numbers from -{MaxScroll} to {MaxScroll}");
                return (input with { Dx = dx, Dy = dy }, null);
            case "key":
                if (ModFlags(q("mods")) is not { } flags) return (null, "mods must be a comma list of cmd, shift, alt, ctrl");
                var key = q("key") ?? "";
                if (KeyCode(key) is { } k) return (input with { Key = k.Code, Flags = flags | (k.Shift ? MacSystem.ShiftFlag : 0) }, null);
                // one character with no key on a US keyboard (é, ü, emoji): typed, which can't carry modifiers
                if (key.Length > 0 && (key.Length == 1 || (key.Length == 2 && char.IsSurrogatePair(key, 0))) && flags == 0) return (new RemoteInput("text", Text: key), null);
                return (null, "unknown key");
            case "text":
                var text = q("text");
                if (string.IsNullOrEmpty(text)) return (null, "text is required");
                if (text.Length > MaxText) return (null, $"text is limited to {MaxText} characters");
                return (input with { Text = text }, null);
            default:
                return (input, null);
        }

        static double? Unit(string? s) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v is >= 0 and <= 1 ? v : null;
        static int? Pixels(string? s) =>
            s is null ? 0 : double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && Math.Abs(v) <= MaxScroll ? (int)Math.Round(v) : null;
    }

    /// <summary>Runs a validated input as CGEvents.</summary>
    private static void Execute(RemoteInput i)
    {
        var (x, y) = i.HasPoint && MacSystem.DisplayRect(i.Display) is { } r ? ToGlobal(r, i.X, i.Y) : (0, 0);
        switch (i.Type)
        {
            case "move": MacSystem.MoveTo(x, y); break;
            case "down" or "up": MacSystem.Button(x, y, i.Button, i.Type == "down"); break;
            case "click": MacSystem.Click(x, y, 0, 1); break;
            case "rightclick": MacSystem.Click(x, y, 1, 1); break;
            case "dblclick": MacSystem.Click(x, y, 0, 2); break;
            case "scroll":
                if (i.HasPoint) MacSystem.MoveTo(x, y);
                MacSystem.Scroll(-i.Dy, -i.Dx); // dx/dy like a browser wheel event (dy > 0 scrolls down); CoreGraphics' are the other way
                break;
            case "key": MacSystem.Key(i.Key, i.Flags); break;
            case "text": MacSystem.Type(i.Text!); break;
        }
    }

    /// <summary>A point 0..1 of a display's bounds (global points; an external display left of or above the main one
    /// has a negative origin) as a global point, kept inside the display.</summary>
    internal static (double X, double Y) ToGlobal((double X, double Y, double W, double H) r, double x, double y) =>
        (r.X + Math.Min(Math.Clamp(x, 0, 1) * r.W, r.W - 1), r.Y + Math.Min(Math.Clamp(y, 0, 1) * r.H, r.H - 1));

    /// <summary>"cmd,shift" as CGEventFlags; 0 for none, null for an unknown name.</summary>
    internal static ulong? ModFlags(string? mods)
    {
        ulong flags = 0;
        foreach (var m in (mods ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (m.ToLowerInvariant())
            {
                case "cmd" or "command" or "meta": flags |= MacSystem.CmdFlag; break;
                case "shift": flags |= MacSystem.ShiftFlag; break;
                case "alt" or "option" or "opt": flags |= MacSystem.AltFlag; break;
                case "ctrl" or "control": flags |= MacSystem.CtrlFlag; break;
                default: return null;
            }
        }
        return flags;
    }

    // ponytail: ANSI (US) key positions; on other layouts a symbol may sit elsewhere, letters/digits/shortcuts mostly match
    private const string Plain = "asdfhgzxcv\0bqweryt123465=97-80]ou[ip\rlj'k;\\,/nm.\0\0`";
    private const string Shifted = "!@#$%^&*()_+{}|:\"<>?~";
    private const string ShiftedBase = "1234567890-=[]\\;',./`";

    /// <summary>A key name (esc, tab, enter, f1–f12, arrows…) or one character as a macOS virtual key code, with Shift
    /// for capitals and shifted symbols; null when there is no such key.</summary>
    internal static (ushort Code, bool Shift)? KeyCode(string key)
    {
        if (key.Length == 1)
        {
            var c = key[0];
            if (c == ' ') return (49, false);
            if (char.IsAsciiLetterUpper(c)) return KeyCode(char.ToLowerInvariant(c).ToString()) is { } u ? (u.Code, true) : null;
            var s = Shifted.IndexOf(c);
            if (s >= 0) return KeyCode(ShiftedBase[s].ToString()) is { } b ? (b.Code, true) : null;
            var i = c == '\0' ? -1 : Plain.IndexOf(c);
            return i >= 0 ? ((ushort)i, false) : null;
        }
        var name = key.ToLowerInvariant();
        if (name is ['f', _, ..] && int.TryParse(name[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is < 1 or > 12) return null;
        return KeySender.MacKeyCode(name) is { } code ? ((ushort)code, false) : null;
    }

    /// <summary>Text as Unicode key-event payloads of at most 20 UTF-16 units (a surrogate pair is never split), with
    /// each line break ("\n", "\r\n" or "\r") as a "\n" entry of its own, pressed as Return.</summary>
    internal static List<string> TextChunks(string text)
    {
        var result = new List<string>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var l = 0; l < lines.Length; l++)
        {
            if (l > 0) result.Add("\n");
            var line = lines[l];
            for (var i = 0; i < line.Length;)
            {
                var len = Math.Min(20, line.Length - i);
                if (len == 20 && char.IsHighSurrogate(line[i + len - 1])) len--;
                result.Add(line.Substring(i, len));
                i += len;
            }
        }
        return result;
    }

    /// <summary>"Capture screen N" devices in <c>ffmpeg -f avfoundation -list_devices true -i ""</c>'s stderr, as
    /// screen number → avfoundation device index (cameras come first, so screen 0 is often device 1 or later).</summary>
    internal static Dictionary<int, int> ParseScreens(string stderr)
    {
        var map = new Dictionary<int, int>();
        foreach (Match m in ScreenDeviceLine().Matches(stderr))
            map.TryAdd(int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        return map;
    }

    [GeneratedRegex(@"\[(\d+)\] Capture screen (\d+)")] private static partial Regex ScreenDeviceLine();

    /// <summary>The avfoundation device of a display: ffmpeg numbers screens in CGGetActiveDisplayList order, so
    /// "Capture screen N" is the Nth active display. Null when ffmpeg has no such screen.</summary>
    internal static int? ScreenDevice(IReadOnlyList<uint> active, IReadOnlyDictionary<int, int> screens, uint display)
    {
        var n = active is List<uint> l ? l.IndexOf(display) : active.ToList().IndexOf(display);
        return n >= 0 && screens.TryGetValue(n, out var device) ? device : null;
    }
}

/// <summary>
/// Screen frames for the remote desktop. One ffmpeg per (display, width, quality) streams MJPEG from avfoundation and
/// only the newest frame is kept; a stream stops 8 s after the last request, at most two run at once (the least
/// recently asked one makes room), and all stop with the agent. Without a working ffmpeg every request runs
/// screencapture instead (slower).
/// </summary>
internal sealed class RemoteCapture : IDisposable
{
    private const int MaxStreams = 2;
    private const long IdleMs = 8_000, FirstFrameMs = 4_000, WaitMs = 1_000, RetryFfmpegMs = 60_000;

    private sealed class Stream
    {
        public Process? Process;
        public byte[]? Frame;
        public long Seq, LastAsked;
        public bool Ended;
        public TaskCompletionSource Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly ILogger<RemoteCapture> _log;
    private readonly Lock _gate = new();
    private readonly Dictionary<(uint Display, int W, int Q), Stream> _streams = [];
    private readonly Timer _timer;
    private static long _seq; // one counter for every stream: a restarted stream never repeats a number
    private long _ffmpegBrokenUntil;
    private (string Key, Dictionary<int, int> Screens)? _devices;
    private bool _disposed;

    public RemoteCapture(ILogger<RemoteCapture> log)
    {
        _log = log;
        _timer = new Timer(_ => Reap(), null, 2000, 2000);
    }

    private static string? Ffmpeg() => ToolProcess.Find("ffmpeg");

    private bool FfmpegUsable => Ffmpeg() is not null && Environment.TickCount64 >= Interlocked.Read(ref _ffmpegBrokenUntil);

    public string Method() => FfmpegUsable ? "ffmpeg" : File.Exists("/usr/sbin/screencapture") ? "screencapture" : "none";

    /// <summary>The newest frame and its number; Jpeg null = nothing newer than <paramref name="since"/> within 1 s
    /// (304). Null when the screen could not be captured.</summary>
    public async Task<(byte[]? Jpeg, long Seq)?> FrameAsync(IReadOnlyList<RemoteDisplay> displays, RemoteDisplay display, int w, int q, long? since, CancellationToken ct)
    {
        if (FfmpegUsable && await DeviceAsync(displays, display.Id, ct) is { } device)
        {
            var got = await StreamFrameAsync(display.Id, device, w, q, since, ct);
            if (got is not null) return got;
        }
        var n = displays.ToList().IndexOf(display) + 1;
        return await ScreencaptureAsync(n, Math.Min(w, display.PixelWidth > 0 ? display.PixelWidth : w), q, ct) is { } jpeg
            ? (jpeg, Interlocked.Increment(ref _seq)) : null;
    }

    private async Task<(byte[]? Jpeg, long Seq)?> StreamFrameAsync(uint display, int device, int w, int q, long? since, CancellationToken ct)
    {
        Stream s;
        Process? evicted = null;
        lock (_gate)
        {
            if (_disposed) return null;
            if (!_streams.TryGetValue((display, w, q), out var found) || found.Ended)
            {
                if (found is not null) _streams.Remove((display, w, q));
                if (_streams.Count >= MaxStreams)
                {
                    var oldest = _streams.MinBy(kv => kv.Value.LastAsked);
                    _streams.Remove(oldest.Key);
                    evicted = oldest.Value.Process;
                }
                s = new Stream();
                if (!Start(s, device, w, q)) return null;
                _streams[(display, w, q)] = s;
            }
            else s = found;
            s.LastAsked = Environment.TickCount64;
        }
        Kill(evicted);

        await Wait(s, frame => frame.Frame is not null, FirstFrameMs, ct);
        bool failed;
        lock (_gate) failed = s.Frame is null && s.Ended;
        if (failed)
        {
            // ffmpeg ended without a picture (no permission for it, a broken build…): screencapture for a while
            Interlocked.Exchange(ref _ffmpegBrokenUntil, Environment.TickCount64 + RetryFfmpegMs);
            _log.LogWarning("remote desktop: ffmpeg gave no picture, using screencapture for a minute");
            return null;
        }
        if (since is { } seen && Volatile.Read(ref s.Seq) == seen) await Wait(s, frame => frame.Seq != seen, WaitMs, ct);
        lock (_gate)
        {
            if (s.Frame is null) return null;
            return since == s.Seq ? (null, s.Seq) : (s.Frame, s.Seq);
        }
    }

    /// <summary>Waits until <paramref name="done"/> holds, the stream ends, or the time is up.</summary>
    private async Task<Stream> Wait(Stream s, Func<Stream, bool> done, long ms, CancellationToken ct)
    {
        var until = Environment.TickCount64 + ms;
        while (true)
        {
            Task next;
            lock (_gate)
            {
                if (done(s) || s.Ended) return s;
                next = s.Next.Task;
            }
            var left = until - Environment.TickCount64;
            if (left <= 0) return s;
            await Task.WhenAny(next, Task.Delay(TimeSpan.FromMilliseconds(left), ct));
            ct.ThrowIfCancellationRequested();
        }
    }

    private bool Start(Stream s, int device, int w, int q)
    {
        var psi = new ProcessStartInfo(Ffmpeg()!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in Args(device, w, q)) psi.ArgumentList.Add(a);
        Process p;
        try { p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start"); }
        catch (Exception ex)
        {
            _log.LogWarning("remote desktop: cannot start ffmpeg: {Msg}", ex.Message);
            Interlocked.Exchange(ref _ffmpegBrokenUntil, Environment.TickCount64 + RetryFfmpegMs);
            return false;
        }
        s.Process = p;
        _ = Task.Run(async () =>
        {
            var split = new JpegSplitter(jpeg =>
            {
                lock (_gate)
                {
                    if (s.Frame is { } prev && prev.AsSpan().SequenceEqual(jpeg)) return; // still screen: no new number, so the phone's wait ends in 304
                    s.Frame = jpeg;
                    s.Seq = Interlocked.Increment(ref _seq);
                    var wake = s.Next;
                    s.Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    wake.TrySetResult();
                }
            });
            var buf = new byte[64 * 1024];
            try { int n; while ((n = await p.StandardOutput.BaseStream.ReadAsync(buf)) > 0) split.Push(buf.AsSpan(0, n)); }
            catch { }
            lock (_gate) { s.Ended = true; s.Next.TrySetResult(); }
        });
        _ = Task.Run(async () =>
        {
            string? last = null;
            try { while (await p.StandardError.ReadLineAsync() is { } line) if (line.Length > 0) last = line; } catch { }
            if (last is not null) _log.LogDebug("remote desktop ffmpeg: {Line}", last);
        });
        return true;
    }

    /// <summary>ffmpeg's arguments: avfoundation screen with the pointer, scaled to at most <paramref name="w"/> wide, MJPEG on stdout.</summary>
    internal static string[] Args(int device, int w, int q) =>
    [
        "-hide_banner", "-nostdin", "-loglevel", "error",
        "-f", "avfoundation", "-capture_cursor", "1", "-capture_mouse_clicks", "0", "-pixel_format", "nv12", "-framerate", "15",
        "-i", $"{device}:none", "-vf", $"scale='min({w},iw)':-2", "-q:v", RemoteDesktop.Qv(q).ToString(CultureInfo.InvariantCulture),
        "-f", "mjpeg", "-",
    ];

    /// <summary>avfoundation's device index for a display; the device list is read again when the displays change.</summary>
    private async Task<int?> DeviceAsync(IReadOnlyList<RemoteDisplay> displays, uint display, CancellationToken ct)
    {
        var active = displays.Select(d => d.Id).ToList();
        var key = string.Join(',', active);
        var cached = _devices;
        if (cached?.Key != key)
        {
            var psi = new ProcessStartInfo(Ffmpeg()!);
            foreach (var a in new[] { "-hide_banner", "-f", "avfoundation", "-list_devices", "true", "-i", "" }) psi.ArgumentList.Add(a);
            string err;
            try { err = (await ToolProcess.RunAsync(psi, TimeSpan.FromSeconds(10), ct)).Error; }
            catch (Exception ex) when (ex is not OperationCanceledException) { err = ""; }
            cached = _devices = (key, RemoteDesktop.ParseScreens(err));
        }
        return RemoteDesktop.ScreenDevice(active, cached.Value.Screens, display);
    }

    /// <summary>One screencapture of display <paramref name="n"/> (1 = main), resized with sips. Null on failure.</summary>
    private static async Task<byte[]?> ScreencaptureAsync(int n, int w, int q, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"phonebox-remote-{Guid.NewGuid():N}.jpg");
        try
        {
            if (!await Run("/usr/sbin/screencapture", "-x", "-D", n.ToString(CultureInfo.InvariantCulture), "-t", "jpg", tmp) || !File.Exists(tmp)) return null;
            await Run("/usr/bin/sips", "-s", "formatOptions", q.ToString(CultureInfo.InvariantCulture), "--resampleWidth", w.ToString(CultureInfo.InvariantCulture), tmp);
            return await File.ReadAllBytesAsync(tmp, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
        finally { try { File.Delete(tmp); } catch { } }

        async Task<bool> Run(string tool, params string[] args)
        {
            var psi = new ProcessStartInfo(tool);
            foreach (var a in args) psi.ArgumentList.Add(a);
            try { return (await ToolProcess.RunAsync(psi, TimeSpan.FromSeconds(8), ct)).ExitCode == 0; }
            catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
        }
    }

    /// <summary>Every 2 s: stops streams nobody asked for in 8 s, forgets ended ones.</summary>
    private void Reap()
    {
        List<Process?> stop = [];
        var now = Environment.TickCount64;
        lock (_gate)
        {
            foreach (var (key, s) in _streams.ToList())
            {
                if (!s.Ended && now - s.LastAsked < IdleMs) continue;
                _streams.Remove(key);
                stop.Add(s.Process);
            }
        }
        foreach (var p in stop) Kill(p);
    }

    private static void Kill(Process? p)
    {
        if (p is null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        p.Dispose();
    }

    public void Dispose()
    {
        List<Process?> stop;
        lock (_gate)
        {
            _disposed = true;
            stop = _streams.Values.Select(s => s.Process).ToList();
            _streams.Clear();
        }
        _timer.Dispose();
        foreach (var p in stop) Kill(p);
    }
}
