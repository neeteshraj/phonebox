using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// Now playing on Linux (MPRIS through playerctl) and macOS (system media through nowplaying-cli, with
/// Spotify and Music AppleScript fallback). Same API as the Windows MediaMonitor.
/// </summary>
public sealed class MediaMonitor(ILogger<MediaMonitor> log) : BackgroundService
{
    private const int MaxArtBytes = 5 * 1024 * 1024;
    private static readonly TimeSpan ArtDeadline = TimeSpan.FromSeconds(5);
    private static readonly HttpClient Http = CreatePublicHttpClient();
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private volatile MediaSnapshot? _snap;
    private string _artUrl = "";
    private int _artId;
    private bool _warned;
    private volatile bool _macSystemMedia;
    private string _macArtKey = "";
    private string _macArtEncoded = "";
    private string _macPositionKey = "";
    private long _macPositionMs;
    private long _macLastRawPositionMs;
    private DateTimeOffset _macPositionAt;
    private string _macAnchorKey = "";
    private bool _macAnchorPlaying;
    private DateTimeOffset _macAnchorAt;
    private static readonly TimeSpan AnchorEvery = TimeSpan.FromSeconds(5);

    /// <summary>The clock reading (Stopwatch ticks) at which the last now-playing position was computed.</summary>
    internal long LastSampleTicks { get; private set; }

    /// <summary>Where the player itself says playback is (ms), or null when it cannot be asked. Replaced in tests.</summary>
    internal Func<string, long?> AppPosition { get; set; } = ReadAppPosition;
    private string _macSourceKey = "";
    private string _macSource = "";
    private string _macThumb = "";       // the YouTube video's thumbnail, for browsers that publish no artwork
    private string _thumbLoaded = "";
    private AlbumArt? _thumbArt;
    private DateTimeOffset _macSourceAt;
    private string _macBundle = "";
    private string _macRawTitle = "";
    private volatile NowInfo? _now;                      // the primary player and what it matches tabs / apps by
    private volatile SessionScan _scan = SessionScan.Empty;
    private readonly Dictionary<string, (string Key, AlbumArt? Art, int Id)> _sessionArt = [];
    private readonly HashSet<string> _jsWarned = [];
    private int _sessionArtId = 1_000_000;              // its own counter: the scan loop runs beside Poll
    private readonly SemaphoreSlim _scanGate = new(1, 1);

    // Every reading starts nowplaying-cli or playerctl (and osascript when nothing is in Now Playing), and on macOS .NET
    // forks the whole agent to start each one. A panel on screen asks for the snapshot every second (the live stream's
    // state), and so does a "media-playing" rule while one exists. With nobody asking (the phone's screen is off, it is
    // unplugged, no panel is open) a reading every IdlePoll is enough, and the first request after that reads at once.
    internal static readonly TimeSpan ActivePoll = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(10);
    internal const long WantedForMs = 15_000;
    private long _lastRequest = -WantedForMs; // nobody has asked yet
    // when the last reading finished; a control acts on that reading, so it reads again first when this is old
    private long _lastReading = long.MinValue / 2;
    internal const long FreshForMs = 2_000;
    private readonly SemaphoreSlim _wake = new(0, 1);

    public AlbumArt? Art { get; private set; }

    /// <summary>
    /// The last reading, without asking for the next one: the state frame every panel gets each second uses this,
    /// so a panel on screen does not keep the reader at ActivePoll by itself. A widget that shows the media asks
    /// through Snapshot (GET /api/media) while it is on screen (#523).
    /// </summary>
    public MediaSnapshot? Peek() => _snap;

    public MediaSnapshot? Snapshot()
    {
        var now = Environment.TickCount64;
        if (IsIdle(Interlocked.Exchange(ref _lastRequest, now), now)) Wake();
        return _snap;
    }

    internal static bool IsIdle(long lastRequest, long now) => now - lastRequest >= WantedForMs;
    internal static bool IsStale(long lastReading, long now) => now - lastReading > FreshForMs;
    private bool Idle => IsIdle(Interlocked.Read(ref _lastRequest), Environment.TickCount64);

    /// <summary>A request came back after a quiet spell and the next reading will not wait out IdlePoll.</summary>
    internal bool ReadingDue => _wake.CurrentCount > 0;

    private void Wake() { try { _wake.Release(); } catch (SemaphoreFullException) { } }

    /// <summary>
    /// While nobody watches, the last reading can be up to IdlePoll old: read again before acting on it. Keyed on the
    /// reading, not the last request, because the phone's /api/media asks for the snapshot just before it acts.
    /// </summary>
    private async Task RefreshIfIdle()
    {
        if (!IsStale(Interlocked.Read(ref _lastReading), Environment.TickCount64)) return;
        try { await Poll(onlyIfStale: true); }
        catch (Exception ex) { log.LogDebug("media: {Msg}", ex.Message); }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // every open player, on its own loop: a scan of many browser windows must not hold the 1 s primary poll
        if (OperatingSystem.IsMacOS()) _ = Task.Run(() => ScanLoop(ct), ct);
        while (!ct.IsCancellationRequested)
        {
            try { await Poll(); }
            catch (Exception ex) { log.LogDebug("media: {Msg}", ex.Message); _snap = null; }
            await _wake.WaitAsync(Idle ? IdlePoll : ActivePoll, ct);
        }
    }

