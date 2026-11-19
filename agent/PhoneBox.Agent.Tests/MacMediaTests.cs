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

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"kMRMediaRemoteNowPlayingInfoTitle\":42}")]
    [InlineData("not json")]
    public void MissingOrMalformedMetadataIsUnavailable(string json) =>
        Assert.Null(new MediaMonitor(NullLogger<MediaMonitor>.Instance).ReadMacSystemMedia(json).Item1);

    [Theory]
    [InlineData("com.spotify.client", "Spotify")]
    [InlineData("com.github.th-ch.youtube-music", "YouTube Music")]
    [InlineData("org.example.player", "player")]
    public void KnownPlayersAreNamedAfterTheirBundle(string bundle, string expected)
    {
        var json = $$"""{"kMRMediaRemoteNowPlayingInfoTitle":"Track","kMRMediaRemoteNowPlayingInfoClientBundleIdentifier":"{{bundle}}","kMRMediaRemoteNowPlayingInfoPlaybackRate":1}""";
        Assert.Equal(expected, new MediaMonitor(NullLogger<MediaMonitor>.Instance).ReadMacSystemMedia(json).Item1!.App);
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
            var monitor = new MediaMonitor(NullLogger<MediaMonitor>.Instance);
            var loadArt = typeof(MediaMonitor).GetMethod("LoadArt", BindingFlags.Instance | BindingFlags.NonPublic)!;
            async Task<AlbumArt?> InvokeLoadArt(string url) => await (Task<AlbumArt?>)loadArt.Invoke(monitor, [url])!;

            var validPng = Path.Combine(dir, "cover.png");
            File.WriteAllBytes(validPng, [0x89, 0x50, 0x4e, 0x47, 1, 2, 3, 4]);
            var loaded = await InvokeLoadArt(new Uri(validPng).AbsoluteUri);
            Assert.NotNull(loaded);
            Assert.Equal("image/png", loaded.Mime);

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
