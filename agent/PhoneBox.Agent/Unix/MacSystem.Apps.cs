using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

public sealed record RunningApp(string BundleId, string Name, int Pid, bool Active, bool Hidden);

/// <summary>Window switcher: running Dock apps (lsappinfo, no permission needed), their icons, activating one and
/// moving its front window to another display (System Events, needs Accessibility).</summary>
internal static partial class MacSystem
{
    private static readonly Lock AppsGate = new();
    private static (long At, IReadOnlyList<RunningApp> Apps, Dictionary<string, string> Paths)? _apps;

    /// <summary>Running regular apps, frontmost first; cached for a second.</summary>
    public static IReadOnlyList<RunningApp> RunningApps() => AppsSnapshot().Apps;

    private static (long At, IReadOnlyList<RunningApp> Apps, Dictionary<string, string> Paths) AppsSnapshot()
    {
        lock (AppsGate)
        {
            if (_apps is { } hit && Environment.TickCount64 - hit.At < 1000) return hit;
            var list = Sh.RunWithTimeout(3000, "lsappinfo", "list");
            var visible = Sh.RunWithTimeout(3000, "lsappinfo", "visibleProcessList");
            var parsed = list is null ? [] : ParseLsappinfo(list, visible ?? "");
            _apps = (Environment.TickCount64, parsed.Select(p => p.App).ToList(), parsed.GroupBy(p => p.App.BundleId).ToDictionary(g => g.Key, g => g.First().Path));
            return _apps.Value;
        }
    }

    [GeneratedRegex(@"^\s*\d+\)\s+""(.*)""\s+ASN:(0x[0-9a-fA-F]+-0x[0-9a-fA-F]+):(.*)$")] private static partial Regex LsHeader();
    [GeneratedRegex(@"ASN:(0x[0-9a-fA-F]+-0x[0-9a-fA-F]+)-""")] private static partial Regex LsVisible();

    /// <summary>
    /// Parses <c>lsappinfo list</c>: only type="Foreground" apps with a bundle id (the Dock apps). Order and visibility
    /// come from <c>lsappinfo visibleProcessList</c> (front to back; a hidden app is not in it).
    /// </summary>
    internal static List<(RunningApp App, string Path)> ParseLsappinfo(string list, string visibleList)
    {
        var order = LsVisible().Matches(visibleList).Select(m => m.Groups[1].Value).ToList();
        var apps = new List<(RunningApp App, string Path, string Asn)>();
        string? name = null, asn = null, bundle = null, path = null, type = null; int pid = 0; bool front = false;
        void Flush()
        {
            if (name is not null && type == "Foreground" && !string.IsNullOrEmpty(bundle) && pid > 0)
                apps.Add((new RunningApp(bundle, name, pid, front, !order.Contains(asn!)), path ?? "", asn!));
            name = asn = bundle = path = type = null; pid = 0; front = false;
        }
        foreach (var raw in list.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (LsHeader().Match(line) is { Success: true } h)
            {
                Flush();
                (name, asn, front) = (h.Groups[1].Value, h.Groups[2].Value, h.Groups[3].Value.Contains("(in front)"));
                continue;
            }
            if (name is null) continue;
            var t = line.TrimStart();
            if (t.StartsWith("bundleID=\"")) bundle = Quoted(t, "bundleID=");
            else if (t.StartsWith("bundle path=\"")) path = Quoted(t, "bundle path=");
            else if (t.StartsWith("pid = "))
            {
                int.TryParse(t[6..].Split(' ')[0], NumberStyles.None, CultureInfo.InvariantCulture, out pid);
                var ti = t.IndexOf(" type=\"", StringComparison.Ordinal);
                if (ti >= 0) type = Quoted(t[(ti + 1)..], "type=");
            }
        }
        Flush();
        int Rank((RunningApp App, string Path, string Asn) a) { var i = order.IndexOf(a.Asn); return a.App.Active ? -1 : i < 0 ? int.MaxValue : i; }
        return apps.OrderBy(Rank).Select(a => (a.App, a.Path)).ToList(); // OrderBy is stable: hidden apps keep lsappinfo order

        static string? Quoted(string s, string key)
        {
            if (!s.StartsWith(key + "\"")) return null;
            var end = s.IndexOf('"', key.Length + 1);
            return end < 0 ? null : s[(key.Length + 1)..end];
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-_]{0,254}$")] private static partial Regex BundlePattern();

    /// <summary>A bundle id safe to hand to mdfind / open / AppleScript (letters, digits, '.', '-', '_').</summary>
    internal static bool ValidBundleId(string? id) => id is not null && BundlePattern().IsMatch(id);

    public static bool IsRunning(string bundle) => RunningApps().Any(a => a.BundleId == bundle);

    private static readonly ConcurrentDictionary<string, byte[]> AppIcons = new();

    /// <summary>A 128 px PNG of the app's icon, or null. The .app comes from the running list, else Spotlight.</summary>
    public static byte[]? AppIcon(string bundle)
    {
        if (!ValidBundleId(bundle)) return null;
        if (AppIcons.TryGetValue(bundle, out var png)) return png;
        AppsSnapshot().Paths.TryGetValue(bundle, out var path);
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            path = Sh.RunWithTimeout(5000, "mdfind", $"kMDItemCFBundleIdentifier == \"{bundle}\"")?
                .Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.EndsWith(".app", StringComparison.Ordinal) && Directory.Exists(l));
        if (path is null || AppCatalog.MakeIcon(path, 128) is not { } made) return null;
        return AppIcons[bundle] = made;
    }

