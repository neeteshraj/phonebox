using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>Favourite Discord servers and channels: the phone's narrow write, and who is in a voice channel.</summary>
public sealed class DiscordFavoritesTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"phonebox-dcfav-{Guid.NewGuid():N}.json");
    private readonly ConfigStore _cfg;

    public DiscordFavoritesTests()
    {
        File.WriteAllText(_path, """
            {"pages":[{"id":"p","title":"P","widgets":[
              {"id":"d","type":"discord","options":{"favGuilds":["111", 5, "x9", "111"],"favChannels":["222"]}},
              {"id":"n","type":"note","options":{"text":"hi"}}]}]}
            """);
        _cfg = new ConfigStore(_path);
    }

    public void Dispose() { try { File.Delete(_path); File.Delete(_path + ".bak"); } catch { } }

    private static bool Known(string kind, string id) => (kind, id) is ("guild", "111" or "333") or ("channel", "222" or "444");
    private WidgetConfig Widget(string id) => _cfg.Get().Pages!.First().Widgets!.First(w => w.Id == id);

    [Fact]
    public void A_hand_edited_list_keeps_only_distinct_ids()
    {
        Assert.Equal(["111"], DiscordFavorites.Read(Widget("d"), DiscordFavorites.GuildsKey));
    }

    [Fact]
    public void A_known_server_is_added_and_removed()
    {
        var r = DiscordFavorites.Apply(_cfg, "p", "d", "guild", "333", true, Known);
        Assert.Equal(200, r.Status);
        Assert.Equal(["111", "333"], DiscordFavorites.Read(Widget("d"), DiscordFavorites.GuildsKey));
        Assert.Equal(200, DiscordFavorites.Apply(_cfg, "p", "d", "guild", "111", false, Known).Status);
        Assert.Equal(["333"], DiscordFavorites.Read(Widget("d"), DiscordFavorites.GuildsKey));
    }

    [Fact]
    public void A_channel_goes_into_the_channel_list()
    {
        Assert.Equal(200, DiscordFavorites.Apply(_cfg, "p", "d", "channel", "444", true, Known).Status);
        Assert.Equal(["222", "444"], DiscordFavorites.Read(Widget("d"), DiscordFavorites.ChannelsKey));
    }

    [Theory]
    [InlineData("guild", "999", "unknown server or channel")] // not in the agent's list
    [InlineData("guild", "12a", "not a Discord id")]
    [InlineData("guild", null, "not a Discord id")]
    [InlineData("user", "111", "kind must be guild or channel")]
    public void Ids_the_agent_did_not_report_or_that_are_not_ids_are_refused(string kind, string? id, string error)
    {
        var before = _cfg.Version;
        var r = DiscordFavorites.Apply(_cfg, "p", "d", kind, id, true, Known);
        Assert.Equal(400, r.Status);
        Assert.Equal(error, r.Error);
        Assert.Equal(before, _cfg.Version);
    }

    [Fact]
    public void Removing_an_id_needs_no_report_so_a_server_you_left_can_still_be_removed()
    {
        Assert.Equal(200, DiscordFavorites.Apply(_cfg, "p", "d", "channel", "222", false, (_, _) => false).Status);
        Assert.Empty(DiscordFavorites.Read(Widget("d"), DiscordFavorites.ChannelsKey));
    }

    [Fact]
    public void Only_a_discord_widget_takes_favourites()
    {
        Assert.Equal(400, DiscordFavorites.Apply(_cfg, "p", "n", "guild", "111", true, Known).Status);
        Assert.Equal(404, DiscordFavorites.Apply(_cfg, "p", "missing", "guild", "111", true, Known).Status);
        Assert.Equal(404, DiscordFavorites.Apply(_cfg, "q", "d", "guild", "111", true, Known).Status);
    }

    [Fact]
    public void Nothing_is_saved_when_the_list_is_already_so()
    {
        var before = _cfg.Version;
        Assert.Equal(200, DiscordFavorites.Apply(_cfg, "p", "d", "guild", "111", true, Known).Status);
        Assert.Equal(before, _cfg.Version);
    }

    [Fact]
    public void A_save_over_newer_settings_is_refused()
    {
        // an old write time, so the next save's differs however coarse the file system's clock is
        File.SetLastWriteTimeUtc(_path, DateTime.UtcNow.AddMinutes(-5));
        _cfg.Get();
        var stale = _cfg.Version;
        Assert.Equal(200, DiscordFavorites.Apply(_cfg, "p", "d", "guild", "333", true, Known).Status);
        Assert.Equal(409, DiscordFavorites.Apply(_cfg, "p", "d", "channel", "444", true, Known, stale).Status);
    }

    [Fact]
    public void A_list_stops_at_its_limit()
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(new
        {
            pages = new[] { new { id = "p", title = "P", widgets = new[] { new { id = "d", type = "discord", options = new
            {
                favGuilds = Enumerable.Range(1, DiscordFavorites.MaxEach).Select(i => i.ToString()).ToArray(),
            } } } } },
        }));
        var cfg = new ConfigStore(_path);
        Assert.Equal("too many favourites", DiscordFavorites.Apply(cfg, "p", "d", "guild", "333", true, Known).Error);
    }

    [Fact]
    public void A_stale_version_is_refused_even_when_nothing_would_change()
    {
        // an old write time, so the next save's differs however coarse the file system's clock is
        File.SetLastWriteTimeUtc(_path, DateTime.UtcNow.AddMinutes(-5));
        _cfg.Get();
        var stale = _cfg.Version;
        Assert.Equal(200, DiscordFavorites.Apply(_cfg, "p", "d", "guild", "333", true, Known).Status);
        Assert.Equal(409, DiscordFavorites.Apply(_cfg, "p", "d", "guild", "333", true, Known, stale).Status);
        Assert.Equal(200, DiscordFavorites.Apply(_cfg, "p", "d", "guild", "333", true, Known, _cfg.Version).Status);
    }

    // ---- which voice channels are read ----

    private static DiscordChannel Room(string id, string guild) => new(id, "Room " + id, "Server " + guild, guild, null);

    private ConfigStore Widgets(params object[] options)
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(new
        {
            pages = new[] { new { id = "p", title = "P", widgets = options.Select((o, i) => new { id = "d" + i, type = "discord", options = o }).ToArray() } },
        }));
        return new ConfigStore(_path);
    }

    private static string[] Wanted(ConfigStore cfg, params DiscordChannel[] known) => [.. DiscordFavorites.WantedRooms(cfg.Get(), known).Select(c => c.Id)];

    [Fact]
    public void A_favourite_channel_is_read_without_the_rest_of_its_server()
    {
        var cfg = Widgets(new { favChannels = new[] { "2" } });
        Assert.Equal(["2"], Wanted(cfg, Room("1", "10"), Room("2", "10"), Room("3", "20")));
    }

    [Fact]
    public void A_favourite_server_is_read_whole_unless_it_has_starred_channels()
    {
        Assert.Equal(["1", "2"], Wanted(Widgets(new { favGuilds = new[] { "10" } }), Room("1", "10"), Room("2", "10"), Room("3", "20")));
        Assert.Equal(["2"], Wanted(Widgets(new { favGuilds = new[] { "10" }, favChannels = new[] { "2" } }), Room("1", "10"), Room("2", "10")));
    }

    [Fact]
    public void Starred_channels_are_read_first_when_there_are_too_many()
    {
        var known = Enumerable.Range(1, 100).Select(i => Room(i.ToString(), "10")).Append(Room("500", "20")).ToArray();
        var wanted = Wanted(Widgets(new { favGuilds = new[] { "10" }, favChannels = new[] { "500" } }), known);
        Assert.Equal(DiscordFavorites.MaxRooms, wanted.Length);
        Assert.Equal("500", wanted[0]);
    }

    [Fact]
    public void Nothing_is_read_for_a_widget_that_does_not_show_channels()
    {
        Assert.Empty(Wanted(Widgets(new { channels = false, favGuilds = new[] { "10" }, favChannels = new[] { "1" } }), Room("1", "10")));
        Assert.Equal(["1"], Wanted(Widgets(new { channels = false, favGuilds = new[] { "20" } }, new { favChannels = new[] { "1" } }), Room("1", "10"), Room("2", "20")));
    }

    [Fact]
    public void Nothing_is_read_without_favourites_or_on_a_disabled_page()
    {
        Assert.Empty(Wanted(Widgets(new { control = true }), Room("1", "10")));
        File.WriteAllText(_path, """
            {"pages":[{"id":"p","title":"P","enabled":false,"widgets":[{"id":"d","type":"discord","options":{"favGuilds":["10"]}}]},
                      {"id":"q","title":"Q","widgets":[]}]}
            """);
        Assert.Empty(Wanted(new ConfigStore(_path), Room("1", "10")));
    }

    // ---- what the agent asks Discord, over a pipe that stands in for Discord ----

    private static async Task<JsonObject> ReadRequest(Stream s, CancellationToken ct)
    {
        var head = new byte[8];
        await s.ReadExactlyAsync(head, ct);
        var body = new byte[BitConverter.ToInt32(head, 4)];
        await s.ReadExactlyAsync(body, ct);
        return JsonNode.Parse(Encoding.UTF8.GetString(body))!.AsObject();
    }

    private static Task Answer(Stream s, JsonNode? nonce, string json, string? evt = null)
    {
        var body = Encoding.UTF8.GetBytes(new JsonObject { ["cmd"] = "GET_CHANNEL", ["evt"] = evt, ["nonce"] = (string?)nonce, ["data"] = JsonNode.Parse(json) }.ToJsonString());
        var buf = new byte[8 + body.Length];
        BitConverter.TryWriteBytes(buf.AsSpan(0, 4), 1);
        BitConverter.TryWriteBytes(buf.AsSpan(4, 4), body.Length);
        body.CopyTo(buf, 8);
        return s.WriteAsync(buf).AsTask();
    }

    private async Task WithDiscord(Func<DiscordMonitor, NamedPipeServerStream, CancellationToken, Task> test)
    {
        var monitor = new DiscordMonitor(_cfg, NullLogger<DiscordMonitor>.Instance);
        var name = "pbf-" + Guid.NewGuid().ToString("N")[..12]; // short: macOS puts the socket under $TMPDIR
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await Task.WhenAll(server.WaitForConnectionAsync(cts.Token), client.ConnectAsync(cts.Token));
        monitor._io = client;
        var loop = monitor.ReadLoop(client, cts.Token);
        await test(monitor, server, cts.Token);
        cts.Cancel();
        try { await loop; } catch (OperationCanceledException) { }
    }

    [Fact]
    public Task Only_the_wanted_channels_are_asked_and_empty_or_refused_ones_are_left_out() => WithDiscord(async (monitor, discord, ct) =>
    {
        var reading = monitor.ReadRooms([Room("1", "10"), Room("2", "10"), Room("3", "10")], ct, TimeSpan.FromSeconds(10));
        var first = await ReadRequest(discord, ct);
        Assert.Equal(("GET_CHANNEL", "1"), ((string?)first["cmd"], (string?)first["args"]!["channel_id"]));
        await Answer(discord, first["nonce"], """{"message":"no access"}""", "ERROR");
        var second = await ReadRequest(discord, ct);
        Assert.Equal("2", (string?)second["args"]!["channel_id"]);
        await Answer(discord, second["nonce"], """{"voice_states":[{"nick":"Ada","user":{"id":"7"}}]}""");
        var third = await ReadRequest(discord, ct);
        Assert.Equal("3", (string?)third["args"]!["channel_id"]);
        await Answer(discord, third["nonce"], """{"voice_states":[]}""");
        var rooms = await reading;
        Assert.Equal(["2"], rooms.Keys);
        Assert.Equal("Ada", Assert.Single(rooms["2"]).Name);
    });

    [Fact]
    public Task A_slow_Discord_ends_the_pass_instead_of_being_asked_for_every_channel() => WithDiscord(async (monitor, discord, ct) =>
    {
        var reading = monitor.ReadRooms([.. Enumerable.Range(1, 20).Select(i => Room(i.ToString(), "10"))], ct, TimeSpan.FromMilliseconds(200));
        Assert.Equal("1", (string?)(await ReadRequest(discord, ct))["args"]!["channel_id"]);
        Assert.Empty(await reading); // never answered: had it gone on, the 19 other requests would follow
        var more = ReadRequest(discord, ct);
        await Task.Delay(300, ct);
        Assert.False(more.IsCompleted);
    });

    [Fact]
    public Task A_channel_no_longer_wanted_during_a_pass_is_not_asked_for() => WithDiscord(async (monitor, discord, ct) =>
    {
        var wanted = new HashSet<string> { "1", "2", "3" };
        var reading = monitor.ReadRooms([Room("1", "10"), Room("2", "10"), Room("3", "10")], ct, TimeSpan.FromSeconds(10), wanted.Contains);
        var first = await ReadRequest(discord, ct);
        wanted.Remove("2");
        await Answer(discord, first["nonce"], """{"voice_states":[]}""");
        var next = await ReadRequest(discord, ct);
        Assert.Equal("3", (string?)next["args"]!["channel_id"]);
        await Answer(discord, next["nonce"], """{"voice_states":[]}""");
        Assert.Empty(await reading);
    });

    // ---- the endpoint's check: only what the agent itself reported ----

    [Fact]
    public void Only_a_server_or_channel_the_agent_reported_is_accepted()
    {
        var monitor = new DiscordMonitor(_cfg, NullLogger<DiscordMonitor>.Instance);
        Assert.Equal(400, DiscordFavorites.Apply(_cfg, "p", "d", "guild", "333", true, monitor.Knows).Status); // nothing reported yet
        monitor.PublishPoll(0, false, false, new(true, null, "me", false, false, null, null, null, [], [Room("444", "333")], []));
        Assert.Equal(200, DiscordFavorites.Apply(_cfg, "p", "d", "guild", "333", true, monitor.Knows).Status);
        Assert.Equal(200, DiscordFavorites.Apply(_cfg, "p", "d", "channel", "444", true, monitor.Knows).Status);
        Assert.Equal(400, DiscordFavorites.Apply(_cfg, "p", "d", "guild", "444", true, monitor.Knows).Status);   // a channel id is not a server id
        Assert.Equal(400, DiscordFavorites.Apply(_cfg, "p", "d", "channel", "333", true, monitor.Knows).Status);
        Assert.Equal(400, DiscordFavorites.Apply(_cfg, "p", "d", "channel", "999", true, monitor.Knows).Status);
    }

    [Fact]
    public void The_people_in_a_voice_channel_are_read_from_its_voice_states()
    {
        var people = DiscordMonitor.MembersOf(JsonNode.Parse("""
            [{"nick":"Ada","user":{"id":"1","username":"ada","avatar":"abc"},"voice_state":{"self_mute":true}},
             {"user":{"id":"2","global_name":"Linus","username":"lin"},"voice_state":{"deaf":true}},
             {"user":{"id":"3"}}]
            """));
        Assert.Equal(3, people.Count);
        Assert.Equal(("Ada", true, false, "abc"), (people[0].Name, people[0].Muted, people[0].Deafened, people[0].Avatar));
        Assert.Equal(("Linus", false, true), (people[1].Name, people[1].Muted, people[1].Deafened));
        Assert.Equal("?", people[2].Name);
        Assert.Empty(DiscordMonitor.MembersOf(null));
    }
}
