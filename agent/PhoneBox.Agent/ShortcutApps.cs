using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>
/// What a shortcut is, for the launcher on the phone: a built-in action ("builtin:mute", "builtin:lock" …) or an app,
/// and on macOS which app (its bundle id), so the phone can mark the ones that are running.
/// </summary>
internal static partial class ShortcutApps
{
    private static readonly ConcurrentDictionary<string, string?> BundleByApp = new(StringComparer.OrdinalIgnoreCase);

    public static string Kind(Shortcut s) => s.Cmd.StartsWith("builtin:", StringComparison.Ordinal) ? "action" : "app";

    /// <summary>The app a shortcut opens: "open -a Name" → Name, "open &lt;folder&gt;" → Finder; null otherwise.</summary>
    internal static string? AppName(string cmd, string? args)
    {
        if (cmd != "open" || string.IsNullOrWhiteSpace(args)) return null;
        var m = OpenA().Match(args);
        if (m.Success) return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        return args.TrimStart().StartsWith('-') ? null : "Finder";
    }

    /// <summary>The bundle id of the app a shortcut opens (macOS), looked up once per app name through LaunchServices.</summary>
    public static string? Bundle(Shortcut s)
    {
#if WINDOWS
        return null;
#else
        if (!OperatingSystem.IsMacOS() || AppName(s.Cmd, s.Args) is not { } app) return null;
        return BundleByApp.GetOrAdd(app, name =>
        {
            // the app's name travels as an argument, never inside the script text
            var id = Sh.RunWithTimeout(3000, "osascript", "-e", "on run argv", "-e", "id of application (item 1 of argv)", "-e", "end run", name)?.Trim();
            return string.IsNullOrEmpty(id) ? null : id;
        });
#endif
    }

    [GeneratedRegex("""-a\s+(?:"([^"]+)"|(\S+))""")]
    private static partial Regex OpenA();
}