    private async Task Poll(bool onlyIfStale = false)
    {
        await _pollGate.WaitAsync();
        // a control that waited here for the loop's own reading uses that one instead of starting the tools again
        if (onlyIfStale && !IsStale(Interlocked.Read(ref _lastReading), Environment.TickCount64)) { _pollGate.Release(); return; }
        var before = _snap;
        // fresh from when the players were asked, not from when the artwork finished loading
        var asked = Environment.TickCount64;
        try
        {
            var (snap, art) = OperatingSystem.IsMacOS() ? ReadMac() : ReadLinux();
            // when the position was computed: for the now-playing info that is inside ReadMacSystemMedia, before the artwork is
            // decoded (a TIFF is converted with sips, which can take seconds); otherwise it was read just now
            var sampledAt = OperatingSystem.IsMacOS() && _macSystemMedia && LastSampleTicks != 0 ? LastSampleTicks : System.Diagnostics.Stopwatch.GetTimestamp();
            if (snap is null) { _snap = null; _now = null; Art = null; _macArtKey = ""; _macArtEncoded = ""; _macPositionKey = ""; _artUrl = ""; return; }
            if (_macSystemMedia)
            {
                // Chrome and friends publish no artwork for YouTube: use the video's own thumbnail instead
                if (Art is null && _macThumb.Length > 0)
                {
                    if (_macThumb != _thumbLoaded)
                    {
                        _thumbLoaded = _macThumb;
                        _thumbArt = await LoadArt(YouTubeThumb(_macThumb, "maxresdefault")) ?? await LoadArt(YouTubeThumb(_macThumb, "mqdefault"));
                        if (_thumbArt is not null) _artId++;
                    }
                    Art = _thumbArt;
                }
                _snap = snap with { ArtId = Art is null ? 0 : _artId, SampledAt = sampledAt };
                _now = new NowInfo(_snap, _macBundle, _macRawTitle);
                return;
            }
            if (_macArtKey.Length > 0) { _macArtKey = ""; _macArtEncoded = ""; _artUrl = "\0"; }
            if (art != _artUrl)
            {
                _artUrl = art;
                var loaded = await LoadArt(art);
                if (loaded is null) Art = null;
                else if (Art is null || Art.Mime != loaded.Mime || !Art.Bytes.AsSpan().SequenceEqual(loaded.Bytes)) { Art = loaded; _artId++; }
            }
            _snap = snap with { ArtId = Art is null ? 0 : _artId, SampledAt = sampledAt };
            _now = OperatingSystem.IsMacOS() ? new NowInfo(_snap, snap.App == "Music" ? "com.apple.Music" : "com.spotify.client", snap.Title) : null;
        }
        finally
        {
            Interlocked.Exchange(ref _lastReading, asked);
            _pollGate.Release();
            // a new track, a pause or a seek reaches the phone now, not at its next whole-second state
            if (StateSignal.MediaChanged(before, _snap)) StateSignal.Poke();
        }
    }

    // ---------------------------------------------------------------- Linux: playerctl (MPRIS)
    private (MediaSnapshot?, string) ReadLinux()
    {
        if (Sh.Which("playerctl") is null)
        {
            if (!_warned) { _warned = true; log.LogInformation("media: install playerctl to show and control what is playing (sudo apt install playerctl)"); }
            return (null, "");
        }
        var line = Sh.Run("playerctl", "metadata", "--format",
            "{{playerName}}\t{{status}}\t{{title}}\t{{artist}}\t{{album}}\t{{position}}\t{{mpris:length}}\t{{mpris:artUrl}}");
        if (string.IsNullOrWhiteSpace(line)) return (null, "");
        var f = line.TrimEnd('\n').Split('\t');
        if (f.Length < 8) return (null, "");
        long Us(string s) => long.TryParse(s, out var v) ? v / 1000 : 0;
        var app = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(f[0].Split('.')[0]);
        return (new MediaSnapshot(app, f[2], f[3], f[4], f[1] == "Playing", Us(f[5]), Us(f[6]), 0, Us(f[6]) > 0), f[7]);
    }

    // ---------------------------------------------------------------- macOS: system media, then AppleScript fallback
    private (MediaSnapshot?, string) ReadMac()
    {
        var system = ReadMacSystemMedia();
        _macSystemMedia = system.Item1 is not null;
        return _macSystemMedia ? system : ReadMacAppleScript();
    }

