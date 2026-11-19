using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>New session from the phone, edit diffs, "allow for this session" and the usage report.</summary>
[Collection("Approval request expiry")] // one test sets the static PhoneLastSeen
public sealed class PhoneSessionFeaturesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pb-phone-features-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Line(object o) => JsonSerializer.Serialize(o);

    // ---------------------------------------------------------------- projects

    [Fact]
    public void ProjectsComeFromTheCwdInTheMostRecentTranscript()
    {
        var projects = Path.Combine(_dir, "projects");
        var a = Directory.CreateDirectory(Path.Combine(projects, "-weird-encoded-name")).FullName;
        // the older transcript points elsewhere: only the most recent one counts
        File.WriteAllText(Path.Combine(a, "old.jsonl"), Line(new { type = "user", cwd = "/old/place" }) + "\n");
        File.SetLastWriteTimeUtc(Path.Combine(a, "old.jsonl"), DateTime.UtcNow.AddDays(-3));
        File.WriteAllText(Path.Combine(a, "new.jsonl"),
            Line(new { type = "session-owner" }) + "\n"
            + Line(new { type = "assistant", message = new { x = new { cwd = "/nested/not/this" } }, cwd = "/work/my-app" }) + "\n");
        File.SetLastWriteTimeUtc(Path.Combine(a, "new.jsonl"), DateTime.UtcNow.AddHours(-1));
        var b = Directory.CreateDirectory(Path.Combine(projects, "gone")).FullName;
        File.WriteAllText(Path.Combine(b, "s.jsonl"), Line(new { cwd = "/deleted/folder" }) + "\n");
        Directory.CreateDirectory(Path.Combine(projects, "empty"));

        var running = new[] { new AgentSession("claude:1", "claude", "n", "p", "busy", null, Since: 5_000_000_000_000, StartedAt: 1, Cwd: "/work/other") };
        var exists = new HashSet<string> { "/work/my-app", "/work/other", "/old/place" };
        var list = ClaudeProjects.Scan(projects, running, exists.Contains, new ConcurrentDictionary<string, string>());

        Assert.Equal(["/work/other", "/work/my-app"], list.Select(p => p.Cwd));
        Assert.Equal("my-app", list[1].Name);
        Assert.Equal(new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(a, "new.jsonl"))).ToUnixTimeMilliseconds(), list[1].LastUsed);
    }

    [Fact]
    public void ProjectsAreCappedAtTwenty()
    {
        var running = Enumerable.Range(0, 30).Select(i => new AgentSession($"claude:{i}", "claude", "n", "p", "idle", null, i, 0, Cwd: $"/p/{i}"));
        var list = ClaudeProjects.Scan(Path.Combine(_dir, "none"), running, _ => true);
        Assert.Equal(20, list.Count);
        Assert.Equal("/p/29", list[0].Cwd);
    }

    [Fact]
    public void OnlyListedFoldersAreAllowed()
    {
        var projects = Path.Combine(_dir, "projects");
        var work = Directory.CreateDirectory(Path.Combine(_dir, "work", "app")).FullName;
        var p = Directory.CreateDirectory(Path.Combine(projects, "x")).FullName;
        File.WriteAllText(Path.Combine(p, "s.jsonl"), Line(new { cwd = work }) + "\n");
        var list = new ClaudeProjects(new SessionMonitor(NullLogger<SessionMonitor>.Instance)) { ProjectsDir = projects };

        Assert.Equal([work], list.List().Select(x => x.Cwd));
        Assert.True(list.IsAllowed(work));
        Assert.False(list.IsAllowed(_dir));
        Assert.False(list.IsAllowed(work + "/.."));
        Assert.False(list.IsAllowed(null));
    }

    [Fact]
    public void NewSessionRefusesAFolderNotInTheList()
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, """{"claude":{"phonePrompts":true}}""");
        var prompts = new SessionPrompts(new ConfigStore(path), new SessionMonitor(NullLogger<SessionMonitor>.Instance),
            new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_dir, "events.json")), NullLogger<SessionPrompts>.Instance);
        var allowed = new HashSet<string?> { "/work/my-app" };

        // an existing folder that the list does not offer is still refused: never an arbitrary path
        Assert.Equal(400, prompts.StartNew(_dir, "hi", allowed.Contains).Status);
        Assert.Equal(400, prompts.StartNew(null, "hi", allowed.Contains).Status);
        Assert.Equal(400, prompts.StartNew("/work/my-app", " ", allowed.Contains).Status);

        File.WriteAllText(path, """{"claude":{"phonePrompts":false}}""");
        var off = new SessionPrompts(new ConfigStore(path), new SessionMonitor(NullLogger<SessionMonitor>.Instance),
            new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_dir, "events2.json")), NullLogger<SessionPrompts>.Instance);
        Assert.Equal(403, off.StartNew("/work/my-app", "hi", allowed.Contains).Status);
    }

    [Fact]
    public void ANewSessionRunLearnsItsRealId()
    {
        var run = new SessionPrompts.Run("r1", "new:r1", "claude", "hello", _ => { });
        Assert.Null(run.Snapshot().RealSessionId);
        Assert.True(run.Apply("""{"type":"system","subtype":"init","cwd":"/x","session_id":"0b1c-uuid"}"""));
        Assert.Equal("claude:0b1c-uuid", run.Snapshot().RealSessionId);
        Assert.Equal("new:r1", run.Snapshot().SessionId);
        var json = JsonSerializer.Serialize(run.Snapshot(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"realSessionId\":\"claude:0b1c-uuid\"", json);

        // a resumed session already has its id: the field stays off the wire
        var resumed = new SessionPrompts.Run("r2", "claude:abc", "claude", "hello", _ => { });
        Assert.False(resumed.Apply("""{"type":"system","subtype":"init","session_id":"abc"}"""));
        Assert.DoesNotContain("realSessionId", JsonSerializer.Serialize(resumed.Snapshot(), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    // ---------------------------------------------------------------- diffs

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void DiffsForFileTools()
    {
        var edit = ClaudeHook.BuildDiff("Edit", J("""{"file_path":"/a.cs","old_string":"x = 1","new_string":"x = 2"}"""))!;
        Assert.Equal("/a.cs", edit.Path);
        Assert.Equal([new DiffHunk("x = 1", "x = 2")], edit.Hunks);

        var multi = ClaudeHook.BuildDiff("MultiEdit", J("""{"file_path":"/b.cs","edits":[{"old_string":"a","new_string":"b"},{"old_string":"c","new_string":"d"}]}"""))!;
        Assert.Equal([new DiffHunk("a", "b"), new DiffHunk("c", "d")], multi.Hunks);

        var write = ClaudeHook.BuildDiff("Write", J("""{"file_path":"/c.txt","content":"hello"}"""))!;
        Assert.Equal([new DiffHunk("", "hello")], write.Hunks);

        var nb = ClaudeHook.BuildDiff("NotebookEdit", J("""{"notebook_path":"/n.ipynb","new_source":"print(1)"}"""))!;
        Assert.Equal("/n.ipynb", nb.Path);
        Assert.Equal([new DiffHunk("", "print(1)")], nb.Hunks);

        Assert.Null(ClaudeHook.BuildDiff("Bash", J("""{"command":"ls"}""")));
    }

    [Fact]
    public void DiffsAreCapped()
    {
        var edits = string.Join(",", Enumerable.Range(0, 30).Select(i => $$"""{"old_string":"{{i}}","new_string":"{{new string('n', 5000)}}"}"""));
        var d = ClaudeHook.BuildDiff("MultiEdit", J($$"""{"file_path":"/f","edits":[{{edits}}]}"""))!;
        Assert.Equal(20, d.Hunks.Count);
        Assert.Equal(4001, d.Hunks[0].New.Length);
        Assert.EndsWith("…", d.Hunks[0].New);
    }

    [Fact]
    public void ApprovalJsonHasDiffAndCanAllowSession()
    {
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var p = new Approval("i", "s", "sess", "proj", "Edit a file", "/a", null, 0, 1, "permission", new ApprovalDiff("/a", [new("o", "n")]));
        var json = JsonSerializer.Serialize(p, opts);
        Assert.Contains("\"canAllowSession\":true", json);
        Assert.Contains("\"diff\":{\"path\":\"/a\",\"hunks\":[{\"old\":\"o\",\"new\":\"n\"}]}", json);
        var q = JsonSerializer.Serialize(new Approval("i", "s", "sess", "proj", "Q", "d", null, 0, 1, "question"), opts);
        Assert.Contains("\"canAllowSession\":false", q);
        Assert.DoesNotContain("\"diff\"", q);
    }

    // ---------------------------------------------------------------- allow for this session

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void AllowForSessionMatchesSessionToolAndRuleAndExpires()
    {
        var clock = new Clock();
        using var push = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_dir, "e.json"));
        var store = new ApprovalStore(push, NullLogger<ApprovalStore>.Instance, clock);
        store.AllowForSession("s1", "Bash", "npm test");
        store.AllowForSession("s1", "Edit", null);
        store.AllowForSession("", "Write", null); // no session id: never remembered

        Assert.True(store.AllowedForSession("s1", "Bash", "npm test"));
        Assert.False(store.AllowedForSession("s1", "Bash", "npm test && rm -rf /")); // exact command only
        Assert.False(store.AllowedForSession("s2", "Bash", "npm test"));             // another session
        Assert.True(store.AllowedForSession("s1", "Edit", null));
        Assert.False(store.AllowedForSession("s1", "Write", null));
        Assert.False(store.AllowedForSession("", "Write", null));

        clock.Now += TimeSpan.FromHours(8) - TimeSpan.FromSeconds(1);
        Assert.True(store.AllowedForSession("s1", "Edit", null));
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.False(store.AllowedForSession("s1", "Edit", null));
        Assert.False(store.AllowedForSession("s1", "Bash", "npm test"));
    }

    [Fact]
    public void RuleContentAndHookOutput()
    {
        Assert.Equal("git status", ClaudeHook.RuleContent("Bash", J("""{"command":"git status"}""")));
        Assert.Null(ClaudeHook.RuleContent("Edit", J("""{"file_path":"/a"}""")));

        var bash = JsonSerializer.Serialize(ClaudeHook.Output("allow-session", "Bash", "git status"));
        Assert.Equal("""{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow","updatedPermissions":[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"git status"}],"behavior":"allow","destination":"session"}]}}}""", bash);
        var edit = JsonSerializer.Serialize(ClaudeHook.Output("allow-session", "Edit", null));
        Assert.Contains("""[{"toolName":"Edit"}]""", edit);
        Assert.Equal("""{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}""",
            JsonSerializer.Serialize(ClaudeHook.Output("allow", "Edit", null)));
        Assert.Contains("\"behavior\":\"deny\"", JsonSerializer.Serialize(ClaudeHook.Output("deny", "Edit", null)));
    }

    [Fact]
    public async Task AllowSessionIsAcceptedForPermissionsOnly()
    {
        var phone = Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, long.MaxValue);
        try
        {
            using var push = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_dir, "e2.json"));
            var store = new ApprovalStore(push, NullLogger<ApprovalStore>.Instance);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var q = store.AskAsync(new Approval("q", "s", "sess", "p", "Q", "d", null, now, now + 60_000, "question"), CancellationToken.None);
            Assert.False(store.Decide("q", "allow-session"));
            Assert.True(store.Decide("q", "skip"));
            Assert.Equal("skip", await q);

            var p = store.AskAsync(new Approval("p", "s", "sess", "p", "Run", "ls", null, now, now + 60_000), CancellationToken.None);
            Assert.True(store.Decide("p", "allow-session"));
            Assert.Equal("allow-session", await p);
        }
        finally { Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, phone); }
    }

    // ---------------------------------------------------------------- usage

    private static string Assistant(string id, string model, string ts, string cwd, long input, long output, long write, long read, long write1h = 0) =>
        Line(new
        {
            type = "assistant", timestamp = ts, cwd,
            message = new
            {
                id, model,
                usage = new { input_tokens = input, output_tokens = output, cache_creation_input_tokens = write, cache_read_input_tokens = read,
                    cache_creation = new { ephemeral_1h_input_tokens = write1h, ephemeral_5m_input_tokens = write - write1h } },
            },
        });

    private static ClaudeUsage.UsageEntry Parse(string line) => ClaudeUsage.ParseLine(Encoding.UTF8.GetBytes(line))!.Value.Entry;

    [Fact]
    public void UsageLinePricing()
    {
        // Opus 4.5: $5 in, $25 out, $0.50 cache read; 5-minute writes 1.25x input, 1-hour writes 2x
        var e = Parse(Assistant("m", "claude-opus-4-5-20251101", "2026-11-19T10:00:00Z", "/w/phonebox", 1_000_000, 1_000_000, 2_000_000, 1_000_000, write1h: 1_000_000));
        Assert.Equal(5_000_000, e.Tokens);
        Assert.Equal(5 + 25 + 6.25 + 10 + 0.5, e.CostUsd, 6);
        Assert.Equal("phonebox", e.Project);
        Assert.True(e.Priced);

        var unknown = Parse(Assistant("m", "claude-future-9", "2026-11-19T10:00:00Z", "/w/x", 10, 10, 0, 0));
        Assert.False(unknown.Priced);
        Assert.Equal(0, unknown.CostUsd);
        Assert.Equal(20, unknown.Tokens);

        Assert.Null(ClaudeUsage.ParseLine(Encoding.UTF8.GetBytes(Assistant("m", "<synthetic>", "2026-11-19T10:00:00Z", "/w", 0, 0, 0, 0))));
        Assert.Null(ClaudeUsage.ParseLine(Encoding.UTF8.GetBytes("""{"type":"user","message":{"usage":{}}}""")));
        Assert.Null(ClaudeUsage.ParseLine(Encoding.UTF8.GetBytes("""{"type":"assistant","message":"usage"}""")));
        Assert.Null(ClaudeUsage.ParseLine(Encoding.UTF8.GetBytes("{broken")));
    }

    [Fact]
    public void PricesMatchTheLongestPrefix()
    {
        Assert.Equal(4, Prices.For("claude-opus-5-5")!.Input);
        Assert.Equal(5, Prices.For("claude-opus-5")!.Input);
        Assert.Equal(15, Prices.For("claude-opus-4-1-20250805")!.Input);
        Assert.Equal(3, Prices.For("claude-sonnet-4-5-20250929")!.Input);
        Assert.Equal(0.25, Prices.For("claude-fable-5-1")!.CacheRead);
        Assert.Null(Prices.For("gpt-5"));
    }

    [Fact]
    public void UsageAggregatesByLocalDayProjectAndModel()
    {
        var tz = TimeZoneInfo.Utc;
        var now = DateTimeOffset.Parse("2026-11-19T12:00:00Z");
        ClaudeUsage.UsageEntry E(string ts, string project, long tokens, double cost, string model = "claude-opus-5-5", bool priced = true) =>
            new(DateTimeOffset.Parse(ts).ToUnixTimeMilliseconds(), model, project, tokens, cost, priced);
        var r = ClaudeUsage.Aggregate([
            E("2026-11-19T01:00:00Z", "a", 100, 1.0),
            E("2026-11-18T23:00:00Z", "b", 50, 0.5),
            E("2026-11-13T00:00:00Z", "a", 10, 0.1, "claude-sonnet-5-5"), // oldest of the 7 days
            E("2026-11-12T23:59:00Z", "a", 999, 9.9),                     // 8 days ago: outside the week
        ], now, tz);

        Assert.Equal(new UsageTotals(100, 1.0), r.Today);
        Assert.Equal(new UsageTotals(160, 1.6), r.Week);
        Assert.Equal(7, r.Daily.Count);
        Assert.Equal(new UsageDay("2026-11-13", 10, 0.1), r.Daily[0]);
        Assert.Equal(new UsageDay("2026-11-19", 100, 1.0), r.Daily[^1]);
        Assert.Equal([new UsageProject("a", 100, 110, 1.1), new UsageProject("b", 0, 50, 0.5)], r.Projects);
        Assert.Equal([new UsageModel("claude-opus-5-5", 150), new UsageModel("claude-sonnet-5-5", 10)], r.Models);
        Assert.False(r.CostPartial);

        Assert.True(ClaudeUsage.Aggregate([E("2026-11-19T01:00:00Z", "a", 5, 0, "claude-x", priced: false)], now, tz).CostPartial);
    }

    [Fact]
    public void UsageReadsIncrementallyAndDedupesMessageIds()
    {
        var root = Directory.CreateDirectory(Path.Combine(_dir, "usage", "proj")).FullName;
        var file = Path.Combine(root, "s.jsonl");
        var ts = DateTimeOffset.UtcNow.ToString("O");
        // the same message logged twice (once per content block), the second with the final output count
        File.WriteAllText(file, Assistant("m1", "claude-opus-5-5", ts, "/w/app", 10, 1, 0, 0) + "\n"
                                + Assistant("m1", "claude-opus-5-5", ts, "/w/app", 10, 5, 0, 0) + "\n"
                                + Line(new { type = "user", cwd = "/w/app" }) + "\n");
        var usage = new ClaudeUsage(NullLogger<ClaudeUsage>.Instance);
        Assert.False(usage.ReadPass(Path.Combine(_dir, "usage")));
        Assert.Equal(15, Total());

        // appended: a new message, a repeat of m1, and a half-written line that is read once complete
        var half = Assistant("m3", "claude-opus-5-5", ts, "/w/app", 1000, 0, 0, 0);
        File.AppendAllText(file, Assistant("m2", "claude-opus-5-5", ts, "/w/other", 100, 0, 0, 0) + "\n"
                                 + Assistant("m1", "claude-opus-5-5", ts, "/w/app", 10, 5, 0, 0) + "\n" + half[..20]);
        usage.ReadPass(Path.Combine(_dir, "usage"));
        Assert.Equal(115, Total());
        File.AppendAllText(file, half[20..] + "\n");
        usage.ReadPass(Path.Combine(_dir, "usage"));
        Assert.Equal(1115, Total());

        long Total() => ClaudeUsage.Aggregate(usage.Entries, DateTimeOffset.Now, TimeZoneInfo.Local).Week.Tokens;
    }
}
