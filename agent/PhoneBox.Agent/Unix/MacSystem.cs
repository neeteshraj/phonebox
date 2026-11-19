using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>
/// macOS displays (CoreGraphics), built-in display brightness (private DisplayServices), keyboard backlight
/// (private CoreBrightness via the Objective-C runtime) and Spotlight search (System Events). Every public call is
/// exception-safe: a missing framework or symbol reads as "unsupported", never as a crash.
/// </summary>
internal static partial class MacSystem
{
    private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string DS = "/System/Library/PrivateFrameworks/DisplayServices.framework/DisplayServices";
    private const string CoreBrightness = "/System/Library/PrivateFrameworks/CoreBrightness.framework/CoreBrightness";
    private const string Objc = "/usr/lib/libobjc.A.dylib";

    [StructLayout(LayoutKind.Sequential)] private struct CGPoint { public double X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct CGRect { public double X, Y, W, H; }

    [DllImport(CG)] private static extern int CGGetActiveDisplayList(uint max, [Out] uint[] displays, out uint count);
    [DllImport(CG)] private static extern int CGDisplayIsBuiltin(uint display);
    [DllImport(CG)] private static extern uint CGMainDisplayID();
    [DllImport(CG)] private static extern CGRect CGDisplayBounds(uint display);
    [DllImport(CG)] private static extern nuint CGDisplayPixelsWide(uint display);
    [DllImport(CG)] private static extern nuint CGDisplayPixelsHigh(uint display);
    [DllImport(CG)] private static extern IntPtr CGDisplayCopyDisplayMode(uint display);
    [DllImport(CG)] private static extern double CGDisplayModeGetRefreshRate(IntPtr mode);
    [DllImport(CG)] private static extern nuint CGDisplayModeGetPixelWidth(IntPtr mode);
    [DllImport(CG)] private static extern nuint CGDisplayModeGetPixelHeight(IntPtr mode);
    [DllImport(CG)] private static extern void CGDisplayModeRelease(IntPtr mode);
    [DllImport(CG)] private static extern int CGWarpMouseCursorPosition(CGPoint point);
    [DllImport(CG)] private static extern int CGAssociateMouseAndMouseCursorPosition(int connected);
    [DllImport(CG)] private static extern IntPtr CGEventCreate(IntPtr source);
    [DllImport(CG)] private static extern CGPoint CGEventGetLocation(IntPtr evt);
    [DllImport(CF)] private static extern void CFRelease(IntPtr obj);
    [DllImport(DS)] private static extern int DisplayServicesGetBrightness(uint display, out float brightness);
    [DllImport(DS)] private static extern int DisplayServicesSetBrightness(uint display, float brightness);

    [DllImport(Objc)] private static extern IntPtr objc_getClass(string name);
    [DllImport(Objc)] private static extern IntPtr sel_registerName(string name);
    [DllImport(Objc)] private static extern IntPtr class_getInstanceMethod(IntPtr cls, IntPtr sel);
    [DllImport(Objc)] private static extern IntPtr objc_autoreleasePoolPush();
    [DllImport(Objc)] private static extern void objc_autoreleasePoolPop(IntPtr pool);
    // objc_msgSend takes the prototype of the method it forwards to, so one import per signature.
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr self, IntPtr sel);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern IntPtr SendIndex(IntPtr self, IntPtr sel, nuint index);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern ulong SendU64(IntPtr self, IntPtr sel);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern float SendFloatForU64(IntPtr self, IntPtr sel, ulong arg);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern byte SendBoolForU64(IntPtr self, IntPtr sel, ulong arg);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern byte SendBoolFloatU64(IntPtr self, IntPtr sel, float value, ulong arg);

    // ---------- displays ----------

    /// <summary>Active displays with names, mode and brightness; [] when anything fails.</summary>
    public static IReadOnlyList<SystemDisplay> Displays()
    {
        if (!OperatingSystem.IsMacOS()) return [];
        try
        {
            var buf = new uint[32];
            if (CGGetActiveDisplayList((uint)buf.Length, buf, out var n) != 0) return [];
            var list = buf.Take((int)n).Select(id => (Id: id, Builtin: CGDisplayIsBuiltin(id) != 0)).ToList();
            var main = CGMainDisplayID();
            var pointer = Pointer();
            var info = ProfilerInfo(list);
            var result = list.Select((d, i) =>
            {
                var b = CGDisplayBounds(d.Id);
                int w = (int)CGDisplayPixelsWide(d.Id), h = (int)CGDisplayPixelsHigh(d.Id), hz = 0;
                var mode = CGDisplayCopyDisplayMode(d.Id);
                if (mode != 0)
                {
                    try
                    {
                        w = (int)CGDisplayModeGetPixelWidth(mode);
                        h = (int)CGDisplayModeGetPixelHeight(mode);
                        hz = (int)Math.Round(CGDisplayModeGetRefreshRate(mode));
                    }
                    finally { CGDisplayModeRelease(mode); }
                }
                info.TryGetValue(d.Id, out var p);
                if (hz == 0) hz = p.Hz; // ProMotion panels report 0 from CoreGraphics
                var focused = pointer is { } pt && pt.X >= b.X && pt.X < b.X + b.W && pt.Y >= b.Y && pt.Y < b.Y + b.H;
                var name = d.Builtin ? "Built-in Display" : p.Name ?? $"Display {i + 1}";
                return (Cg: d.Id, Display: new SystemDisplay(d.Id.ToString(CultureInfo.InvariantCulture), name, d.Builtin, d.Id == main, focused, w, h, hz, Brightness(d.Id)));
            }).ToList();
            // externals without DisplayServices brightness: DDC/CI through m1ddc, when the monitor really answers
            var ddc = DdcLevels(result.Where(r => !r.Display.Builtin && r.Display.Brightness is null).Select(r => (r.Cg, r.Display.Name)).ToList());
            return result.Select(r => ddc.TryGetValue(r.Cg, out var l) ? r.Display with { Brightness = l.Percent } : r.Display).ToList();
        }
        catch { return []; }
    }

