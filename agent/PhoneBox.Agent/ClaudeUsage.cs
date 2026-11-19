using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

public sealed record UsageTotals(long Tokens, double CostUsd);
public sealed record UsageDay(string Date, long Tokens, double CostUsd);
public sealed record UsageProject(string Name, long TokensToday, long TokensWeek, double CostWeekUsd);
public sealed record UsageModel(string Model, long TokensWeek);
public sealed record UsageReport(UsageTotals Today, UsageTotals Week, IReadOnlyList<UsageDay> Daily, IReadOnlyList<UsageProject> Projects,
    IReadOnlyList<UsageModel> Models, long UpdatedAt, bool CostPartial);

/// <summary>
/// Claude Code token use and its API-price cost, from the assistant messages' "usage" in ~/.claude/projects/**/*.jsonl.
/// Computed in the background every 60 s, reading only the bytes appended since the last pass; /api/usage serves the
/// cached report.
/// </summary>
public sealed class ClaudeUsage(ILogger<ClaudeUsage> log) : BackgroundService
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private const long MaxBytesPerPass = 256L * 1024 * 1024;
    private static readonly TimeSpan Window = TimeSpan.FromDays(8);

    private readonly Dictionary<string, long> _offsets = new();          // file -> bytes already parsed
    private readonly Dictionary<string, UsageEntry> _entries = new();    // message id -> its usage (deduplicated)
    private volatile UsageReport _report = Aggregate([], DateTimeOffset.Now, TimeZoneInfo.Local);

    public UsageReport Report() => _report;
    internal IEnumerable<UsageEntry> Entries => _entries.Values; // tests; the background loop owns _entries

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Yield();
        while (!ct.IsCancellationRequested)
        {
            var more = false;
            try
            {
                more = ReadPass(Path.Combine(Home, ".claude", "projects"));
                _report = Aggregate(_entries.Values, DateTimeOffset.Now, TimeZoneInfo.Local);
            }
            catch (Exception ex) { log.LogDebug("claude usage: {Msg}", ex.Message); }
            // a pass that hit the byte cap continues soon; otherwise once a minute
            await Task.Delay(more ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(60), ct);
        }
    }

    /// <returns>true when the byte cap left appended data for the next pass.</returns>
    internal bool ReadPass(string root)
    {
        if (!Directory.Exists(root)) return false;
        var cutoff = DateTime.UtcNow - Window;
        var budget = MaxBytesPerPass;
        var seen = new HashSet<string>();
        foreach (var f in new DirectoryInfo(root).EnumerateFiles("*.jsonl", SearchOption.AllDirectories))
        {
            if (f.LastWriteTimeUtc < cutoff) continue;
            seen.Add(f.FullName);
            var offset = _offsets.GetValueOrDefault(f.FullName);
            if (f.Length < offset) offset = 0; // rewritten: read it again (ids dedupe the rest)
            if (f.Length == offset) continue;
            if (budget <= 0) return true;
            try { _offsets[f.FullName] = ReadAppended(f.FullName, offset, ref budget); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        foreach (var gone in _offsets.Keys.Where(k => !seen.Contains(k)).ToList()) _offsets.Remove(gone);
        var oldest = DateTimeOffset.UtcNow.Subtract(Window).ToUnixTimeMilliseconds();
        foreach (var (id, e) in _entries.Where(kv => kv.Value.At < oldest).ToList()) _entries.Remove(id);
        return budget <= 0;
    }

    /// <summary>Parses complete lines from <paramref name="offset"/> on; returns the offset after the last complete line.</summary>
    private long ReadAppended(string path, long offset, ref long budget)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        fs.Seek(offset, SeekOrigin.Begin);
        var buf = new byte[1024 * 1024];
        var pending = new MemoryStream();
        long pos = offset;
        int n;
        while (budget > 0 && (n = fs.Read(buf, 0, buf.Length)) > 0)
        {
            budget -= n;
            var start = 0;
            for (var i = 0; i < n; i++)
            {
                if (buf[i] != (byte)'\n') continue;
                ReadOnlySpan<byte> line;
                if (pending.Length > 0) { pending.Write(buf, start, i - start); line = pending.GetBuffer().AsSpan(0, (int)pending.Length); }
                else line = buf.AsSpan(start, i - start);
                pos += line.Length + 1;
                Add(line);
                pending.SetLength(0);
                start = i + 1;
            }
            pending.Write(buf, start, n - start);
        }
        return pos; // a partial last line is read again next time
    }

    private void Add(ReadOnlySpan<byte> line)
    {
        if (line.IndexOf("\"usage\""u8) < 0) return;
        if (ParseLine(line) is not { } parsed) return;
        // the same message is logged once per content block: keep the most complete count
        if (!_entries.TryGetValue(parsed.Id, out var prev) || parsed.Entry.Tokens > prev.Tokens) _entries[parsed.Id] = parsed.Entry;
    }

    internal sealed record UsageEntry(long At, string Model, string Project, long Tokens, double CostUsd, bool Priced);

    /// <summary>One transcript line: an assistant message with usage, or null.</summary>
    internal static (string Id, UsageEntry Entry)? ParseLine(ReadOnlySpan<byte> line)
    {
        try
        {
            var reader = new Utf8JsonReader(line);
            using var doc = JsonDocument.ParseValue(ref reader);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || Str(r, "type") != "assistant"
                || !r.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object
                || !m.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return null;
            if (Str(m, "id") is not { Length: > 0 } id || !DateTimeOffset.TryParse(Str(r, "timestamp"), out var ts)) return null;
            long input = Long(u, "input_tokens"), output = Long(u, "output_tokens"), cacheWrite = Long(u, "cache_creation_input_tokens"), cacheRead = Long(u, "cache_read_input_tokens");
            var tokens = input + output + cacheWrite + cacheRead;
            if (tokens == 0) return null; // synthetic messages (errors, interrupts)
            // cache writes are priced by lifetime: the 1-hour ones cost more
            long write1h = u.TryGetProperty("cache_creation", out var cc) ? Long(cc, "ephemeral_1h_input_tokens") : 0;
            write1h = Math.Min(write1h, cacheWrite);
            var model = Str(m, "model") ?? "unknown";
            var cwd = Str(r, "cwd") ?? "";
            var project = Path.GetFileName(cwd.TrimEnd('/', '\\')) is { Length: > 0 } p ? p : "other";
            var price = Prices.For(model);
            var cost = price is null ? 0 : (input * price.Input + output * price.Output + (cacheWrite - write1h) * price.Input * 1.25
                + write1h * price.Input * 2 + cacheRead * price.CacheRead) / 1_000_000;
            return (id, new UsageEntry(ts.ToUnixTimeMilliseconds(), model, project, tokens, cost, price is not null));
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    internal static UsageReport Aggregate(IEnumerable<UsageEntry> entries, DateTimeOffset now, TimeZoneInfo tz)
    {
        var today = TimeZoneInfo.ConvertTime(now, tz).Date;
        var firstDay = today.AddDays(-6);
        var days = Enumerable.Range(0, 7).Select(i => firstDay.AddDays(i)).ToArray();
        var perDay = days.ToDictionary(d => d, _ => (Tokens: 0L, Cost: 0.0));
        var projects = new Dictionary<string, (long Today, long Week, double Cost)>();
        var models = new Dictionary<string, long>();
        var partial = false;
        foreach (var e in entries)
        {
            var day = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(e.At), tz).Date;
            if (day < firstDay || day > today) continue;
            var d = perDay[day];
            perDay[day] = (d.Tokens + e.Tokens, d.Cost + e.CostUsd);
            var p = projects.GetValueOrDefault(e.Project);
            projects[e.Project] = (p.Today + (day == today ? e.Tokens : 0), p.Week + e.Tokens, p.Cost + e.CostUsd);
            models[e.Model] = models.GetValueOrDefault(e.Model) + e.Tokens;
            partial |= !e.Priced;
        }
        var t = perDay[today];
        return new UsageReport(
            new UsageTotals(t.Tokens, Math.Round(t.Cost, 4)),
            new UsageTotals(perDay.Values.Sum(v => v.Tokens), Math.Round(perDay.Values.Sum(v => v.Cost), 4)),
            days.Select(dd => new UsageDay(dd.ToString("yyyy-MM-dd"), perDay[dd].Tokens, Math.Round(perDay[dd].Cost, 4))).ToList(),
            projects.OrderByDescending(kv => kv.Value.Week).Take(8)
                .Select(kv => new UsageProject(kv.Key, kv.Value.Today, kv.Value.Week, Math.Round(kv.Value.Cost, 4))).ToList(),
            models.OrderByDescending(kv => kv.Value).Select(kv => new UsageModel(kv.Key, kv.Value)).ToList(),
            now.ToUnixTimeMilliseconds(), partial);
    }

    private static string? Str(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static long Long(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;
}

/// <summary>
/// Anthropic API list prices in USD per million tokens (input, output, cache read). Cache writes are 1.25x input for
/// the 5-minute lifetime and 2x for 1 hour. Models match by the longest id prefix, so dated ids
/// ("claude-sonnet-4-5-20250929") find their family. The one place to update when prices change.
/// ponytail: Haiku 5.5's long-prompt tier ($0.50 / $2.50 above 100K input) is not modelled.
/// </summary>
internal static class Prices
{
    internal sealed record Price(double Input, double Output, double CacheRead);

    private static readonly (string Prefix, Price Price)[] Table =
    [
        ("claude-fable-5-2", new(8, 40, 0.20)),
        ("claude-mythos-5-2", new(8, 40, 0.20)),
        ("claude-fable-5-1", new(10, 50, 0.25)),
        ("claude-mythos-5-1", new(10, 50, 0.25)),
        ("claude-fable-5", new(10, 50, 1.00)),
        ("claude-mythos-5", new(10, 50, 1.00)),
        ("claude-opus-5-5", new(4, 20, 0.20)),
        ("claude-opus-5", new(5, 25, 0.50)),
        ("claude-opus-4-8", new(5, 25, 0.50)),
        ("claude-opus-4-7", new(5, 25, 0.50)),
        ("claude-opus-4-6", new(5, 25, 0.50)),
        ("claude-opus-4-5", new(5, 25, 0.50)),
        ("claude-opus-4", new(15, 75, 1.50)), // Opus 4 and 4.1
        ("claude-sonnet-5-5", new(2, 10, 0.10)),
        ("claude-sonnet-5", new(2, 10, 0.20)),
        ("claude-sonnet-4", new(3, 15, 0.30)), // Sonnet 4, 4.5, 4.6
        ("claude-3-7-sonnet", new(3, 15, 0.30)),
        ("claude-haiku-5-5", new(0.10, 0.50, 0.01)),
        ("claude-haiku-4-5", new(1, 5, 0.10)),
        ("claude-3-5-haiku", new(0.80, 4, 0.08)),
    ];

    public static Price? For(string model) =>
        Table.Where(t => model.StartsWith(t.Prefix, StringComparison.OrdinalIgnoreCase)).OrderByDescending(t => t.Prefix.Length).Select(t => t.Price).FirstOrDefault();
}
