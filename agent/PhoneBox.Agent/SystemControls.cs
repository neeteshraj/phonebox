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
        bool caffeinated = false, focusShortcuts = false;
#if !WINDOWS
        if (OperatingSystem.IsMacOS()) (caffeinated, focusShortcuts) = (MacSystem.Caffeinated, MacSystem.FocusShortcuts());
#endif
        return new
        {
            displays = Displays(deck), keyboard = new { available = keyboard is not null, brightness = keyboard }, search = OperatingSystem.IsMacOS(),
            power = new { caffeinated, focusShortcuts },
        };
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

    private static IResult MacOnly => Error("Supported on macOS only");
    private static IResult Done(string? error) => error is null ? Ok : Error(error);

#if WINDOWS
    public static void MapMacRoutes(WebApplication app) { } // window switcher, clipboard, power, audio outputs: macOS only
#else
    public static void MapMacRoutes(WebApplication app)
    {
        app.MapGet("/api/system/apps", Apps);
        app.MapGet("/api/system/apps/icon", (string? bundle, HttpResponse res) => AppIcon(bundle, res));
        app.MapPost("/api/system/apps/activate", (string? bundle) => Activate(bundle));
        app.MapPost("/api/system/apps/move", (string? bundle, string? display, ControlDeck deck) => Move(bundle, display, deck));
        app.MapGet("/api/system/clipboard", Clipboard);
        app.MapPost("/api/system/clipboard", (string? text) => SetClipboard(text));
        app.MapPost("/api/system/caffeinate", (bool? on) => Caffeinate(on));
        app.MapPost("/api/system/display/sleep", DisplaySleep);
        app.MapPost("/api/system/lock", Lock);
        app.MapPost("/api/system/focus", (bool? on) => Focus(on));
        app.MapGet("/api/system/audio", Audio);
        app.MapPost("/api/system/audio/output", (string? id) => AudioOutput(id));
    }

    // ---------- window switcher ----------

    public static IResult Apps() => OperatingSystem.IsMacOS()
        ? Results.Json(MacSystem.RunningApps())
        : Results.Json(Array.Empty<RunningApp>());

    public static IResult AppIcon(string? bundle, HttpResponse res)
    {
        if (!OperatingSystem.IsMacOS() || !MacSystem.ValidBundleId(bundle) || MacSystem.AppIcon(bundle!) is not { } png) return Results.NotFound();
        res.Headers.CacheControl = "private, max-age=86400";
        return Results.File(png, "image/png");
    }

    public static IResult Activate(string? bundle)
    {
        if (!OperatingSystem.IsMacOS()) return MacOnly;
        if (!MacSystem.ValidBundleId(bundle) || !MacSystem.IsRunning(bundle!)) return Results.NotFound();
        return MacSystem.Activate(bundle!) ? Ok : Error("Could not bring the app to the front");
    }

    public static IResult Move(string? bundle, string? display, ControlDeck deck)
    {
        if (!OperatingSystem.IsMacOS()) return MacOnly;
        if (!MacSystem.ValidBundleId(bundle) || !MacSystem.IsRunning(bundle!)) return Results.NotFound();
        if (!Displays(deck).Any(d => d.Id == display) || !uint.TryParse(display, out var id)) return Results.NotFound();
        return Done(MacSystem.MoveWindow(bundle!, id));
    }

    // ---------- clipboard ----------

    public static IResult Clipboard() => OperatingSystem.IsMacOS() ? Results.Json(new { text = MacSystem.Clipboard() }) : MacOnly;

    public static IResult SetClipboard(string? text)
    {
        if (!OperatingSystem.IsMacOS()) return MacOnly;
        if (text is null) return Error("text is required");
        if (text.Length > MacSystem.MaxClipboard) return Error($"Clipboard text is limited to {MacSystem.MaxClipboard} characters");
        return MacSystem.SetClipboard(text) ? Ok : Error("Could not set the clipboard");
    }

    // ---------- power ----------

    public static IResult Caffeinate(bool? on)
    {
        if (!OperatingSystem.IsMacOS()) return MacOnly;
        if (on is null) return Error("on must be true or false");
        return MacSystem.Caffeinate(on.Value) ? Ok : Error("Could not start caffeinate");
    }

    public static IResult DisplaySleep() => !OperatingSystem.IsMacOS() ? MacOnly : MacSystem.DisplaySleep() ? Ok : Error("Could not sleep the display");

    public static IResult Lock() => OperatingSystem.IsMacOS() ? Done(MacSystem.LockScreen()) : MacOnly;

    public static IResult Focus(bool? on)
    {
        if (!OperatingSystem.IsMacOS()) return MacOnly;
        if (on is null) return Error("on must be true or false");
        return Done(MacSystem.Focus(on.Value));
    }

    // ---------- audio output ----------

    public static IResult Audio() => Results.Json(new { outputs = MacAudioProbe.Outputs() });

    public static IResult AudioOutput(string? id)
    {
        if (!OperatingSystem.IsMacOS()) return MacOnly;
        return !string.IsNullOrEmpty(id) && MacAudioProbe.SetDefaultOutput(id) ? Ok : Results.NotFound();
    }
#endif
}
