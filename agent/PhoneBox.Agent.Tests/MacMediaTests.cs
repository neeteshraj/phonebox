using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class MacMediaTests
{
    [Fact]
    public void SystemMetadataAdvancesFromTheReportedPositionAndStopsWhenPaused()
    {
        var monitor = new MediaMonitor(NullLogger<MediaMonitor>.Instance);
        const string json = """{"kMRMediaRemoteNowPlayingInfoTitle":"Track","kMRMediaRemoteNowPlayingInfoClientBundleIdentifier":"test.player","kMRMediaRemoteNowPlayingInfoDuration":120,"kMRMediaRemoteNowPlayingInfoElapsedTime":10,"kMRMediaRemoteNowPlayingInfoPlaybackRate":1}""";
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(10000, monitor.ReadMacSystemMedia(json, now).Item1!.PosMs);
        Assert.Equal(12000, monitor.ReadMacSystemMedia(json, now.AddSeconds(2)).Item1!.PosMs);
        var paused = monitor.ReadMacSystemMedia(json.Replace("PlaybackRate\":1", "PlaybackRate\":0"), now.AddSeconds(4)).Item1!;
        Assert.False(paused.Playing);
        Assert.Equal(12000, paused.PosMs);
    }

    private const string StampedTemplate = """{"kMRMediaRemoteNowPlayingInfoTitle":"Track","kMRMediaRemoteNowPlayingInfoClientBundleIdentifier":"test.player","kMRMediaRemoteNowPlayingInfoDuration":600,"kMRMediaRemoteNowPlayingInfoElapsedTime":__ELAPSED__,"kMRMediaRemoteNowPlayingInfoPlaybackRate":__RATE__,"kMRMediaRemoteNowPlayingInfoTimestamp":__STAMP__}""";

    private static string Stamped(double elapsed, int rate, string stamp) =>
        StampedTemplate.Replace("__ELAPSED__", elapsed.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("__RATE__", rate.ToString()).Replace("__STAMP__", stamp);

    [Fact]
    public void PositionIsExtrapolatedFromWhenTheSourcePublishedIt()
    {
        // a browser published 0:00 when playback began and never refreshed it: the phone must show 0:15, not 0:00
        var now = DateTimeOffset.UtcNow;
        var published = now.AddSeconds(-15);
        var iso = "\"" + published.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") + "\"";
        var epoch = (published.ToUnixTimeMilliseconds() / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var stamp in new[] { iso, epoch })
        {
            var pos = new MediaMonitor(NullLogger<MediaMonitor>.Instance).ReadMacSystemMedia(Stamped(0, 1, stamp), now).Item1!.PosMs;
            Assert.InRange(pos, 14000, 16000); // the ISO form is whole seconds
        }
    }

    [Fact]
    public void ThePublishedPositionIsKeptWhilePausedOrWhenTheTimestampCannotBeTrusted()
    {
        var now = DateTimeOffset.UtcNow;
        var old = "\"" + now.AddSeconds(-15).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") + "\"";
        Assert.Equal(7000, new MediaMonitor(NullLogger<MediaMonitor>.Instance).ReadMacSystemMedia(Stamped(7, 0, old), now).Item1!.PosMs);
        // a timestamp in the future, or one that is not a date at all, is ignored (the position then advances from the sample)
        var future = "\"" + now.AddMinutes(10).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") + "\"";
        Assert.Equal(7000, new MediaMonitor(NullLogger<MediaMonitor>.Instance).ReadMacSystemMedia(Stamped(7, 1, future), now).Item1!.PosMs);
        Assert.Equal(7000, new MediaMonitor(NullLogger<MediaMonitor>.Instance).ReadMacSystemMedia(Stamped(7, 1, "\"soon\""), now).Item1!.PosMs);
        Assert.Equal(7000, new MediaMonitor(NullLogger<MediaMonitor>.Instance).ReadMacSystemMedia(Stamped(7, 1, "12"), now).Item1!.PosMs);
    }

    [Fact]
    public void ASampleReportsItsAgeToThePhoneWithoutExposingTheClockReading()
    {
        var fresh = new MediaSnapshot("App", "T", "A", "", true, 5000, 60000, 0, true);
        Assert.Equal(0, fresh.AgeMs); // radio and other undated snapshots
        var aged = fresh with { SampledAt = System.Diagnostics.Stopwatch.GetTimestamp() - System.Diagnostics.Stopwatch.Frequency * 3 / 2 };
        Assert.InRange(aged.AgeMs, 1500, 1700);
        var json = System.Text.Json.JsonSerializer.Serialize(aged, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("\"ageMs\":", json);
        Assert.DoesNotContain("sampledAt", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SpotifyAndMusicAnchorOnThePlayersOwnPositionWhenMacOsReportsNone()
    {
        // macOS 26: Spotify reports ElapsedTime 0 for the whole track and no Timestamp, so counting from the first sample
        // would show 0:00 while it is really at 0:15. The player knows; ask it now and then and count in between.
        var asked = new List<string>();
        long answer = 15_000;
        var monitor = new MediaMonitor(NullLogger<MediaMonitor>.Instance) { AppPosition = bundle => { asked.Add(bundle); return answer; } };
        const string json = """{"kMRMediaRemoteNowPlayingInfoTitle":"Track","kMRMediaRemoteNowPlayingInfoClientBundleIdentifier":"com.spotify.client","kMRMediaRemoteNowPlayingInfoDuration":200,"kMRMediaRemoteNowPlayingInfoElapsedTime":0,"kMRMediaRemoteNowPlayingInfoPlaybackRate":1}""";
        var t0 = DateTimeOffset.UtcNow;
        Assert.Equal(15_000, monitor.ReadMacSystemMedia(json, t0).Item1!.PosMs);
        Assert.Equal(17_000, monitor.ReadMacSystemMedia(json, t0.AddSeconds(2)).Item1!.PosMs); // counted, not asked again
        Assert.Single(asked);
        answer = 31_000; // a seek, noticed at the next check
        Assert.Equal(31_000, monitor.ReadMacSystemMedia(json, t0.AddSeconds(5)).Item1!.PosMs);
        Assert.Equal(2, asked.Count);
        // pausing is noticed at once and the position holds
        var paused = monitor.ReadMacSystemMedia(json.Replace("PlaybackRate\":1", "PlaybackRate\":0"), t0.AddSeconds(6)).Item1!;
        Assert.False(paused.Playing);
        Assert.Equal(31_000, paused.PosMs);
        Assert.Equal(3, asked.Count);
    }

    [Fact]
    public void AfterASeekThePlayerIsAskedAgainAtOnceNotAtTheNextCheck()
    {
        long answer = 20_000;
        var asked = 0;
        var monitor = new MediaMonitor(NullLogger<MediaMonitor>.Instance) { AppPosition = _ => { asked++; return answer; } };
        const string json = """{"kMRMediaRemoteNowPlayingInfoTitle":"Track","kMRMediaRemoteNowPlayingInfoClientBundleIdentifier":"com.spotify.client","kMRMediaRemoteNowPlayingInfoDuration":300,"kMRMediaRemoteNowPlayingInfoElapsedTime":0,"kMRMediaRemoteNowPlayingInfoPlaybackRate":1}""";
        var t0 = DateTimeOffset.UtcNow;
        Assert.Equal(20_000, monitor.ReadMacSystemMedia(json, t0).Item1!.PosMs);
        Assert.Equal(21_000, monitor.ReadMacSystemMedia(json, t0.AddSeconds(1)).Item1!.PosMs);
        Assert.Equal(1, asked);

        answer = 150_000; // the phone seeked to 2:30
        monitor.ForgetPlayerPosition();
        Assert.Equal(150_000, monitor.ReadMacSystemMedia(json, t0.AddSeconds(2)).Item1!.PosMs); // not 22 s for the next 5 s
        Assert.Equal(2, asked);
    }

    [Fact]
    public void ATrackThatComesBackAfterAGapIsAskedAboutAgainAtOnce()
    {
        // the now-playing info can go empty for a moment; when the same track returns its position starts over from the raw
        // ElapsedTime (0 for Spotify), and the player must be asked then, not at the next five-second check
        long answer = 40_000;
        var asked = 0;
        var monitor = new MediaMonitor(NullLogger<MediaMonitor>.Instance) { AppPosition = _ => { asked++; return answer; } };
        const string json = """{"kMRMediaRemoteNowPlayingInfoTitle":"Track","kMRMediaRemoteNowPlayingInfoClientBundleIdentifier":"com.spotify.client","kMRMediaRemoteNowPlayingInfoDuration":300,"kMRMediaRemoteNowPlayingInfoElapsedTime":0,"kMRMediaRemoteNowPlayingInfoPlaybackRate":1}""";
        var t0 = DateTimeOffset.UtcNow;
        Assert.Equal(40_000, monitor.ReadMacSystemMedia(json, t0).Item1!.PosMs);
        Assert.Null(monitor.ReadMacSystemMedia("{}", t0.AddSeconds(1)).Item1); // nothing playing for a moment

        answer = 44_000;
        Assert.Equal(44_000, monitor.ReadMacSystemMedia(json, t0.AddSeconds(2)).Item1!.PosMs); // not 0
        Assert.Equal(2, asked);
    }

    [Fact]
    public void ThePositionIsDatedWhenItIsComputedNotAfterTheArtworkIsDecoded()
    {
        // the artwork of a TIFF is converted with sips (seconds, at worst) after the position is known; the sample's age
        // is counted from the position, so the clock reading is taken there
        var monitor = new MediaMonitor(NullLogger<MediaMonitor>.Instance) { AppPosition = _ => null };
        const string json = """{"kMRMediaRemoteNowPlayingInfoTitle":"Track","kMRMediaRemoteNowPlayingInfoClientBundleIdentifier":"test.player","kMRMediaRemoteNowPlayingInfoDuration":120,"kMRMediaRemoteNowPlayingInfoElapsedTime":10,"kMRMediaRemoteNowPlayingInfoPlaybackRate":1}""";
        var before = System.Diagnostics.Stopwatch.GetTimestamp();
        monitor.ReadMacSystemMedia(json);
        var after = System.Diagnostics.Stopwatch.GetTimestamp();
        Assert.InRange(monitor.LastSampleTicks, before, after);
    }

    [Fact]
    public void WhenThePlayerCannotBeAskedThePositionStillAdvancesAndOtherPlayersAreNeverAsked()
    {
        var asked = 0;
        var monitor = new MediaMonitor(NullLogger<MediaMonitor>.Instance) { AppPosition = _ => { asked++; return null; } };
        const string json = """{"kMRMediaRemoteNowPlayingInfoTitle":"Track","kMRMediaRemoteNowPlayingInfoClientBundleIdentifier":"com.spotify.client","kMRMediaRemoteNowPlayingInfoDuration":200,"kMRMediaRemoteNowPlayingInfoElapsedTime":4,"kMRMediaRemoteNowPlayingInfoPlaybackRate":1}""";
        var t0 = DateTimeOffset.UtcNow;
        Assert.Equal(4000, monitor.ReadMacSystemMedia(json, t0).Item1!.PosMs);
        Assert.Equal(7000, monitor.ReadMacSystemMedia(json, t0.AddSeconds(3)).Item1!.PosMs);
        Assert.Equal(1, asked); // a refusal is not retried every second

        var other = new MediaMonitor(NullLogger<MediaMonitor>.Instance) { AppPosition = _ => { asked++; return 99_000; } };
        Assert.Equal(4000, other.ReadMacSystemMedia(json.Replace("com.spotify.client", "com.google.Chrome"), t0).Item1!.PosMs);
        Assert.Equal(1, asked);
    }

    [Fact]
    public void ExtrapolatedPositionNeverPassesTheDuration()
    {
        var now = DateTimeOffset.UtcNow;
        var iso = "\"" + now.AddMinutes(-30).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") + "\"";
        Assert.Equal(600000, new MediaMonitor(NullLogger<MediaMonitor>.Instance).ReadMacSystemMedia(Stamped(5, 1, iso), now).Item1!.PosMs);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"kMRMediaRemoteNowPlayingInfoTitle\":42}")]
    [InlineData("not json")]
    public void MissingOrMalformedMetadataIsUnavailable(string json) =>
        Assert.Null(new MediaMonitor(NullLogger<MediaMonitor>.Instance).ReadMacSystemMedia(json).Item1);

    [Theory]
    [InlineData("com.spotify.client", "Spotify")]
    [InlineData("com.github.th-ch.youtube-music", "YouTube Music")]
    [InlineData("com.google.Chrome.app.cinhimbnkkaeohfgghhklpknlkffjgod", "YouTube Music")]
    [InlineData("com.google.Chrome.app.agimnkijcaahngcdmfeangaknmldooml", "Chrome")]
    [InlineData("org.example.player", "player")]
    public void KnownPlayersAreNamedAfterTheirBundle(string bundle, string expected)
    {
        var json = $$"""{"kMRMediaRemoteNowPlayingInfoTitle":"Track","kMRMediaRemoteNowPlayingInfoClientBundleIdentifier":"{{bundle}}","kMRMediaRemoteNowPlayingInfoPlaybackRate":1}""";
        Assert.Equal(expected, new MediaMonitor(NullLogger<MediaMonitor>.Instance) { AppPosition = _ => null }.ReadMacSystemMedia(json).Item1!.App);
    }

    [Fact]
    public void DelayedMacArtworkRefreshUpdatesArtIdAndClearsWhenMissing()
    {
        var monitor = new MediaMonitor(NullLogger<MediaMonitor>.Instance);
        var pngOne = Convert.ToBase64String([0x89, 0x50, 0x4e, 0x47, 1, 2, 3, 4]);
        var pngTwo = Convert.ToBase64String([0x89, 0x50, 0x4e, 0x47, 5, 6, 7, 8]);
        const string template = """{"kMRMediaRemoteNowPlayingInfoTitle":"Track","kMRMediaRemoteNowPlayingInfoClientBundleIdentifier":"test.player","kMRMediaRemoteNowPlayingInfoDuration":120,"kMRMediaRemoteNowPlayingInfoElapsedTime":10,"kMRMediaRemoteNowPlayingInfoPlaybackRate":1__ART__}""";

        var withoutArt = monitor.ReadMacSystemMedia(template.Replace("__ART__", "")).Item1!;
        Assert.Equal(0, withoutArt.ArtId);
        Assert.Null(monitor.Art);

        var firstArt = monitor.ReadMacSystemMedia(template.Replace("__ART__", $$""" ,"kMRMediaRemoteNowPlayingInfoArtworkData":"{{pngOne}}" """)).Item1!;
        Assert.Equal(1, firstArt.ArtId);
        Assert.Equal("image/png", monitor.Art?.Mime);

        var replacedArt = monitor.ReadMacSystemMedia(template.Replace("__ART__", $$""" ,"kMRMediaRemoteNowPlayingInfoArtworkData":"{{pngTwo}}" """)).Item1!;
        Assert.Equal(2, replacedArt.ArtId);

        var failedDecode = monitor.ReadMacSystemMedia(template.Replace("__ART__", $$""" ,"kMRMediaRemoteNowPlayingInfoArtworkData":"not-valid-base64!!!" """)).Item1!;
        Assert.Equal(0, failedDecode.ArtId);
        Assert.Null(monitor.Art);

        var recoveredArt = monitor.ReadMacSystemMedia(template.Replace("__ART__", $$""" ,"kMRMediaRemoteNowPlayingInfoArtworkData":"{{pngTwo}}" """)).Item1!;
        Assert.Equal(3, recoveredArt.ArtId);

        var clearedArt = monitor.ReadMacSystemMedia(template.Replace("__ART__", "")).Item1!;
        Assert.Equal(0, clearedArt.ArtId);
        Assert.Null(monitor.Art);
    }

    [Fact]
    public async Task LoadArtRejectsNonImagesAndLoopbackAndSystemMediaHonorsSmallSeeks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pb-media-art-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var loopback = new TcpListener(IPAddress.Loopback, 0);
        loopback.Start();
        var port = ((IPEndPoint)loopback.LocalEndpoint).Port;

        try
        {
            var monitor = new MediaMonitor(NullLogger<MediaMonitor>.Instance) { AppPosition = _ => null }; // not the Spotify that may be playing here
            var loadArt = typeof(MediaMonitor).GetMethod("LoadArt", BindingFlags.Instance | BindingFlags.NonPublic)!;
            async Task<AlbumArt?> InvokeLoadArt(string url) => await (Task<AlbumArt?>)loadArt.Invoke(monitor, [url])!;

            var validPng = Path.Combine(dir, "cover.png");
            File.WriteAllBytes(validPng, [0x89, 0x50, 0x4e, 0x47, 1, 2, 3, 4]);
            var loaded = await InvokeLoadArt(new Uri(validPng).AbsoluteUri);
            Assert.NotNull(loaded);
            Assert.Equal("image/png", loaded.Mime);

            // Chromium / Brave on Linux write the art to a temp file whose random suffix looks like an extension
            var chromiumPng = Path.Combine(dir, ".org.chromium.Chromium.Wa6XAo");
            File.WriteAllBytes(chromiumPng, [0x89, 0x50, 0x4e, 0x47, 1, 2, 3, 4]);
            var loadedChromium = await InvokeLoadArt(new Uri(chromiumPng).AbsoluteUri);
            Assert.NotNull(loadedChromium);
            Assert.Equal("image/png", loadedChromium.Mime);

            var chromePng = Path.Combine(dir, ".com.google.Chrome.x9Q2bZ");
            File.WriteAllBytes(chromePng, [0x89, 0x50, 0x4e, 0x47, 1, 2, 3, 4]);
            Assert.Equal("image/png", (await InvokeLoadArt(new Uri(chromePng).AbsoluteUri))?.Mime);

            // only the temp file name itself: six letters or digits after the prefix, nothing more
            foreach (var other in new[] { ".org.chromium.Chromium.private", ".org.chromium.Chromium.Wa6XA", ".org.chromium.Chromium.Wa6-Ao" })
            {
                var near = Path.Combine(dir, other);
                File.WriteAllBytes(near, [0x89, 0x50, 0x4e, 0x47, 1, 2, 3, 4]);
                Assert.Null(await InvokeLoadArt(new Uri(near).AbsoluteUri));
            }

            var chromiumText = Path.Combine(dir, ".org.chromium.Chromium.tmp123");
            File.WriteAllText(chromiumText, "plain text not an image");
            Assert.Null(await InvokeLoadArt(new Uri(chromiumText).AbsoluteUri));

            // any other name without an image extension stays refused, even with image bytes
            var extensionlessPng = Path.Combine(dir, "extensionless-png");
            File.WriteAllBytes(extensionlessPng, [0x89, 0x50, 0x4e, 0x47, 1, 2, 3, 4]);
            Assert.Null(await InvokeLoadArt(new Uri(extensionlessPng).AbsoluteUri));

            var secretJson = Path.Combine(dir, "config.json");
            File.WriteAllText(secretJson, "{\"token\":\"secret\"}");
            Assert.Null(await InvokeLoadArt(new Uri(secretJson).AbsoluteUri));

            var disguisedPng = Path.Combine(dir, "fake.png");
            File.WriteAllText(disguisedPng, "{\"not\":\"an-image\"}");
            Assert.Null(await InvokeLoadArt(new Uri(disguisedPng).AbsoluteUri));

            // nothing accepts on the listener, so a connection that got through stays queued for Pending() to see
            Assert.Null(await InvokeLoadArt($"http://127.0.0.1:{port}/art.png").WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(loopback.Pending());

            var t0 = DateTimeOffset.UtcNow;
            const string json10 = """{"kMRMediaRemoteNowPlayingInfoTitle":"Track","kMRMediaRemoteNowPlayingInfoClientBundleIdentifier":"com.spotify.client","kMRMediaRemoteNowPlayingInfoDuration":120,"kMRMediaRemoteNowPlayingInfoElapsedTime":10,"kMRMediaRemoteNowPlayingInfoPlaybackRate":1}""";
            const string json102 = """{"kMRMediaRemoteNowPlayingInfoTitle":"Track","kMRMediaRemoteNowPlayingInfoClientBundleIdentifier":"com.spotify.client","kMRMediaRemoteNowPlayingInfoDuration":120,"kMRMediaRemoteNowPlayingInfoElapsedTime":10.2,"kMRMediaRemoteNowPlayingInfoPlaybackRate":1}""";
            Assert.Equal(10000, monitor.ReadMacSystemMedia(json10, t0).Item1!.PosMs);
            Assert.Equal(40000, monitor.ReadMacSystemMedia(json10, t0.AddSeconds(30)).Item1!.PosMs);
            Assert.Equal(10200, monitor.ReadMacSystemMedia(json102, t0.AddSeconds(31)).Item1!.PosMs);
            Assert.Null(monitor.ReadMacSystemMedia("", t0.AddSeconds(32)).Item1);
            Assert.Equal(10200, monitor.ReadMacSystemMedia(json102, t0.AddSeconds(52)).Item1!.PosMs);
        }
        finally
        {
            try { loopback.Stop(); } catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task ArtworkDownloadDeadlineCoversAStalledBody()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        using var release = new CancellationTokenSource();
        var serving = Task.Run(async () =>
        {
            using var c = await server.AcceptTcpClientAsync();
            var stream = c.GetStream();
            if (await stream.ReadAsync(new byte[4096]) == 0) return; // the request (any part of it will do)
            // the headers and the first bytes of a PNG, then nothing more until the test ends
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: 4096\r\n\r\n"));
            await stream.WriteAsync(new byte[] { 0x89, 0x50, 0x4e, 0x47 });
            try { await Task.Delay(Timeout.Infinite, release.Token); } catch (OperationCanceledException) { }
        });
        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var download = MediaMonitor.DownloadArt(http, $"http://127.0.0.1:{port}/art.png", TimeSpan.FromMilliseconds(300));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            release.Cancel();
            server.Stop();
            try { await serving.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
    }

    [Fact]
    public void TheYouTubeTabThatIsPlayingIsFoundAmongSeveral()
    {
        // what Now Playing reported and what Chrome's tabs said, with a notification count and invisible direction marks
        const string playing = "Casa Tupka Anthemo | @Yo Yo Honey Singh  feat. Priyanshi | Full Video";
        var tabs = new[]
        {
            ("https://www.youtube.com/watch?v=aaaaaaaaaaa", "Lo-fi beats to study to - YouTube"),
            ("https://www.youtube.com/watch?v=vRjaGgDsWSo&list=RDF57P9C4SAW4&index=6", "(298) Casa Tupka Anthemo | \u202a@YoYoHoneySingh\u202c feat. Priyanshi | Full Video - YouTube"),
            ("https://music.youtube.com/watch?v=bbbbbbbbbbb", "Casa Tupka Anthemo - YouTube Music"),
        };
        Assert.Equal(("YouTube", "vRjaGgDsWSo"), MediaMonitor.MatchYouTubeTab(playing, tabs));
        Assert.Equal(("YouTube Music", "bbbbbbbbbbb"), MediaMonitor.MatchYouTubeTab("Casa Tupka Anthemo", tabs[2..]));
        Assert.Equal(("", ""), MediaMonitor.MatchYouTubeTab("Something else entirely", tabs));
        Assert.Equal("dQw4w9WgXcQ", MediaMonitor.YouTubeId("https://youtu.be/dQw4w9WgXcQ?t=3"));
        Assert.Equal("dQw4w9WgXcQ", MediaMonitor.YouTubeId("https://www.youtube.com/shorts/dQw4w9WgXcQ"));
    }

    private static string Js(bool playing, double t, double d, string title = "", string artist = "", string art = "") =>
        $$"""{"has":true,"p":{{(playing ? "true" : "false")}},"t":{{t}},"d":{{d}},"ti":"{{title}}","ar":"{{artist}}","al":"","art":"{{art}}"}""";

    private static MediaMonitor.NowInfo Now(string bundle, string title, string app, bool playing = true) =>
        new(new MediaSnapshot(app, title, "", "", playing, 5000, 200000, 7, true), bundle, title);

    [Fact]
    public void TwoYouTubeTabsSpotifyAndNetflixAreEachTheirOwnPlayer()
    {
        var chrome = string.Join("\n",
            "11\t101\thttps://www.youtube.com/watch?v=aaaaaaaaaaa\t" + Js(true, 30, 200, "Blinding Lights (Official Video)", "TheWeekndVEVO") + "\t(3) Blinding Lights (Official Video) - YouTube",
            "11\t102\thttps://www.youtube.com/watch?v=bbbbbbbbbbb\t" + Js(false, 12.5, 300) + "\tLo-fi beats - YouTube",
            "12\t201\thttps://www.netflix.com/watch/81234567\t" + Js(false, 600, 2700, "Episode 3") + "\tNetflix",
            "12\t202\thttps://www.youtube.com/\t" + """{"has":false,"p":false,"t":0,"d":0,"ti":"","ar":"","al":"","art":""}""" + "\tYouTube",
            "12\t203\thttps://www.google.com/search?q=netflix.com/\t" + Js(true, 1, 1) + "\tnetflix.com/ - Google Search",
            "12\t204\thttps://www.youtube.com/watch?v=ccccccccccc\t!Some other error\tAsleep - YouTube",
            "garbage line");
        var (tabs, blocked) = MediaMonitor.ParseBrowserTabs("chrome", chrome);
        Assert.False(blocked);
        Assert.Equal(["chrome:11:101", "chrome:11:102", "chrome:12:201"], tabs.Select(t => t.Id));
        Assert.Equal(("YouTube", "Blinding Lights", "The Weeknd", true, 30000L), (tabs[0].App, tabs[0].Title, tabs[0].Artist, tabs[0].Playing, tabs[0].PosMs));
        Assert.Equal("yt:aaaaaaaaaaa", tabs[0].ArtUrl);                        // no session artwork: the video thumbnail
        Assert.Equal(("Lo-fi beats", 12500L, 300000L), (tabs[1].Title, tabs[1].PosMs, tabs[1].DurMs));
        Assert.Equal(("Netflix", "Episode 3", false), (tabs[2].App, tabs[2].Title, tabs[2].Playing));

        var apps = MediaMonitor.ParseApps("spotify\tplaying\tSong\tArtist\tAlbum\t12,5\t180.0\thttps://i.scdn.co/image/x\nmusic\tpaused\tTrack\tA\tB\t3\t200\t\n");
        Assert.Equal(("Spotify", true, 12500L, 180000L, "https://i.scdn.co/image/x"), (apps[0].App, apps[0].Playing, apps[0].PosMs, apps[0].DurMs, apps[0].ArtUrl));
        Assert.Equal(("music", false), (apps[1].Id, apps[1].Playing));

        // Now Playing is the Spotify app: it comes first, once, under Spotify's id; then playing, then paused
        var merged = MediaMonitor.MergeSessions(Now("com.spotify.client", "Song", "Spotify"), tabs.Concat(apps));
        Assert.Equal(["spotify", "chrome:11:101", "chrome:11:102", "chrome:12:201", "music"], merged.Select(m => m.Id));
        Assert.True(merged[0].NowPlaying);
        Assert.Equal(5000, merged[0].PosMs);                                 // Now Playing's own numbers
    }

    [Fact]
    public void NowPlayingAndItsTabAppearOnceUnderTheTabsId()
    {
        var (tabs, _) = MediaMonitor.ParseBrowserTabs("chrome", string.Join("\n",
            "11\t101\thttps://www.youtube.com/watch?v=aaaaaaaaaaa\t" + Js(true, 30, 200, "Other video") + "\tOther video - YouTube",
            "11\t102\thttps://www.youtube.com/watch?v=bbbbbbbbbbb\t" + Js(true, 50, 300, "Casa Tupka Anthemo | Full Video") + "\t(298) Casa Tupka Anthemo | ‪@YoYoHoneySingh‬ | Full Video - YouTube"));
        var merged = MediaMonitor.MergeSessions(Now("com.google.Chrome", "Casa Tupka Anthemo | Full Video", "YouTube"), tabs);
        Assert.Equal(["chrome:11:102", "chrome:11:101"], merged.Select(m => m.Id));
        Assert.Equal("yt:bbbbbbbbbbb", merged[0].ArtUrl);

        // another browser's tab of the same name is not this one; with nothing playing the list is empty
        Assert.Equal(["system", "chrome:11:101", "chrome:11:102"], MediaMonitor.MergeSessions(Now("com.brave.Browser", "Casa Tupka Anthemo | Full Video", "Brave"), tabs).Select(m => m.Id));
        Assert.Empty(MediaMonitor.MergeSessions(null, []));
        Assert.Equal(["system"], MediaMonitor.MergeSessions(Now("org.videolan.vlc", "Movie", "vlc"), []).Select(m => m.Id));
    }

    [Fact]
    public void WithoutJavaScriptFromAppleEventsMediaTabsAreListedByTitle()
    {
        var output = string.Join("\n",
            "11\t101\thttps://www.youtube.com/watch?v=CcCw1ggftuQ&list=RD\t!Executing JavaScript through AppleScript is turned off. To turn it on, from the menu bar, go to View > Developer > Allow JavaScript from Apple Events.\t(299) Flo Rida - Right Round (feat. Ke$ha) [Official Video] - YouTube",
            "11\t102\thttps://www.youtube.com/watch?v=dQw4w9WgXcQ\t!off\tRick Astley - Never Gonna Give You Up - YouTube",
            "11\t103\thttps://www.youtube.com/results?search_query=x\t!off\tx - YouTube",
            "11\t104\thttps://www.netflix.com/browse\t!off\tNetflix",
            "11\t105\thttps://www.netflix.com/watch/1\t!off\tNetflix");
        var (tabs, blocked) = MediaMonitor.ParseBrowserTabs("chrome", output);
        Assert.True(blocked);
        Assert.Equal(["chrome:11:101", "chrome:11:102", "chrome:11:105"], tabs.Select(t => t.Id));
        Assert.All(tabs, t => Assert.False(t.Playing));
        Assert.Equal(("Right Round (feat. Ke$ha)", "Flo Rida"), (tabs[0].Title, tabs[0].Artist));
        Assert.Equal("yt:CcCw1ggftuQ", tabs[0].ArtUrl);

        // the tab Now Playing reports takes its place, playing, with Now Playing's numbers
        var merged = MediaMonitor.MergeSessions(Now("com.google.Chrome", "Never Gonna Give You Up", "YouTube"), tabs);
        Assert.Equal(["chrome:11:102", "chrome:11:101", "chrome:11:105"], merged.Select(m => m.Id));
        Assert.True(merged[0].Playing);
        Assert.True(merged[0].NowPlaying);
    }

    [Fact]
    public void ScriptsCarryNoQuotesFromThePageAndOnlyNumbersWeFormatted()
    {
        Assert.Contains("execute tab id tid of window id wid javascript js", MediaMonitor.BrowserScanScript("Google Chrome"));
        Assert.Contains("do JavaScript js in tab i of window id wid", MediaMonitor.BrowserScanScript("Safari"));
        Assert.Contains("m.currentTime=12.345;", MediaMonitor.TabControlJs("seek", 12345));
        Assert.DoesNotContain("\"", MediaMonitor.TabControlJs("prev", null));
        Assert.Null(MediaMonitor.TabControlJs("seek", null));
        Assert.Equal("YouTube Music", MediaMonitor.MediaHostApp("https://music.youtube.com/watch?v=x"));
        Assert.Null(MediaMonitor.MediaHostApp("https://notyoutube.com/watch"));
    }

    [Theory]
    [InlineData("Don Omar - Danza Kuduro ft. Lucenzo", "DonOmarVEVO", "Danza Kuduro ft. Lucenzo", "Don Omar")]
    [InlineData("Blinding Lights (Official Video)", "TheWeekndVEVO", "Blinding Lights", "The Weeknd")]
    [InlineData("Tum Hi Ho", "Arijit Singh - Topic", "Tum Hi Ho", "Arijit Singh")]
    [InlineData("How to cook rice - the easy way - part 2", "Chef John", "How to cook rice - the easy way - part 2", "Chef John")]
    public void YouTubeMusicVideosShowTheSongAndTheArtist(string title, string channel, string song, string artist) =>
        Assert.Equal((song, artist), MediaMonitor.CleanYouTube(title, channel));
}
