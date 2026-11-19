using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>Clipboard, power (caffeinate, display sleep, lock, Focus via Shortcuts) and external-display brightness over
/// DDC (m1ddc).</summary>
internal static partial class MacSystem
{
    /// <summary>Runs a tool without a shell. UTF-8 locale so pbcopy / pbpaste keep non-ASCII text; optional stdin.</summary>
    private static (int Code, string Out, string Err) Exec(int timeoutMs, string tool, string[] args, string? stdin = null)
    {
        var path = tool.Contains('/') ? tool : Sh.Which(tool);
        if (path is null) return (-1, "", "");
        try
        {
            var psi = new ProcessStartInfo(path)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppPaths.NeutralCwd,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin is not null,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            if (stdin is not null) psi.StandardInputEncoding = new UTF8Encoding(false);
            psi.Environment["LANG"] = "en_US.UTF-8";
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            if (stdin is not null) { p.StandardInput.Write(stdin); p.StandardInput.Close(); }
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-1, "", "timed out"); }
            return (p.ExitCode, output.Result, error.Result);
        }
        catch (Exception e) { return (-1, "", e.Message); }
    }

    // ---------- clipboard ----------

    public const int MaxClipboard = 100_000;

    /// <summary>The clipboard's plain text (at most <see cref="MaxClipboard"/> chars), or null when it holds none.</summary>
    public static string? Clipboard()
    {
        var r = Exec(3000, "pbpaste", ["-Prefer", "txt"]);
        return r.Code != 0 || r.Out.Length == 0 ? null : r.Out.Length > MaxClipboard ? r.Out[..MaxClipboard] : r.Out;
    }

    public static bool SetClipboard(string text) => Exec(3000, "pbcopy", [], text).Code == 0;

    // ---------- power ----------

    private static readonly Lock PowerGate = new();
    private static Process? _caffeinate;

    public static bool Caffeinated { get { lock (PowerGate) return _caffeinate is { HasExited: false }; } }

    /// <summary>Starts / stops a <c>caffeinate -di</c> child. <c>-w</c> ties it to the agent: it ends when the agent does,
    /// even after a crash.</summary>
    public static bool Caffeinate(bool on)
    {
        lock (PowerGate)
        {
            if (!on)
            {
                try { if (_caffeinate is { HasExited: false }) _caffeinate.Kill(); } catch { }
                _caffeinate?.Dispose();
                _caffeinate = null;
                return true;
            }
            if (_caffeinate is { HasExited: false }) return true;
            if (Sh.Which("caffeinate") is not { } exe) return false;
            try
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppPaths.NeutralCwd };
                foreach (var a in new[] { "-di", "-w", Environment.ProcessId.ToString(CultureInfo.InvariantCulture) }) psi.ArgumentList.Add(a);
                _caffeinate = Process.Start(psi);
                return _caffeinate is not null;
            }
            catch { return false; }
        }
    }

    public static bool DisplaySleep() => Sh.RunWithTimeout(5000, "pmset", "displaysleepnow") is not null;

    private const string LoginFramework = "/System/Library/PrivateFrameworks/login.framework/Versions/Current/login";
    private delegate int LockFn();

    /// <summary>SACLockScreenImmediate from the private login framework, or 0 when missing.</summary>
    internal static IntPtr LockSymbol() =>
        OperatingSystem.IsMacOS() && NativeLibrary.TryLoad(LoginFramework, out var lib) && NativeLibrary.TryGetExport(lib, "SACLockScreenImmediate", out var fn) ? fn : 0;

    /// <summary>Locks the screen now; null on success, else a message. Falls back to Ctrl+Cmd+Q.</summary>
    public static string? LockScreen()
    {
        try
        {
            if (LockSymbol() is var fn && fn != 0) { Marshal.GetDelegateForFunctionPointer<LockFn>(fn)(); return null; }
            KeySender.SendCombo(["ctrl", "win", "q"]);
            return null;
        }
        catch (InvalidOperationException e) { return e.Message; }
        catch { return "Could not lock the screen"; }
    }

    // ---------- Focus (Do Not Disturb) through the Shortcuts app ----------

    internal const string FocusOnShortcut = "PhoneBox Focus On", FocusOffShortcut = "PhoneBox Focus Off";
    internal const string FocusHelp = $"Create two shortcuts named \"{FocusOnShortcut}\" and \"{FocusOffShortcut}\" in the Shortcuts app (each with a Set Focus action)";
    private static (long At, bool Has)? _focusShortcuts;

    /// <summary>True when <c>shortcuts list</c> output names both PhoneBox Focus shortcuts exactly.</summary>
    internal static bool HasFocusShortcuts(string? list)
    {
        var names = (list ?? "").Split('\n').Select(l => l.Trim()).ToHashSet(StringComparer.Ordinal);
        return names.Contains(FocusOnShortcut) && names.Contains(FocusOffShortcut);
    }

    public static bool FocusShortcuts()
    {
        lock (PowerGate)
        {
            if (_focusShortcuts is { } hit && Environment.TickCount64 - hit.At < 60_000) return hit.Has;
            var has = HasFocusShortcuts(Sh.RunWithTimeout(10_000, "shortcuts", "list"));
            _focusShortcuts = (Environment.TickCount64, has);
            return has;
        }
    }

    /// <summary>Null on success, else a message.</summary>
    public static string? Focus(bool on)
    {
        lock (PowerGate) _focusShortcuts = null; // just created them? look again
        if (!FocusShortcuts()) return FocusHelp;
        var name = on ? FocusOnShortcut : FocusOffShortcut;
        return Sh.RunWithTimeout(30_000, "shortcuts", "run", name) is null ? $"The shortcut \"{name}\" failed" : null;
    }

    // ---------- external displays over DDC (m1ddc) ----------

    [GeneratedRegex(@"^\[(\d+)\]\s+(.+?)\s+\(([^()]*)\)\s*$")] private static partial Regex M1ddcLine();

    /// <summary>Parses <c>m1ddc display list</c>: "[2] LG ULTRAFINE (uuid)".</summary>
    internal static List<(int Index, string Name)> ParseM1ddcList(string? text) =>
        (text ?? "").Split('\n').Select(l => M1ddcLine().Match(l.Trim())).Where(m => m.Success)
            .Select(m => (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), m.Groups[2].Value)).ToList();

    /// <summary>The DDC level as 0-100, or null unless both readings are sane (max &gt; 0, 0 &lt; value ≤ max). m1ddc prints
    /// 0 when a DDC read fails (an LG UltraFine answered 30/100, 0/0 and 0/100 in a row), so a 0 is never trusted.</summary>
    // ponytail: a monitor really at 0 reads as unsupported until set from here (DdcSet caches it); fine for brightness.
    internal static (int Percent, int Max)? DdcLevel(string? value, string? max) =>
        int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var v)
        && int.TryParse(max?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m > 0 && v > 0 && v <= m
            ? ((int)Math.Round(v * 100.0 / m), m) : null;

    /// <summary>External display id → m1ddc index: by name (case-insensitive), then in order when the leftovers on both
    /// sides are the same count.</summary>
    internal static Dictionary<uint, int> MapDdc(IReadOnlyList<(uint Id, string Name)> externals, IReadOnlyList<(int Index, string Name)> ddc)
    {
        var map = new Dictionary<uint, int>();
        var free = ddc.ToList();
        foreach (var d in externals)
        {
            var i = free.FindIndex(x => string.Equals(x.Name.Trim(), d.Name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (i < 0) continue;
            map[d.Id] = free[i].Index;
            free.RemoveAt(i);
        }
        var left = externals.Where(d => !map.ContainsKey(d.Id)).ToList();
        if (left.Count == free.Count)
            for (var i = 0; i < left.Count; i++) map[left[i].Id] = free[i].Index;
        return map;
    }

    private static readonly Lock DdcGate = new();
    private static (long At, string Key, Dictionary<uint, (int Index, int Percent, int Max)> Levels)? _ddc;

    /// <summary>DDC-capable externals among <paramref name="externals"/> (those with no DisplayServices brightness),
    /// re-read every 30 s: up to four tries per display, and a display that answered before keeps its last level
    /// through a refresh where every read failed.</summary>
    private static Dictionary<uint, (int Index, int Percent, int Max)> DdcLevels(IReadOnlyList<(uint Id, string Name)> externals)
    {
        var key = string.Join(',', externals.Select(e => $"{e.Id}:{e.Name}"));
        lock (DdcGate)
        {
            if (_ddc is { } hit && hit.Key == key && Environment.TickCount64 - hit.At < 30_000) return hit.Levels;
            var levels = new Dictionary<uint, (int, int, int)>();
            if (externals.Count > 0 && Sh.Which("m1ddc") is not null)
                foreach (var (id, index) in MapDdc(externals, ParseM1ddcList(Sh.RunWithTimeout(5000, "m1ddc", "display", "list"))))
                {
                    var n = index.ToString(CultureInfo.InvariantCulture);
                    for (var attempt = 0; attempt < 4 && !levels.ContainsKey(id); attempt++)
                        if (DdcLevel(Sh.RunWithTimeout(3000, "m1ddc", "display", n, "get", "luminance"),
                                Sh.RunWithTimeout(3000, "m1ddc", "display", n, "max", "luminance")) is { } level)
                            levels[id] = (index, level.Percent, level.Max);
                    if (!levels.ContainsKey(id) && _ddc is { } old && old.Key == key && old.Levels.TryGetValue(id, out var kept) && kept.Index == index)
                        levels[id] = kept;
                }
            _ddc = (Environment.TickCount64, key, levels);
            return levels;
        }
    }

    private static bool DdcSet(uint display, int percent)
    {
        (int Index, int Percent, int Max) d;
        lock (DdcGate) { if (_ddc is not { } hit || !hit.Levels.TryGetValue(display, out d)) return false; }
        var raw = (int)Math.Round(Math.Clamp(percent, 0, 100) * d.Max / 100.0);
        var ok = Sh.RunWithTimeout(3000, "m1ddc", "display", d.Index.ToString(CultureInfo.InvariantCulture), "set", "luminance",
            raw.ToString(CultureInfo.InvariantCulture)) is not null;
        lock (DdcGate) if (ok && _ddc is { } c && c.Levels.ContainsKey(display)) c.Levels[display] = d with { Percent = Math.Clamp(percent, 0, 100) };
        return ok;
    }
}