    internal (MediaSnapshot?, string) ReadMacSystemMedia(string? suppliedJson = null, DateTimeOffset? sampledAt = null)
    {
        var json = suppliedJson ?? Sh.Run("nowplaying-cli", "get-raw");
        if (string.IsNullOrWhiteSpace(json)) { _macPositionKey = ""; return (null, ""); }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement;
            if (data.ValueKind != JsonValueKind.Object) { _macPositionKey = ""; return (null, ""); }
            const string prefix = "kMRMediaRemoteNowPlayingInfo";
            string Str(string key) => data.TryGetProperty(prefix + key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "" : "";
            double Num(string key) => data.TryGetProperty(prefix + key, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : 0;
            var title = Str("Title");
            if (string.IsNullOrWhiteSpace(title)) { _macPositionKey = ""; return (null, ""); }
            var bundle = Str("ClientBundleIdentifier");
            _macBundle = bundle;
            _macRawTitle = title;
            var app = bundle switch
            {
                "com.apple.WebKit.GPU" or "com.apple.Safari" => "Safari",
                "com.google.Chrome" => "Chrome",
                "com.brave.Browser" => "Brave",
                "com.microsoft.edgemac" => "Edge",
                "company.thebrowser.Browser" => "Arc",
                "com.vivaldi.Vivaldi" => "Vivaldi",
                "org.mozilla.firefox" => "Firefox",
                "com.apple.Music" => "Music",
                "com.spotify.client" => "Spotify",
                "com.github.th-ch.youtube-music" => "YouTube Music",
                // YouTube Music installed as a Chrome app (PWA): the ID is the same on every Mac
                "com.google.Chrome.app.cinhimbnkkaeohfgghhklpknlkffjgod" => "YouTube Music",
                // any other Chrome app would otherwise show its 32-letter ID
                _ when bundle.StartsWith("com.google.Chrome.app.", StringComparison.Ordinal) => "Chrome",
                _ => string.IsNullOrEmpty(bundle) ? "Mac" : bundle.Split('.').Last(),
            };
            var sourceKey = bundle + "\n" + title;
            if (_macSourceKey != sourceKey || DateTimeOffset.UtcNow - _macSourceAt > TimeSpan.FromSeconds(10))
            {
                _macSourceKey = sourceKey;
                _macSourceAt = DateTimeOffset.UtcNow;
                (_macSource, _macThumb) = ReadYouTubeSource(bundle, title);
            }
            if (_macSource.Length > 0) app = _macSource;

            var duration = Math.Max(0, Num("Duration"));
            var playing = Num("PlaybackRate") > 0;
            var rawPosition = (long)(Math.Max(0, Num("ElapsedTime")) * 1000);
            var positionKey = sourceKey + "\n" + Num("UniqueIdentifier").ToString(CultureInfo.InvariantCulture);
            var now = sampledAt ?? DateTimeOffset.UtcNow;
            LastSampleTicks = System.Diagnostics.Stopwatch.GetTimestamp(); // the position is as of now, whatever the artwork below takes
            if (positionKey != _macPositionKey)
            {
                _macPositionKey = positionKey;
                _macPositionMs = rawPosition;
                _macLastRawPositionMs = rawPosition;
                _macAnchorAt = DateTimeOffset.MinValue; // a new position sequence (or the same track back after a gap): ask the player
            }
            else
            {
                var predicted = _macPositionMs + (playing ? (long)(now - _macPositionAt).TotalMilliseconds : 0);
                _macPositionMs = rawPosition != _macLastRawPositionMs ? rawPosition : predicted;
                _macLastRawPositionMs = rawPosition;
            }
            // ElapsedTime is only valid as of the Timestamp the source published it with (browsers publish it on play,
            // pause and seek, not while playing), so the real position is that plus the time since, at the playback rate
            if (MacPublishedAt(data, prefix + "Timestamp", now) is { } publishedAt)
            {
                var rate = Num("PlaybackRate");
                _macPositionMs = rawPosition + (playing ? (long)(Math.Max(0, (now - publishedAt).TotalMilliseconds) * rate) : 0);
                _macLastRawPositionMs = rawPosition;
            }
            else if (bundle is "com.spotify.client" or "com.apple.Music")
            {
                // Since macOS 15.4 these often report ElapsedTime 0 for the whole track and no Timestamp at all (Spotify on
                // macOS 26: checked), so nothing here says where playback is. The player does: ask it when the track or
                // play/pause changes and every few seconds (a seek), and count from that in between.
                if (positionKey != _macAnchorKey || playing != _macAnchorPlaying || now - _macAnchorAt >= AnchorEvery)
                {
                    _macAnchorKey = positionKey;
                    _macAnchorPlaying = playing;
                    _macAnchorAt = now; // also after a refusal: it is not asked again every second
                    if (AppPosition(bundle) is { } fromPlayer) { _macPositionMs = fromPlayer; _macLastRawPositionMs = rawPosition; }
                }
            }
            _macPositionAt = now;
            var durationMs = (long)(duration * 1000);
            if (durationMs > 0) _macPositionMs = Math.Min(_macPositionMs, durationMs);

            var artKey = positionKey;
            if (artKey != _macArtKey) { _macArtKey = artKey; _macArtEncoded = ""; Art = null; }
            var encoded = data.TryGetProperty("kMRMediaRemoteNowPlayingInfoArtworkData", out var artProp) && artProp.ValueKind == JsonValueKind.String
                ? artProp.GetString() ?? "" : "";
            if (encoded.Length == 0)
            {
                _macArtEncoded = "";
                Art = null;
            }
            else if (encoded != _macArtEncoded)
            {
                var nextArt = DecodeMacArt(encoded);
                if (nextArt is null)
                {
                    _macArtEncoded = "";
                    Art = null;
                }
                else
                {
                    _macArtEncoded = encoded;
                    if (Art is null || Art.Mime != nextArt.Mime || !Art.Bytes.AsSpan().SequenceEqual(nextArt.Bytes)) { Art = nextArt; _artId++; }
                }
            }
            var (shownTitle, shownArtist) = app == "YouTube" ? CleanYouTube(title, Str("Artist")) : (title, Str("Artist"));
            return (new MediaSnapshot(app, shownTitle, shownArtist, Str("Album"), playing,
                _macPositionMs, durationMs, Art is null ? 0 : _artId, duration > 0), "");
        }
        catch (JsonException) { _macPositionKey = ""; return (null, ""); }
    }

    /// <summary>
    /// When the source published the position, from the "Timestamp" of the now-playing info: a date string
    /// (<c>2026-11-19T10:00:00Z</c>) or seconds since 1970 (a number; a count from 2001 or in milliseconds is not
    /// guessed). Null when it is missing, unreadable or implausible, so the caller falls back to advancing the sample.
    /// </summary>
    internal static DateTimeOffset? MacPublishedAt(JsonElement data, string key, DateTimeOffset now)
    {
        if (!data.TryGetProperty(key, out var value)) return null;
        DateTimeOffset at;
        if (value.ValueKind == JsonValueKind.String)
        {
            if (!DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out at)) return null;
        }
        else if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var seconds) && double.IsFinite(seconds)
                 && seconds is > 1e9 and < 1e11)
            at = DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
        else return null;
        // up to a few seconds ahead is clock noise (the date string is cut to whole seconds); further, or over half a day
        // back, means this is not a time we can rely on
        var age = now - at;
        return age < TimeSpan.FromSeconds(-5) || age > TimeSpan.FromHours(12) ? null : at;
    }

    // Chromium browsers share Chrome's AppleScript dictionary; the key is the bundle Now Playing reports.
    private static readonly Dictionary<string, string> ChromiumApps = new()
    {
        ["com.google.Chrome"] = "Google Chrome", ["com.brave.Browser"] = "Brave Browser",
        ["com.microsoft.edgemac"] = "Microsoft Edge", ["company.thebrowser.Browser"] = "Arc",
        ["com.vivaldi.Vivaldi"] = "Vivaldi",
    };

    /// <summary>
    /// "YouTube" or "YouTube Music" and the tab's video id when the browser that is playing has a YouTube tab whose
    /// title matches what Now Playing reports; ("", "") otherwise. Every YouTube tab is listed, so with several open
    /// the one that is actually playing wins, not the first.
    /// </summary>
    private static (string Source, string VideoId) ReadYouTubeSource(string bundle, string title)
    {
        string? app = bundle is "com.apple.WebKit.GPU" or "com.apple.Safari" ? "Safari" : ChromiumApps.GetValueOrDefault(bundle);
        if (app is null) return ("", "");
        var name = app == "Safari" ? "name" : "title";
        var script = $$"""
            if application "{{app}}" is not running then return ""
            set matches to ""
            tell application "{{app}}"
              repeat with w in windows
                repeat with t in tabs of w
                  try
                    set u to URL of t
                    if u contains "youtube.com/" or u contains "youtu.be/" then
                      set matches to matches & u & (ASCII character 9) & ({{name}} of t) & linefeed
                    end if
                  end try
                end repeat
              end repeat
            end tell
            return matches
            """;
        var tabs = (Sh.Run("osascript", "-e", script) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\t', 2)).Where(p => p.Length == 2).Select(p => (Url: p[0], Title: p[1]));
        return MatchYouTubeTab(title, tabs);
    }

    internal static (string Source, string VideoId) MatchYouTubeTab(string title, IEnumerable<(string Url, string Title)> tabs)
    {
        var want = Fold(title);
        if (want.Length < 3) return ("", "");
        // a tab title is the video title plus extras ("(3) … - YouTube"), and YouTube writes channel handles without
        // the spaces Now Playing keeps, with invisible direction marks around them: compare letters and digits only
        foreach (var (url, tabTitle) in tabs)
        {
            if (!Fold(tabTitle).Contains(want, StringComparison.Ordinal)) continue;
            var source = url.Contains("music.youtube.com/", StringComparison.OrdinalIgnoreCase) ? "YouTube Music" : "YouTube";
            return (source, YouTubeId(url) ?? "");
        }
        return ("", "");
    }

    private static readonly System.Text.RegularExpressions.Regex VideoNoise = new(
        @"\s*[\(\[](official\s*(music\s*)?(video|audio|lyric(s)?\s*video|visualizer)|lyric(s)?(\s*video)?|audio|video|hd|4k|mv|m/v)[\)\]]\s*",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Music videos on YouTube carry "Artist - Song (Official Video)" as the title and the channel as the artist
    /// ("ArtistVEVO", "Artist - Topic"): show the song and the artist instead. Anything else is left as it is.
    /// </summary>
    internal static (string Title, string Artist) CleanYouTube(string title, string channel)
    {
        var artist = channel.Trim();
        if (artist.EndsWith(" - Topic", StringComparison.Ordinal)) artist = artist[..^8];
        else if (artist.EndsWith("VEVO", StringComparison.Ordinal) && artist.Length > 4)
            artist = System.Text.RegularExpressions.Regex.Replace(artist[..^4], "(?<=[a-z])(?=[A-Z])", " ");
        var song = VideoNoise.Replace(title, " ").Trim();
        var dash = song.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0 && song.IndexOf(" - ", dash + 3, StringComparison.Ordinal) < 0)
        {
            artist = song[..dash].Trim();
            song = song[(dash + 3)..].Trim();
        }
        return (song.Length > 0 ? song : title, artist);
    }

    internal static string Fold(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s.Normalize(System.Text.NormalizationForm.FormKC))
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    internal static string? YouTubeId(string url)
    {
        var m = System.Text.RegularExpressions.Regex.Match(url, @"(?:[?&]v=|youtu\.be/|/shorts/|/live/)([A-Za-z0-9_-]{11})");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string YouTubeThumb(string id, string size) => $"https://i.ytimg.com/vi/{id}/{size}.jpg";

    private static AlbumArt? DecodeMacArt(string encoded)
    {
        if (string.IsNullOrEmpty(encoded) || encoded.Length > 8_000_000) return null;
        try
        {
            var bytes = Convert.FromBase64String(encoded);
            if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff)
                return new AlbumArt(bytes, "image/jpeg");
            if (bytes.Length >= 4 && bytes.AsSpan(0, 4).SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47 }))
                return new AlbumArt(bytes, "image/png");
            if (bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8))
                return new AlbumArt(bytes, "image/webp");

            // Safari often publishes TIFF artwork; Android WebView cannot display TIFF.
            var input = Path.GetTempFileName();
            var output = input + ".png";
            try
            {
                File.WriteAllBytes(input, bytes);
                if (Sh.RunWithTimeout(5000, "sips", "-s", "format", "png", input, "--out", output) is null || !File.Exists(output))
                    return null;
                return new AlbumArt(File.ReadAllBytes(output), "image/png");
            }
            finally
            {
                File.Delete(input);
                if (File.Exists(output)) File.Delete(output);
            }
        }
        catch (Exception) { return null; }
    }

    /// <summary>Spotify's or Music's own "player position" (about 150 ms; never launches the app).</summary>
    private static long? ReadAppPosition(string bundle)
    {
        var app = bundle == "com.spotify.client" ? "Spotify" : "Music";
        var text = Sh.Run("osascript", "-e",
            $"if application \"{app}\" is running then tell application \"{app}\" to return (player position as string)")?.Trim();
        // the decimal separator follows the Mac's language ("16,32")
        return !string.IsNullOrEmpty(text) && double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
            && double.IsFinite(s) && s >= 0 ? (long)(s * 1000) : null;
    }

    private static (MediaSnapshot?, string) ReadMacAppleScript()
    {
        const string script = """
            if application "Spotify" is running then
              tell application "Spotify"
                if player state is stopped then return ""
                set t to current track
                return "Spotify" & tab & (player state as string) & tab & (name of t) & tab & (artist of t) & tab & (album of t) & tab & (player position as string) & tab & ((duration of t) / 1000 as string) & tab & (artwork url of t)
              end tell
            else if application "Music" is running then
              tell application "Music"
                if player state is stopped then return ""
                set t to current track
                return "Music" & tab & (player state as string) & tab & (name of t) & tab & (artist of t) & tab & (album of t) & tab & (player position as string) & tab & ((duration of t) as string) & tab & ""
              end tell
            end if
            return ""
            """;
        var line = Sh.Run("osascript", "-e", script)?.Trim();
        if (string.IsNullOrEmpty(line)) return (null, "");
        var f = line.Split('\t');
        if (f.Length < 8) return (null, "");
        long Sec(string s) => double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (long)(v * 1000) : 0;
        return (new MediaSnapshot(f[0], f[2], f[3], f[4], f[1] == "playing", Sec(f[5]), Sec(f[6]), 0, true), f[7]);
    }

    private static HttpClient CreatePublicHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(4),
            ConnectCallback = async (ctx, token) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, token);
                var publicIps = addresses.Where(WidgetFeedProxy.IsPublicAddress)
                    .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                    .ToArray();
                if (publicIps.Length == 0)
                    throw new HttpRequestException("Refused non-public cover art host");
                Exception? last = null;
                foreach (var ip in publicIps)
                {
                    var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(ip, ctx.DnsEndPoint.Port), token);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { socket.Dispose(); throw; }
                    catch (Exception ex) { socket.Dispose(); last = ex; }
                }
                throw last ?? new HttpRequestException("Could not connect to cover art host");
            },
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(5) };
    }

    internal static string? SniffImageMime(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff)
            return "image/jpeg";
        if (bytes.Length >= 4 && bytes[..4].SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47 }))
            return "image/png";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
            return "image/webp";
        if (bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
            return "image/gif";
        return null;
    }

    // The extension is what keeps a player from making the agent read any file it names. Chromium-based browsers
    // write the art to a base::CreateTemporaryFile file, ".org.chromium.Chromium.XXXXXX" (".com.google.Chrome.XXXXXX"
    // in Google Chrome), whose six random letters and digits only look like an extension; exactly that name is let
    // through. The bytes must still be an image (SniffImageMime).
    private static bool HasAllowedArtExtension(string path) =>
        IsChromiumTempName(Path.GetFileName(path))
        || Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif";

    private static bool IsChromiumTempName(string name)
    {
        var prefix = name.StartsWith(".org.chromium.Chromium.", StringComparison.Ordinal) ? ".org.chromium.Chromium.".Length
            : name.StartsWith(".com.google.Chrome.", StringComparison.Ordinal) ? ".com.google.Chrome.".Length : -1;
        return prefix > 0 && name.Length == prefix + 6 && name.AsSpan(prefix).IndexOfAnyExcept(TempNameChars) < 0;
    }

    private static readonly System.Buffers.SearchValues<char> TempNameChars =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789");

    /// <summary>
    /// The body of a cover art URL, or null when the server refuses it or it is larger than <see cref="MaxArtBytes"/>.
    /// One deadline covers the request and every body read: HttpClient.Timeout ends at the headers, and a server that
    /// stops mid-body would otherwise hold the poll (and every media control waiting on it) for good.
    /// </summary>
    internal static async Task<byte[]?> DownloadArt(HttpClient http, string url, TimeSpan deadline)
    {
        using var cts = new CancellationTokenSource(deadline);
        using var r = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!r.IsSuccessStatusCode || r.Content.Headers.ContentLength > MaxArtBytes) return null;
        await using var stream = await r.Content.ReadAsStreamAsync(cts.Token);
        using var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buf, cts.Token)) > 0)
        {
            if (ms.Length + read > MaxArtBytes) return null;
            ms.Write(buf, 0, read);
        }
        return ms.ToArray();
    }

    private async Task<AlbumArt?> LoadArt(string url)
    {
        try
        {
            if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.IsUnc) return null;
                var path = Uri.UnescapeDataString(uri.AbsolutePath);
                if (!File.Exists(path) && uri.IsFile) path = uri.LocalPath;
                if (!HasAllowedArtExtension(path) || !File.Exists(path)) return null;
                var info = new FileInfo(path);
                if (info.Length is <= 0 or > MaxArtBytes) return null;
                var bytes = await File.ReadAllBytesAsync(path);
                return bytes.Length <= MaxArtBytes && SniffImageMime(bytes) is { } mime ? new AlbumArt(bytes, mime) : null;
            }
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await DownloadArt(Http, url, ArtDeadline);
                return bytes is not null && SniffImageMime(bytes) is { } mime ? new AlbumArt(bytes, mime) : null;
            }
        }
        catch (Exception ex) { log.LogDebug("cover art: {Msg}", ex.Message); }
        return null;
    }

    public async Task<bool> Control(string action, long? ms)
    {
        await RefreshIfIdle(); // which player to ask comes from the last reading
        bool ok;
        if (OperatingSystem.IsMacOS())
        {
            if (_macSystemMedia)
            {
                string[]? args = action switch
                {
                    "playpause" => ["togglePlayPause"],
                    "next" => ["next"],
                    "prev" => ["previous"],
                    "seek" when ms is { } m => ["seek", (m / 1000.0).ToString(CultureInfo.InvariantCulture)],
                    _ => null,
                };
                ok = args is not null && Sh.Run("nowplaying-cli", args) is not null;
            }
            else
            {
                ok = ControlApp(_snap?.App == "Music" ? "Music" : "Spotify", action, ms);
            }
        }
        else
        {
            string[]? args = action switch
            {
                "playpause" => ["play-pause"],
                "next" => ["next"],
                "prev" => ["previous"],
                "seek" when ms is { } m => ["position", (m / 1000.0).ToString(CultureInfo.InvariantCulture)],
                _ => null,
            };
            ok = args is not null && Sh.Run("playerctl", args) is not null;
        }
        // after a seek the position is no longer where the last check put it: the poll below asks the player again
        if (action == "seek") ForgetPlayerPosition();
        try { await Poll(); } catch { }
        return ok;
    }

    private static bool ControlApp(string app, string action, long? ms)
    {
        var cmd = action switch
        {
            "playpause" => "playpause",
            "next" => "next track",
            "prev" => "previous track",
            "seek" when ms is { } m => $"set player position to {(m / 1000.0).ToString(CultureInfo.InvariantCulture)}",
            _ => null,
        };
        return cmd is not null && Sh.Run("osascript", "-e", $"tell application \"{app}\" to {cmd}") is not null;
    }

    // ---------------------------------------------------------------- every open player (macOS): the phone's carousel

    internal sealed record NowInfo(MediaSnapshot Snap, string Bundle, string RawTitle);

    /// <summary>A player found by a scan. <see cref="ArtUrl"/> is a URL or "yt:&lt;video id&gt;"; <see cref="MatchText"/> is what Now Playing's title is looked for in.</summary>
    internal sealed record MacSource(string Id, string App, string Title, string Artist, string Album, bool Playing,
        long PosMs, long DurMs, bool CanSeek, string ArtUrl = "", string MatchText = "", bool NowPlaying = false);

    internal sealed record SessionScan(IReadOnlyList<MacSource> Items, IReadOnlySet<string> JsBlocked, DateTimeOffset At)
    {
        public static readonly SessionScan Empty = new([], new HashSet<string>(), DateTimeOffset.MinValue);
    }

    // id prefix → AppleScript application name; the Chromium ones share Chrome's dictionary
    private static readonly (string Key, string App, string Bundle)[] Browsers =
    [
        ("chrome", "Google Chrome", "com.google.Chrome"), ("brave", "Brave Browser", "com.brave.Browser"),
        ("edge", "Microsoft Edge", "com.microsoft.edgemac"), ("arc", "Arc", "company.thebrowser.Browser"),
        ("vivaldi", "Vivaldi", "com.vivaldi.Vivaldi"), ("safari", "Safari", "com.apple.Safari"),
    ];

    private static readonly (string Host, string App)[] MediaHosts =
    [
        ("music.youtube.com", "YouTube Music"), ("youtube.com", "YouTube"), ("youtu.be", "YouTube"), ("netflix.com", "Netflix"),
        ("open.spotify.com", "Spotify"), ("soundcloud.com", "SoundCloud"), ("twitch.tv", "Twitch"), ("vimeo.com", "Vimeo"),
        ("primevideo.com", "Prime Video"), ("disneyplus.com", "Disney+"), ("hotstar.com", "Hotstar"), ("jiocinema.com", "JioCinema"),
    ];

    internal static string? MediaHostApp(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "https" or "http"
            ? MediaHosts.FirstOrDefault(h => u.Host.Equals(h.Host, StringComparison.OrdinalIgnoreCase)
                || u.Host.EndsWith("." + h.Host, StringComparison.OrdinalIgnoreCase)).App
            : null;

    // the page's playing (else first loaded) <video>/<audio>; single quotes only, the script sits in an AppleScript string
    private const string PickMedia = "var a=document.querySelectorAll('video,audio'),m=null;for(var i=0;i<a.length;i++){if(!a[i].paused){m=a[i];break}if(!m&&a[i].duration>0)m=a[i]}";

    private const string ReadJs = "(function(){" + PickMedia +
        "var s=navigator.mediaSession&&navigator.mediaSession.metadata,art='',best=-1;" +
        "if(s&&s.artwork)for(var j=0;j<s.artwork.length;j++){var w=parseInt(String(s.artwork[j].sizes||'0').split('x')[0])||0;if(w>best){best=w;art=s.artwork[j].src}}" +
        "return JSON.stringify({has:!!m,p:!!m&&!m.paused,t:m?m.currentTime||0:0,d:m&&isFinite(m.duration)?m.duration:0," +
        "ti:s&&s.title||'',ar:s&&s.artist||'',al:s&&s.album||'',art:art||''})})()";

    private static string AsString(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>
    /// One osascript per browser: every window's tabs in bulk (a few Apple Events per window, not per tab), and the
    /// reading script only in tabs on media hosts. Lines: window id, tab id (Safari: index), URL, script result or
    /// "!error", title. After the first "JavaScript from Apple Events is off" the remaining tabs skip the script.
    /// </summary>
    internal static string BrowserScanScript(string app)
    {
        var safari = app == "Safari";
        var hosts = string.Join(", ", MediaHosts.Select(h => AsString(h.Host + "/")));
        return $$"""
            if application "{{app}}" is not running then return ""
            set TB to ASCII character 9
            set LF to ASCII character 10
            set js to {{AsString(ReadJs)}}
            set hosts to {{{hosts}}}
            set out to ""
            set jsOk to true
            tell application "{{app}}"
              repeat with w in windows
                try
                  set wid to id of w
                  set us to URL of tabs of w
                  set ts to {{(safari ? "name" : "title")}} of tabs of w
                  {{(safari ? "" : "set ids to id of tabs of w")}}
                  repeat with i from 1 to count of us
                    try
                      set u to item i of us
                      set isMedia to false
                      repeat with h in hosts
                        if u contains h then set isMedia to true
                      end repeat
                      if isMedia then
                        set tid to {{(safari ? "i" : "item i of ids")}}
                        set r to "!off"
                        if jsOk then
                          try
                            set r to "" & ({{(safari ? "do JavaScript js in tab i of window id wid" : "execute tab id tid of window id wid javascript js")}})
                          on error e
                            set r to "!" & e
                            if e contains "Apple Events" or e contains "turned off" then set jsOk to false
                          end try
                        end if
                        set out to out & wid & TB & tid & TB & u & TB & r & TB & (item i of ts) & LF
                      end if
                    end try
                  end repeat
                end try
              end repeat
            end tell
            return out
            """;
    }

    /// <summary>Media tabs of one browser from <see cref="BrowserScanScript"/>'s output, and whether it refuses JavaScript from Apple Events.</summary>
    internal static (List<MacSource> Items, bool JsBlocked) ParseBrowserTabs(string key, string output)
    {
        var items = new List<MacSource>();
        var blocked = false;
        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = raw.TrimEnd('\r').Split('\t', 5);
            if (f.Length < 5 || !long.TryParse(f[0], out var wid) || !long.TryParse(f[1], out var tid)) continue;
            var (url, result, tabTitle) = (f[2], f[3], f[4]);
            if (MediaHostApp(url) is not { } app) continue;
            var id = $"{key}:{wid}:{tid}";
            var videoId = app.StartsWith("YouTube", StringComparison.Ordinal) ? YouTubeId(url) : null;
            var thumb = videoId is null ? "" : "yt:" + videoId;
            if (result.StartsWith('!'))
            {
                if (result == "!off" || result.Contains("Apple Events", StringComparison.Ordinal) || result.Contains("turned off", StringComparison.Ordinal))
                {
                    blocked = true;
                    // title only; skip YouTube's home and search pages and Netflix's browse rows, which play nothing of yours
                    if (app.StartsWith("YouTube", StringComparison.Ordinal) && videoId is null && !url.Contains("/watch", StringComparison.Ordinal)) continue;
                    if (app == "Netflix" && !url.Contains("/watch", StringComparison.Ordinal)) continue;
                    var (t, ar) = CleanTabTitle(app, tabTitle);
                    items.Add(new MacSource(id, app, t, ar, "", false, 0, 0, false, thumb, tabTitle));
                }
                continue;  // any other error: a sleeping or discarded tab
            }
            try
            {
                using var doc = JsonDocument.Parse(result);
                var o = doc.RootElement;
                string S(string n) => o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                double N(string n) => o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? Math.Max(0, d) : 0;
                bool B(string n) => o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
                var playing = B("p");
                var pos = N("t");
                var metaTitle = S("ti");
                if (!(B("has") && (playing || pos > 0)) && metaTitle.Length == 0) continue;   // nothing loaded in this tab
                var (title, artist) = metaTitle.Length > 0 ? (metaTitle, S("ar")) : CleanTabTitle(app, tabTitle);
                if (app == "YouTube" && metaTitle.Length > 0) (title, artist) = CleanYouTube(title, artist);
                var art = S("art");
                if (!art.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !art.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) art = thumb;
                var dur = N("d");
                items.Add(new MacSource(id, app, title, artist, S("al"), playing, (long)(pos * 1000), (long)(dur * 1000), dur > 0,
                    art, tabTitle + "\n" + metaTitle));
            }
            catch (JsonException) { }
        }
        return (items, blocked);
    }

    private static readonly System.Text.RegularExpressions.Regex TabNoise = new(@"^\(\d+\)\s*|\s+[-|–]\s+(YouTube Music|YouTube|Netflix|Twitch|SoundCloud|Vimeo|Prime Video|Disney\+|JioCinema|Hotstar)$");

    internal static (string Title, string Artist) CleanTabTitle(string app, string tabTitle)
    {
        var t = TabNoise.Replace(tabTitle, "").Trim();
        if (t.Length == 0) t = app;
        return app == "YouTube" ? CleanYouTube(t, "") : (t, "");
    }

    private const string AppsScanScript = """
        set TB to ASCII character 9
        set out to ""
        if application "Spotify" is running then
          try
            tell application "Spotify"
              if player state is not stopped then
                set t to current track
                set out to out & "spotify" & TB & (player state as string) & TB & (name of t) & TB & (artist of t) & TB & (album of t) & TB & (player position as string) & TB & ((duration of t) / 1000 as string) & TB & (artwork url of t) & linefeed
              end if
            end tell
          end try
        end if
        if application "Music" is running then
          try
            tell application "Music"
              if player state is not stopped then
                set t to current track
                set out to out & "music" & TB & (player state as string) & TB & (name of t) & TB & (artist of t) & TB & (album of t) & TB & (player position as string) & TB & ((duration of t) as string) & TB & "" & linefeed
              end if
            end tell
          end try
        end if
        return out
        """;

    internal static List<MacSource> ParseApps(string output)
    {
        long Sec(string s) => double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? (long)(Math.Max(0, v) * 1000) : 0;
        var items = new List<MacSource>();
        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = raw.TrimEnd('\r').Split('\t');
            if (f.Length < 8 || f[0] is not ("spotify" or "music")) continue;
            items.Add(new MacSource(f[0], f[0] == "spotify" ? "Spotify" : "Music", f[2], f[3], f[4], f[1] == "playing",
                Sec(f[5]), Sec(f[6]), true, f[7]));
        }
        return items;
    }

    /// <summary>
    /// The carousel order: the Now Playing player first (merged with the tab or app it is, which keeps that id so its
    /// controls reach the right one), then the other playing ones, then the paused ones.
    /// </summary>
    internal static List<MacSource> MergeSessions(NowInfo? now, IEnumerable<MacSource> scanned)
    {
        var rest = scanned.ToList();
        var result = new List<MacSource>(rest.Count + 1);
        if (now is not null)
        {
            var s = now.Snap;
            var first = new MacSource("system", s.App, s.Title, s.Artist, s.Album, s.Playing, s.PosMs, s.DurMs, s.CanSeek, NowPlaying: true);
            if (FindNowPlaying(now, rest) is { } match)
            {
                rest.Remove(match);
                first = first with { Id = match.Id, ArtUrl = match.ArtUrl };
            }
            result.Add(first);
        }
        result.AddRange(rest.Where(x => x.Playing));
        result.AddRange(rest.Where(x => !x.Playing));
        return result;
    }

    private static MacSource? FindNowPlaying(NowInfo now, List<MacSource> items)
    {
        if (now.Bundle == "com.spotify.client") return items.FirstOrDefault(x => x.Id == "spotify");
        if (now.Bundle == "com.apple.Music") return items.FirstOrDefault(x => x.Id == "music");
        var key = now.Bundle == "com.apple.WebKit.GPU" ? "safari" : Browsers.FirstOrDefault(b => b.Bundle == now.Bundle).Key;
        var want = Fold(now.RawTitle);
        if (key is null || want.Length < 3) return null;
        return items.Where(x => x.Id.StartsWith(key + ":", StringComparison.Ordinal) && Fold(x.MatchText).Contains(want, StringComparison.Ordinal))
            .OrderByDescending(x => x.Playing).FirstOrDefault();
    }

    private async Task ScanLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await ScanSessions(); }
            catch (Exception ex) { log.LogDebug("media sessions: {Msg}", ex.Message); }
            try { await Task.Delay(2500, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task ScanSessions()
    {
        if (!await _scanGate.WaitAsync(0)) return;   // one already running (a control asked for a fresh look)
        try
        {
            var running = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in System.Diagnostics.Process.GetProcesses()) { try { running.Add(p.ProcessName); } catch { } p.Dispose(); }
            var tasks = new List<Task<(List<MacSource>, string?)>>();
            if (running.Contains("Spotify") || running.Contains("Music"))
                tasks.Add(Task.Run(() => (ParseApps(Sh.RunWithTimeout(3000, "osascript", "-e", AppsScanScript) ?? ""), (string?)null)));
            foreach (var (key, app, _) in Browsers)
            {
                if (!running.Contains(app)) continue;
                tasks.Add(Task.Run(() =>
                {
                    var (items, blocked) = ParseBrowserTabs(key, Sh.RunWithTimeout(5000, "osascript", "-e", BrowserScanScript(app)) ?? "");
                    return (items, blocked ? app : null);
                }));
            }
            var results = await Task.WhenAll(tasks);
            var blockedApps = results.Select(r => r.Item2).OfType<string>().ToHashSet();
            foreach (var app in blockedApps)
                lock (_jsWarned)
                    if (_jsWarned.Add(app))
                        log.LogInformation("media: {App} does not let PhoneBox read or control its tabs one by one. To see every tab that plays on the phone, turn on {Where}",
                            app, app == "Safari" ? "Develop › Allow JavaScript from Apple Events in Safari" : $"View › Developer › Allow JavaScript from Apple Events in {app}");
            var items = results.SelectMany(r => r.Item1).ToList();
            var blockedKeys = Browsers.Where(b => blockedApps.Contains(b.App)).Select(b => b.Key).ToHashSet();
            _scan = new SessionScan(items, blockedKeys, DateTimeOffset.UtcNow);

            // artwork per player, downloaded again only when its source changes
            var wanted = items.Where(i => i.ArtUrl.Length > 0).ToDictionary(i => i.Id, i => i.ArtUrl);
            lock (_sessionArt)
                foreach (var gone in _sessionArt.Keys.Where(k => !wanted.ContainsKey(k)).ToList()) _sessionArt.Remove(gone);
            foreach (var (id, url) in wanted)
            {
                lock (_sessionArt) if (_sessionArt.TryGetValue(id, out var have) && have.Key == url) continue;
                var art = url.StartsWith("yt:", StringComparison.Ordinal)
                    ? await LoadArt(YouTubeThumb(url[3..], "maxresdefault")) ?? await LoadArt(YouTubeThumb(url[3..], "mqdefault"))
                    : await LoadArt(url);
                lock (_sessionArt) _sessionArt[id] = (url, art, art is null ? 0 : ++_sessionArtId);
            }
        }
        finally { _scanGate.Release(); }
    }

    private List<MacSource> CurrentSessions()
    {
        if (!OperatingSystem.IsMacOS())
            return _snap is { } s ? [new MacSource("system", s.App, s.Title, s.Artist, s.Album, s.Playing, s.PosMs, s.DurMs, s.CanSeek, NowPlaying: true)] : [];
        var scan = _scan;
        var elapsed = (long)Math.Max(0, (DateTimeOffset.UtcNow - scan.At).TotalMilliseconds);
        // scanned positions are a few seconds old: carry the playing ones forward
        var items = scan.Items.Select(x => x.Playing ? x with { PosMs = x.DurMs > 0 ? Math.Min(x.DurMs, x.PosMs + elapsed) : x.PosMs + elapsed } : x);
        return MergeSessions(_now, items);
    }

    /// <summary>Every open player, the Now Playing one first; empty when nothing plays.</summary>
    public IReadOnlyList<MediaSession> Sessions()
    {
        var art = Art;
        var snap = _snap;
        return CurrentSessions().Select(x =>
        {
            int artId = 0;
            if (x.NowPlaying && art is not null) artId = snap?.ArtId ?? 0;
            else lock (_sessionArt) if (_sessionArt.TryGetValue(x.Id, out var a)) artId = a.Id;
            return new MediaSession(x.Id, x.App, x.Title, x.Artist, x.Album, x.Playing, x.PosMs, x.DurMs, x.CanSeek, artId);
        }).ToList();
    }

    public AlbumArt? SessionArt(string id)
    {
        if (CurrentSessions().FirstOrDefault(x => x.Id == id) is not { } x) return null;
        if (x.NowPlaying && Art is { } art) return art;
        lock (_sessionArt) return _sessionArt.TryGetValue(id, out var a) ? a.Art : null;
    }

    private static readonly System.Text.RegularExpressions.Regex TabIdPattern = new(@"^(chrome|brave|edge|arc|vivaldi|safari):(\d{1,15}):(\d{1,15})$");

    /// <summary>The script that does <paramref name="action"/> to a tab's player, or null; the only number in it is one we formatted.</summary>
    internal static string? TabControlJs(string action, long? ms) => action switch
    {
        "playpause" => "(function(){" + PickMedia + "if(!m)return '';if(m.paused)m.play();else m.pause();return 'ok'})()",
        "seek" when ms is { } v => "(function(){" + PickMedia + "if(!m)return '';m.currentTime=" + (Math.Max(0, v) / 1000.0).ToString(CultureInfo.InvariantCulture) + ";return 'ok'})()",
        "next" => "(function(){var b=document.querySelector('.ytp-next-button,ytmusic-player-bar .next-button,[data-uia=control-next]');if(!b)return '';b.click();return 'ok'})()",
        "prev" => "(function(){" + PickMedia + "if(!m)return '';if(/youtube\\.com$/.test(location.hostname)&&m.currentTime<3){history.back();return 'ok'}m.currentTime=0;return 'ok'})()",
        _ => null,
    };

    /// <summary>Controls one player of <see cref="Sessions"/>; no session (or "system") is the shared controls.</summary>
    public async Task<bool> Control(string action, long? ms, string? session)
    {
        if (string.IsNullOrEmpty(session) || session == "system") return await Control(action, ms);
        if (CurrentSessions().FirstOrDefault(x => x.Id == session) is not { } target) return false;
        bool ok;
        if (session is "spotify" or "music") ok = ControlApp(session == "spotify" ? "Spotify" : "Music", action, ms);
        else if (TabIdPattern.Match(session) is { Success: true } m)
        {
            var key = m.Groups[1].Value;
            // without JavaScript from Apple Events only the Now Playing tab can be reached, through the system controls
            if (_scan.JsBlocked.Contains(key)) return target.NowPlaying && await Control(action, ms);
            if (TabControlJs(action, ms) is not { } js) return false;
            var (w, t) = (long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
            var app = Browsers.First(b => b.Key == key).App;
            var run = key == "safari" ? $"do JavaScript {AsString(js)} in tab {t} of window id {w}" : $"execute tab id {t} of window id {w} javascript {AsString(js)}";
            var outText = Sh.RunWithTimeout(3000, "osascript", "-e", $"if application \"{app}\" is not running then return \"\"\ntell application \"{app}\" to return {run}");
            ok = outText?.Trim() == "ok";
        }
        else return false;
        if (ok && action == "playpause")
        {
            // show the flip at once; the next scan (started now, it runs off this request) confirms it
            var scan = _scan;
            _scan = scan with { Items = scan.Items.Select(x => x.Id == session ? x with { Playing = !x.Playing } : x).ToList() };
        }
        try { await Poll(); } catch { }
        _ = Task.Run(async () => { try { await ScanSessions(); } catch { } });
        return ok;
    }

    /// <summary>The next reading asks the player for its position instead of counting on from the last answer.</summary>
    internal void ForgetPlayerPosition() => _macAnchorAt = DateTimeOffset.MinValue;

    public async Task<bool> PauseAsync()
    {
        await RefreshIfIdle();
        var snap = _snap;
        if (snap is not { Playing: true }) return false;
        bool ok;
        if (OperatingSystem.IsMacOS())
        {
            if (_macSystemMedia) ok = Sh.Run("nowplaying-cli", "pause") is not null;
            else if (snap.App is "Spotify" or "Music")
                ok = Sh.Run("osascript", "-e", $"tell application \"{snap.App}\" to pause") is not null;
            else return false;
        }
        else ok = Sh.Run("playerctl", "pause") is not null;
        try { await Poll(); } catch { }
        return ok;
    }
}
