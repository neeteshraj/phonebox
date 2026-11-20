using System.Runtime.InteropServices;

namespace PhoneBox.Agent;

/// <summary>A display as the remote desktop sees it: CoreGraphics id, name, and its bounds in global points.</summary>
internal sealed record RemoteDisplay(uint Id, string Name, bool Main, double X, double Y, double W, double H, int PixelWidth);

/// <summary>
/// Remote desktop and the Trackpad widget on macOS: synthetic mouse / keyboard events (CGEventPost at the HID tap,
/// which needs the Accessibility permission) and the Screen Recording / Accessibility permission checks.
/// Every CGEvent created here is released after posting.
/// </summary>
internal static partial class MacSystem
{
    private const string AppServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    [DllImport(CG)] private static extern IntPtr CGEventCreateMouseEvent(IntPtr source, uint type, CGPoint position, uint button);
    [DllImport(CG)] private static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort keycode, [MarshalAs(UnmanagedType.U1)] bool down);
    [DllImport(CG)] private static extern IntPtr CGEventCreateScrollWheelEvent2(IntPtr source, uint units, uint wheelCount, int wheel1, int wheel2, int wheel3);
    [DllImport(CG)] private static extern void CGEventSetFlags(IntPtr evt, ulong flags);
    [DllImport(CG)] private static extern void CGEventSetIntegerValueField(IntPtr evt, uint field, long value);
    [DllImport(CG, CharSet = CharSet.Unicode)] private static extern void CGEventKeyboardSetUnicodeString(IntPtr evt, nuint length, string text);
    [DllImport(CG)] private static extern void CGEventPost(uint tap, IntPtr evt);
    [DllImport(CG)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool CGPreflightScreenCaptureAccess();
    [DllImport(CG)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool CGRequestScreenCaptureAccess();
    [DllImport(AppServices)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool AXIsProcessTrustedWithOptions(IntPtr options);
    [DllImport(CF)] private static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count, IntPtr keyCallBacks, IntPtr valueCallBacks);

    private const uint HidTap = 0;                 // kCGHIDEventTap
    private const uint LeftDown = 1, LeftUp = 2, RightDown = 3, RightUp = 4, Moved = 5, LeftDragged = 6, RightDragged = 7;
    private const uint ClickStateField = 1;        // kCGMouseEventClickState
    private const uint PixelUnits = 0;             // kCGScrollEventUnitPixel
    internal const ulong ShiftFlag = 0x20000, CtrlFlag = 0x40000, AltFlag = 0x80000, CmdFlag = 0x100000;

    // ---------- permissions ----------

    private static bool _screenAsked, _inputAsked;

    public static bool ScreenAccess() { try { return CGPreflightScreenCaptureAccess(); } catch { return false; } }

    /// <summary>Shows macOS's Screen Recording prompt, once per run (macOS itself only shows it once anyway).</summary>
    public static void AskScreenAccess()
    {
        if (_screenAsked) return;
        _screenAsked = true;
        try { CGRequestScreenCaptureAccess(); } catch { }
    }

    public static bool InputTrusted() { try { return AXIsProcessTrusted() != 0; } catch { return false; } }

    /// <summary>Shows macOS's Accessibility prompt (AXIsProcessTrustedWithOptions + kAXTrustedCheckOptionPrompt), once per run.</summary>
    public static void AskInputTrust()
    {
        if (_inputAsked) return;
        _inputAsked = true;
        IntPtr dict = 0;
        try
        {
            var ax = NativeLibrary.Load(AppServices);
            var cf = NativeLibrary.Load(CF);
            var key = Marshal.ReadIntPtr(NativeLibrary.GetExport(ax, "kAXTrustedCheckOptionPrompt"));
            var yes = Marshal.ReadIntPtr(NativeLibrary.GetExport(cf, "kCFBooleanTrue"));
            dict = CFDictionaryCreate(0, [key], [yes], 1, NativeLibrary.GetExport(cf, "kCFTypeDictionaryKeyCallBacks"), NativeLibrary.GetExport(cf, "kCFTypeDictionaryValueCallBacks"));
            if (dict != 0) AXIsProcessTrustedWithOptions(dict);
        }
        catch { }
        finally { if (dict != 0) CFRelease(dict); }
    }

    // ---------- displays (points) ----------

    /// <summary>Active displays in CGGetActiveDisplayList order (the main display first), the order ffmpeg numbers its
    /// "Capture screen N" devices and screencapture its -D displays.</summary>
    public static IReadOnlyList<RemoteDisplay> RemoteDisplays()
    {
        if (!OperatingSystem.IsMacOS()) return [];
        try
        {
            var buf = new uint[32];
            if (CGGetActiveDisplayList((uint)buf.Length, buf, out var n) != 0) return [];
            var list = buf.Take((int)n).Select(id => (Id: id, Builtin: CGDisplayIsBuiltin(id) != 0)).ToList();
            var main = CGMainDisplayID();
            var info = ProfilerInfo(list);
            return list.Select((d, i) =>
            {
                var b = CGDisplayBounds(d.Id);
                var px = (int)CGDisplayPixelsWide(d.Id);
                var mode = CGDisplayCopyDisplayMode(d.Id);
                if (mode != 0) { try { px = (int)CGDisplayModeGetPixelWidth(mode); } finally { CGDisplayModeRelease(mode); } }
                info.TryGetValue(d.Id, out var p);
                var name = d.Builtin ? "Built-in Display" : p.Name ?? $"Display {i + 1}";
                return new RemoteDisplay(d.Id, name, d.Id == main, b.X, b.Y, b.W, b.H, px);
            }).ToList();
        }
        catch { return []; }
    }

    /// <summary>A display's bounds in global points, or null when it is not active.</summary>
    public static (double X, double Y, double W, double H)? DisplayRect(uint display)
    {
        try
        {
            var buf = new uint[32];
            if (CGGetActiveDisplayList((uint)buf.Length, buf, out var n) != 0 || !buf.Take((int)n).Contains(display)) return null;
            var b = CGDisplayBounds(display);
            return b.W > 0 && b.H > 0 ? (b.X, b.Y, b.W, b.H) : null;
        }
        catch { return null; }
    }

    // ---------- events ----------

    private static readonly Lock InputGate = new();
    private static int _held = -1; // the mouse button held down (0 left, 1 right), -1 none: moves become drags

    private static void Post(IntPtr evt)
    {
        if (evt == 0) throw new InvalidOperationException("CoreGraphics did not create the event");
        try { CGEventPost(HidTap, evt); } finally { CFRelease(evt); }
    }

    private static void Mouse(uint type, double x, double y, uint button, int clicks = 0)
    {
        var evt = CGEventCreateMouseEvent(0, type, new CGPoint { X = x, Y = y }, button);
        if (evt != 0 && clicks > 0) CGEventSetIntegerValueField(evt, ClickStateField, clicks);
        Post(evt);
    }

    /// <summary>Moves the pointer to a global point; a drag while a button is held.</summary>
    public static void MoveTo(double x, double y)
    {
        lock (InputGate) Mouse(_held switch { 0 => LeftDragged, 1 => RightDragged, _ => Moved }, x, y, _held == 1 ? 1u : 0u);
    }

    /// <summary>Button 0 (left) or 1 (right) down or up at a global point.</summary>
    public static void Button(double x, double y, int button, bool down)
    {
        lock (InputGate)
        {
            Mouse(button == 1 ? (down ? RightDown : RightUp) : (down ? LeftDown : LeftUp), x, y, button == 1 ? 1u : 0u, 1);
            _held = down ? button : -1;
        }
    }

    /// <summary>A click (count 1) or double click (count 2) at a global point.</summary>
    public static void Click(double x, double y, int button, int count)
    {
        lock (InputGate)
        {
            for (var i = 1; i <= count; i++)
            {
                Mouse(button == 1 ? RightDown : LeftDown, x, y, button == 1 ? 1u : 0u, i);
                Mouse(button == 1 ? RightUp : LeftUp, x, y, button == 1 ? 1u : 0u, i);
            }
            _held = -1;
        }
    }

    /// <summary>Pixel scroll in CoreGraphics' sense: positive <paramref name="wheelY"/> scrolls up, positive <paramref name="wheelX"/> left.</summary>
    public static void Scroll(int wheelY, int wheelX)
    {
        if (wheelY == 0 && wheelX == 0) return;
        lock (InputGate) Post(CGEventCreateScrollWheelEvent2(0, PixelUnits, 2, wheelY, wheelX, 0));
    }

    /// <summary>Presses and releases one key (a virtual key code) with modifier flags.</summary>
    public static void Key(ushort keycode, ulong flags)
    {
        lock (InputGate)
        {
            foreach (var down in new[] { true, false })
            {
                var evt = CGEventCreateKeyboardEvent(0, keycode, down);
                if (evt != 0) CGEventSetFlags(evt, flags);
                Post(evt);
            }
        }
    }

    /// <summary>Types text: Unicode-string key events of at most 20 UTF-16 units each, Return for line breaks.</summary>
    public static void Type(string text)
    {
        lock (InputGate)
        {
            foreach (var chunk in RemoteDesktop.TextChunks(text))
            {
                if (chunk == "\n") { Key(36, 0); continue; }
                foreach (var down in new[] { true, false })
                {
                    var evt = CGEventCreateKeyboardEvent(0, 0, down);
                    if (evt != 0) CGEventSetUnicodeString(evt, chunk);
                    Post(evt);
                }
            }
        }

        static void CGEventSetUnicodeString(IntPtr evt, string s) => CGEventKeyboardSetUnicodeString(evt, (nuint)s.Length, s);
    }

    // ---------- relative (Trackpad widget) ----------

    /// <summary>Moves the pointer by a delta in points, kept within the displays' bounding box.</summary>
    public static void MoveBy(int dx, int dy)
    {
        if (Pointer() is not { } p) return;
        var ds = RemoteDisplays();
        double x = p.X + dx, y = p.Y + dy;
        if (ds.Count > 0)
        {
            x = Math.Clamp(x, ds.Min(d => d.X), ds.Max(d => d.X + d.W) - 1);
            y = Math.Clamp(y, ds.Min(d => d.Y), ds.Max(d => d.Y + d.H) - 1);
        }
        MoveTo(x, y);
    }

    public static void ClickHere(int button) { if (Pointer() is { } p) Click(p.X, p.Y, button, 1); }

    public static void ButtonHere(int button, bool down) { if (Pointer() is { } p) Button(p.X, p.Y, button, down); }
}