    public static bool Activate(string bundle) => Sh.RunWithTimeout(5000, "/usr/bin/open", "-b", bundle) is not null;

    // ---------- moving a window ----------

    /// <summary>osascript argv that prints "x y w h ww wh": the display's visible frame (top-left origin, like System
    /// Events) and the app's front window size. Bundle and display id are run-handler arguments, never script text.</summary>
    internal static string[] WindowProbeArgs(string bundle, uint display) =>
    [
        "-e", "use framework \"AppKit\"",
        "-e", "use scripting additions",
        "-e", "on run argv",
        "-e", "set ss to current application's NSScreen's screens()",
        "-e", "set mainH to item 2 of item 2 of ((item 1 of ss)'s frame() as list)",
        "-e", "repeat with s in ss",
        "-e", "if ((s's deviceDescription()'s objectForKey:\"NSScreenNumber\") as integer) = ((item 2 of argv) as integer) then",
        "-e", "set {{x, y}, {w, h}} to (s's visibleFrame() as list)",
        "-e", "tell application \"System Events\" to set {ww, wh} to size of window 1 of (first application process whose bundle identifier is (item 1 of argv))",
        "-e", "return ((x as integer) as text) & \" \" & (((mainH - y - h) as integer) as text) & \" \" & ((w as integer) as text) & \" \" & ((h as integer) as text) & \" \" & ww & \" \" & wh",
        "-e", "end if",
        "-e", "end repeat",
        "-e", "return \"\"",
        "-e", "end run",
        "--", bundle, display.ToString(CultureInfo.InvariantCulture),
    ];

    internal static string[] WindowMoveArgs(string bundle, (int X, int Y, int W, int H) r) =>
    [
        "-e", "on run argv",
        "-e", "set {px, py, sw, sh} to {(item 2 of argv) as integer, (item 3 of argv) as integer, (item 4 of argv) as integer, (item 5 of argv) as integer}",
        "-e", "tell application \"System Events\"",
        "-e", "set w to window 1 of (first application process whose bundle identifier is (item 1 of argv))",
        "-e", "set position of w to {px, py}",
        "-e", "set size of w to {sw, sh}",
        "-e", "set position of w to {px, py}", // again: a resize near the edge can nudge it
        "-e", "end tell",
        "-e", "end run",
        "--", bundle, .. new[] { r.X, r.Y, r.W, r.H }.Select(v => v.ToString(CultureInfo.InvariantCulture)),
    ];

    /// <summary>Parses the probe's "x y w h ww wh".</summary>
    internal static ((int X, int Y, int W, int H) Visible, int W, int H)? ParseWindowProbe(string? text)
    {
        var p = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var n = new int[6];
        if (p.Length != 6 || p.Select((s, i) => int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n[i])).Any(ok => !ok)) return null;
        if (n[2] <= 0 || n[3] <= 0 || n[4] <= 0 || n[5] <= 0) return null;
        return ((n[0], n[1], n[2], n[3]), n[4], n[5]);
    }

    /// <summary>The window's size shrunk to fit the visible frame, centred in it.</summary>
    internal static (int X, int Y, int W, int H) FitCentered((int X, int Y, int W, int H) visible, int w, int h)
    {
        w = Math.Min(w, visible.W);
        h = Math.Min(h, visible.H);
        return (visible.X + (visible.W - w) / 2, visible.Y + (visible.H - h) / 2, w, h);
    }

    [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern byte AXIsProcessTrusted();

    private static bool AccessibilityTrusted() { try { return AXIsProcessTrusted() != 0; } catch { return true; } }

    /// <summary>Null on success, else a message for the phone.</summary>
    public static string? MoveWindow(string bundle, uint display)
    {
        var probe = Exec(8000, "osascript", WindowProbeArgs(bundle, display));
        if (probe.Code != 0 || ParseWindowProbe(probe.Out.Trim()) is not { } found)
            return probe.Err.Contains("assistive", StringComparison.OrdinalIgnoreCase) || !AccessibilityTrusted()
                ? KeySender.AccessibilityHint : "This app has no window to move";
        var move = Exec(8000, "osascript", WindowMoveArgs(bundle, FitCentered(found.Visible, found.W, found.H)));
        return move.Code == 0 ? null : KeySender.AccessibilityHint;
    }
}
