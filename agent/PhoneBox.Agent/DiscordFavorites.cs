using System.Globalization;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// Favourite Discord servers and voice channels of one Discord widget, picked on the phone. Like <see cref="NoteEdit"/>,
/// a narrow write into the saved settings: the phone sends a server or channel id and whether it is a favourite; only
/// ids the agent itself reported (in the current snapshot) are accepted, and only that widget's two lists change.
/// </summary>
public static class DiscordFavorites
{
    public const string GuildsKey = "favGuilds";
    public const string ChannelsKey = "favChannels";
    /// <summary>The most favourites a list keeps: the panel shows few, and the agent reads the people of at most <see cref="MaxRooms"/> channels.</summary>
    public const int MaxEach = 30;

    public sealed record Result(int Status, string? Error = null, string? Version = null);

    /// <summary>A Discord id (snowflake): digits only, as the agent stores and compares them.</summary>
    public static bool IsId(string? id) => id is { Length: > 0 and <= 24 } && id.All(char.IsAsciiDigit);

    /// <summary>The ids in a widget's favourite list, cleaned of anything that is not an id (a hand-edited settings file).</summary>
    public static IReadOnlyList<string> Read(WidgetConfig w, string key) =>
        w.Opt(key) is { ValueKind: JsonValueKind.Array } a
            ? [.. a.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()).Where(IsId).Distinct().Take(MaxEach).Select(s => s!)]
            : [];

    /// <summary>The most voice channels whose people are read per pass (one GET_CHANNEL each).</summary>
    public const int MaxRooms = 20;

    /// <summary>
    /// The voice channels to read occupancy for, over every Discord widget on an enabled page that shows channels: what
    /// the panel shows because of the favourites, so only the starred channels of a server that has some, and all of a
    /// favourite server without. Starred channels come first, so they are read even when the cap is reached.
    /// </summary>
    public static List<DiscordChannel> WantedRooms(AgentConfig cfg, IReadOnlyCollection<DiscordChannel> known)
    {
        List<DiscordChannel> starred = [], rest = [];
        foreach (var w in cfg.Widgets(enabledPagesOnly: true).Where(w => w.Type == "discord" && w.Bool("channels") != false))
        {
            var guilds = Read(w, GuildsKey);
            var channels = Read(w, ChannelsKey);
            var mine = known.Where(c => channels.Contains(c.Id)).ToList();
            starred.AddRange(mine);
            rest.AddRange(known.Where(c => c.GuildId is { } g && guilds.Contains(g) && mine.All(m => m.GuildId != g)));
        }
        return [.. starred.Concat(rest).DistinctBy(c => c.Id).Take(MaxRooms)];
    }

    /// <param name="kind">"guild" or "channel".</param>
    /// <param name="known">Whether the agent reported this id (a server or voice channel in its current list).</param>
    public static Result Apply(ConfigStore cfg, string pageId, string widgetId, string? kind, string? id, bool on,
        Func<string, string, bool> known, long? expectedVersion = null)
    {
        var key = kind switch { "guild" => GuildsKey, "channel" => ChannelsKey, _ => null };
        if (key is null) return new(400, "kind must be guild or channel");
        if (!IsId(id)) return new(400, "not a Discord id");
        if (on && !known(kind!, id!)) return new(400, "unknown server or channel");

        // Work on a copy: cfg.Get() is the shared, cached config, and a save may still refuse.
        var version = expectedVersion ?? cfg.Version;
        var copy = JsonSerializer.Deserialize<AgentConfig>(JsonSerializer.Serialize(cfg.Get(), ConfigStore.WriteOpts), ConfigStore.WriteOpts);
        var widget = copy?.Pages?.FirstOrDefault(p => p.Id == pageId)?.Widgets?.FirstOrDefault(w => w.Id == widgetId);
        if (widget is null) return new(404, "widget not found");
        if (widget.Type != "discord") return new(400, "option not allowed");

        // also when nothing would change: the answer carries the version, and the phone's copy of the settings is older than that
        if (version != cfg.Version) return new(409, ConfigStore.Conflict);
        var list = Read(widget, key).ToList();
        if (on == list.Contains(id!)) return new(200, null, cfg.Version.ToString(CultureInfo.InvariantCulture)); // already so: no save
        if (on && list.Count >= MaxEach) return new(400, "too many favourites");
        if (on) list.Add(id!); else list.Remove(id!);
        widget.Set(key, list);
        var err = cfg.Save(JsonSerializer.Serialize(copy, ConfigStore.WriteOpts), version, out var savedVersion);
        if (err == ConfigStore.Conflict) return new(409, err);
        return err is null ? new(200, null, savedVersion.ToString(CultureInfo.InvariantCulture)) : new(400, err);
    }
}
