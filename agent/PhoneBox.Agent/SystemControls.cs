namespace PhoneBox.Agent;

public sealed record SystemDisplay(string Id, string Name, bool Builtin, bool Main, bool Focused, int Width, int Height, int Hz, int? Brightness);

/// <summary>
/// /api/system/*: list displays and focus one, set display / keyboard backlight brightness, Spotlight search.
/// macOS uses <c>MacSystem</c>; elsewhere displays come from the Control Deck's native monitors and the rest is unsupported.
/// </summary>
internal static class SystemControls
{
    public const int MaxQuery = 200;

    private static IReadOnlyList<SystemDisplay> Displays(ControlDeck deck)
    {
        try
        {
#if !WINDOWS
            if (OperatingSystem.IsMacOS()) return MacSystem.Displays();
#endif
            return deck.Monitors().Select(m => new SystemDisplay(m.Id, m.Name, false, false, false, m.Width, m.Height, m.Hz, m.Brightness)).ToList();
        }
        catch { return []; }
    }

    private static int? KeyboardBrightness()
    {
#if !WINDOWS
        if (OperatingSystem.IsMacOS()) return MacSystem.KeyboardBrightness();
#endif
        return null;
    }

    public static object Snapshot(ControlDeck deck)
    {
        var keyboard = KeyboardBrightness();
        return new { displays = Displays(deck), keyboard = new { available = keyboard is not null, brightness = keyboard }, search = OperatingSystem.IsMacOS() };
    }

    internal static bool ValidLevel(int? value) => value is >= 0 and <= 100;

    /// <summary>The query as it will be typed (control characters dropped, so it cannot press Return), or null when too long.</summary>
    internal static string? CleanQuery(string? q)
    {
        var clean = new string((q ?? "").Where(c => !char.IsControl(c)).ToArray());
        return clean.Length > MaxQuery ? null : clean;
    }

    private static IResult Error(string message) => Results.BadRequest(new { error = message });
    private static readonly IResult Ok = Results.Json(new { ok = true });

    public static IResult Focus(string? id, ControlDeck deck)
    {
        if (!OperatingSystem.IsMacOS()) return Error("Focusing a display is supported on macOS only");
        if (!Displays(deck).Any(d => d.Id == id) || !uint.TryParse(id, out var display)) return Results.NotFound();
#if !WINDOWS
        if (MacSystem.Focus(display)) return Ok;
#endif
        return Error("Could not move the pointer");
    }

    public static IResult Brightness(string? id, int? value, ControlDeck deck)
    {
        if (!ValidLevel(value)) return Error("value must be 0-100");
        var display = Displays(deck).FirstOrDefault(d => d.Id == id);
        if (display is null) return Results.NotFound();
#if !WINDOWS
        if (OperatingSystem.IsMacOS())
            return uint.TryParse(id, out var cg) && MacSystem.SetBrightness(cg, value!.Value) ? Ok : Error("This display's brightness cannot be changed");
#endif
        return deck.MonitorBrightness(display.Id, value!.Value) ? Ok : Error("This display's brightness cannot be changed");
    }

    public static IResult Keyboard(int? value)
    {
        if (!ValidLevel(value)) return Error("value must be 0-100");
#if !WINDOWS
        if (OperatingSystem.IsMacOS() && MacSystem.SetKeyboardBrightness(value!.Value)) return Ok;
#endif
        return Error("Keyboard backlight is unavailable");
    }

    public static IResult Search(string? q)
    {
        if (CleanQuery(q) is not { } query) return Error($"Search text is limited to {MaxQuery} characters");
        if (!OperatingSystem.IsMacOS()) return Error("Spotlight search is supported on macOS only");
#if !WINDOWS
        if (MacSystem.Spotlight(query) is { } err) return Error(err);
        return Ok;
#else
        return Error("Spotlight search is supported on macOS only");
#endif
    }
}