    private static CGPoint? Pointer()
    {
        var evt = CGEventCreate(0);
        if (evt == 0) return null;
        try { return CGEventGetLocation(evt); } finally { CFRelease(evt); }
    }

    /// <summary>Moves the pointer to the centre of a display.</summary>
    public static bool Focus(uint display)
    {
        try
        {
            var b = CGDisplayBounds(display);
            if (b.W <= 0 || b.H <= 0) return false;
            var ok = CGWarpMouseCursorPosition(new CGPoint { X = b.X + b.W / 2, Y = b.Y + b.H / 2 }) == 0;
            CGAssociateMouseAndMouseCursorPosition(1); // a warp freezes the pointer briefly otherwise
            return ok;
        }
        catch { return false; }
    }

    private static bool _noDisplayServices;

    /// <summary>0-100, or null when the display's brightness cannot be read (most third-party externals).</summary>
    public static int? Brightness(uint display)
    {
        if (_noDisplayServices) return null;
        try { return DisplayServicesGetBrightness(display, out var v) == 0 && v is >= 0 and <= 1 ? (int)Math.Round(v * 100) : null; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { _noDisplayServices = true; return null; }
        catch { return null; }
    }

    public static bool SetBrightness(uint display, int percent)
    {
        if (Brightness(display) is null) return DdcSet(display, percent); // DDC-capable external, else unsupported
        try { return DisplayServicesSetBrightness(display, Math.Clamp(percent, 0, 100) / 100f) == 0; }
        catch { return false; }
    }

    // system_profiler gives external displays their marketing names; CoreGraphics has none.
    private static readonly Lock ProfilerGate = new();
    private static string? _profilerKey;
    private static long _profilerAt;
    private static bool _profilerIncomplete;
    private static Dictionary<uint, (string? Name, int Hz)> _profiler = [];

    /// <summary>Re-runs system_profiler when the display set changes, or every 30 s while an external is still unnamed.</summary>
    private static Dictionary<uint, (string? Name, int Hz)> ProfilerInfo(List<(uint Id, bool Builtin)> displays)
    {
        var key = string.Join(',', displays.Select(d => d.Id));
        lock (ProfilerGate)
        {
            var now = Environment.TickCount64;
            if (key == _profilerKey && !(_profilerIncomplete && now - _profilerAt > 30_000)) return _profiler;
            var json = Sh.RunWithTimeout(10_000, "system_profiler", "SPDisplaysDataType", "-json");
            _profiler = json is null ? [] : ParseProfiler(json, displays);
            _profilerKey = key;
            _profilerAt = now;
            _profilerIncomplete = displays.Any(d => !d.Builtin && !_profiler.ContainsKey(d.Id));
            return _profiler;
        }
    }

    /// <summary>
    /// Maps <c>system_profiler SPDisplaysDataType -json</c> entries to CoreGraphics display ids: by
    /// <c>_spdisplays_displayID</c> when present, else in order among displays of the same kind (built-in / external).
    /// </summary>
    internal static Dictionary<uint, (string? Name, int Hz)> ParseProfiler(string json, IReadOnlyList<(uint Id, bool Builtin)> displays)
    {
        var result = new Dictionary<uint, (string? Name, int Hz)>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("SPDisplaysDataType", out var gpus) || gpus.ValueKind != JsonValueKind.Array) return result;
            var unmatched = new List<(bool Internal, string? Name, int Hz)>();
            foreach (var gpu in gpus.EnumerateArray())
            {
                if (!gpu.TryGetProperty("spdisplays_ndrvs", out var drvs) || drvs.ValueKind != JsonValueKind.Array) continue;
                foreach (var e in drvs.EnumerateArray())
                {
                    var name = Str(e, "_name");
                    var res = Str(e, "_spdisplays_resolution") ?? Str(e, "spdisplays_resolution") ?? "";
                    var m = HzPattern().Match(res);
                    var hz = m.Success ? (int)Math.Round(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)) : 0;
                    if (uint.TryParse(Str(e, "_spdisplays_displayID"), out var id) && displays.Any(d => d.Id == id) && !result.ContainsKey(id))
                        result[id] = (name, hz);
                    else
                        unmatched.Add((Str(e, "spdisplays_connection_type") == "spdisplays_internal", name, hz));
                }
            }
            foreach (var d in displays.Where(d => !result.ContainsKey(d.Id)))
            {
                var i = unmatched.FindIndex(u => u.Internal == d.Builtin);
                if (i < 0) continue;
                result[d.Id] = (unmatched[i].Name, unmatched[i].Hz);
                unmatched.RemoveAt(i);
            }
        }
        catch (JsonException) { }
        return result;

        static string? Str(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    [GeneratedRegex(@"@\s*([0-9]+(?:\.[0-9]+)?)\s*Hz")] private static partial Regex HzPattern();

    // ---------- keyboard backlight ----------

    private static readonly Lock KeyboardGate = new();
    private static bool _keyboardTried;
    private static IntPtr _keyboardClient;
    private static ulong _keyboardId;

    private static IntPtr Sel(string name) => sel_registerName(name);

    /// <summary>The cached KeyboardBrightnessClient and the backlight id (built-in keyboard first); false when unavailable.</summary>
    private static bool KeyboardClient(out IntPtr client, out ulong id)
    {
        if (!_keyboardTried)
        {
            _keyboardTried = true;
            try { InitKeyboard(); } catch { _keyboardClient = 0; }
        }
        client = _keyboardClient; id = _keyboardId;
        return client != 0;
    }

    private static void InitKeyboard()
    {
        if (!OperatingSystem.IsMacOS() || !NativeLibrary.TryLoad(CoreBrightness, out _)) return;
        var cls = objc_getClass("KeyboardBrightnessClient");
        if (cls == 0) return;
        // An unknown selector raises an Objective-C exception, which would take the process down: check first.
        string[] needed = ["copyKeyboardBacklightIDs", "brightnessForKeyboard:", "setBrightness:forKeyboard:"];
        if (needed.Any(s => class_getInstanceMethod(cls, Sel(s)) == 0)) return;
        var hasBuiltIn = class_getInstanceMethod(cls, Sel("isKeyboardBuiltIn:")) != 0;
        var pool = objc_autoreleasePoolPush();
        try
        {
            var client = Send(Send(cls, Sel("alloc")), Sel("init"));
            if (client == 0) return;
            var ids = Send(client, Sel("copyKeyboardBacklightIDs"));
            ulong? pick = null;
            if (ids != 0)
            {
                var count = (nuint)Send(ids, Sel("count"));
                for (nuint i = 0; i < count; i++)
                {
                    var id = SendU64(SendIndex(ids, Sel("objectAtIndex:"), i), Sel("unsignedLongLongValue"));
                    pick ??= id;
                    if (hasBuiltIn && SendBoolForU64(client, Sel("isKeyboardBuiltIn:"), id) != 0) { pick = id; break; }
                }
                Send(ids, Sel("release"));
            }
            if (pick is null) { Send(client, Sel("release")); return; }
            _keyboardClient = client; // kept for the life of the process
            _keyboardId = pick.Value;
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    /// <summary>Keyboard backlight 0-100, or null when there is none / the private API is missing.</summary>
    public static int? KeyboardBrightness()
    {
        lock (KeyboardGate)
        {
            if (!KeyboardClient(out var client, out var id)) return null;
            var pool = objc_autoreleasePoolPush();
            try
            {
                var v = SendFloatForU64(client, Sel("brightnessForKeyboard:"), id);
                return v is >= 0 and <= 1 ? (int)Math.Round(v * 100) : null;
            }
            catch { return null; }
            finally { objc_autoreleasePoolPop(pool); }
        }
    }

    public static bool SetKeyboardBrightness(int percent)
    {
        lock (KeyboardGate)
        {
            if (!KeyboardClient(out var client, out var id)) return false;
            var pool = objc_autoreleasePoolPush();
            try { return SendBoolFloatU64(client, Sel("setBrightness:forKeyboard:"), Math.Clamp(percent, 0, 100) / 100f, id) != 0; }
            catch { return false; }
            finally { objc_autoreleasePoolPop(pool); }
        }
    }

    // ---------- Spotlight ----------

    /// <summary>osascript argv that opens Spotlight and types the query. The query is a run-handler argument after
    /// "--", never part of the script text.</summary>
    internal static string[] SpotlightArgs(string query) =>
    [
        "-e", "on run argv",
        "-e", "tell application \"System Events\"",
        "-e", "key code 49 using {command down}",
        "-e", "delay 0.4",
        "-e", "if (item 1 of argv) is not \"\" then keystroke (item 1 of argv)",
        "-e", "end tell",
        "-e", "end run",
        "--", query,
    ];

    /// <summary>Null on success, else a message the phone can show.</summary>
    public static string? Spotlight(string query) =>
        Sh.RunWithTimeout(8000, "osascript", SpotlightArgs(query)) is null ? KeySender.AccessibilityHint : null;
}
