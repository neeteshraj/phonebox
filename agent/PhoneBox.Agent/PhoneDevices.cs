namespace PhoneBox.Agent;

/// <summary>
/// The phone mic as seen from the phone: its state in /api/state (for a Custom HTML widget) and the shortcut command
/// that acts on it, so the panel needs no new widget or app build:
/// <list type="bullet">
/// <item><c>builtin:phone-mic</c>: toggles mute (args "mute" / "unmute" set it); as a toggle key it shows on while live.</item>
/// </list>
/// Set once by Program; null in tests and on platforms without it.
/// </summary>
public static class PhoneDevices
{
    public const string MicCmd = "builtin:phone-mic";

    public static PhoneMic? Mic { get; set; }

    public static bool IsCommand(string? cmd) => cmd is MicCmd;

    /// <summary>Runs one of the commands; throws with a message for the phone when it cannot.</summary>
    public static void Run(string cmd, string? args)
    {
        switch (cmd)
        {
            case MicCmd:
                if (Mic is not { Enabled: true } mic) throw new InvalidOperationException("the phone mic is off in the agent's settings");
                switch ((args ?? "").Trim().ToLowerInvariant())
                {
                    case "mute": mic.SetMuted(true); break;
                    case "unmute": mic.SetMuted(false); break;
                    default: mic.Toggle(); break;
                }
                break;
        }
    }

    /// <summary>A toggle key's state: the mic counts as on while it is live and not muted.</summary>
    public static bool? MicOn => Mic is { Enabled: true } m ? m.State == "live" && !m.Muted : null;
}
