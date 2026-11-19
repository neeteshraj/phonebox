using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

public class SessionPromptsTests
{
    private static SessionPrompts.Run NewRun(string tool = "claude", List<SessionRunSnapshot>? published = null) =>
        new("r1", "claude:abc", tool, "hello", s => published?.Add(s));

    [Fact]
    public void ClaudeStreamJsonBecomesItems()
    {
        var run = NewRun();
        // shapes captured from a real `claude -p --output-format stream-json --verbose` run
        Assert.False(run.Apply("""{"type":"system","subtype":"init","cwd":"/tmp/x","session_id":"s"}"""));
        Assert.True(run.Apply("""{"type":"assistant","message":{"id":"m1","role":"assistant","content":[{"type":"text","text":"Let me look."}]}}"""));
        Assert.True(run.Apply("""{"type":"assistant","message":{"id":"m1","role":"assistant","content":[{"type":"tool_use","id":"t","name":"Bash","input":{"command":"git status","description":"Status"}}]}}"""));
        Assert.True(run.Apply("""{"type":"assistant","message":{"id":"m2","content":[{"type":"text","text":"Clean."}]}}"""));
        Assert.True(run.Apply("""{"type":"assistant","message":{"id":"m2","content":[{"type":"text","text":"Nothing to commit."}]}}"""));
        Assert.False(run.Apply("""{"type":"rate_limit_event","rate_limit_info":{}}"""));
        Assert.True(run.Apply("""{"type":"result","subtype":"success","is_error":false,"result":"Clean.\n\nNothing to commit."}"""));
        run.Finish(0, "");

        var s = run.Snapshot();
        Assert.Equal("done", s.Status);
        Assert.NotNull(s.EndedAt);
        Assert.Equal(
            [new("user", "hello"), new("assistant", "Let me look."), new("tool", "Bash: git status"), new("assistant", "Clean.\n\nNothing to commit.")],
            s.Items);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{broken")]
    [InlineData("""{"type":"assistant","message":"oops"}""")]
    [InlineData("""{"type":"assistant","message":{"content":[1,"x",{"type":"text","text":5}]}}""")]
    [InlineData("""{"type":"assistant","message":{"content":{"type":"text"}}}""")]
    public void MalformedLinesAreIgnored(string line)
    {
        var run = NewRun();
        Assert.False(run.Apply(line));
        Assert.Single(run.Snapshot().Items);
    }

    [Fact]
    public void ErrorResultAndFailedExitAreErrors()
    {
        var run = NewRun();
        run.Apply("""{"type":"result","subtype":"error_during_execution","is_error":true,"result":"No conversation found"}""");
        run.Finish(1, "");
        Assert.Equal("error", run.Snapshot().Status);
        Assert.Equal(new RunItem("error", "No conversation found"), run.Snapshot().Items[^1]);

        var crashed = NewRun();
        crashed.Finish(1, "boom: bad flag");
        Assert.Equal(new RunItem("error", "boom: bad flag"), crashed.Snapshot().Items[^1]);
    }

    [Fact]
    public void CancelAndTimeout()
    {
        var run = NewRun();
        run.Cancel();
        run.Finish(137, "");
        Assert.Equal("cancelled", run.Snapshot().Status);

        var slow = NewRun();
        slow.Kill("timed out");
        slow.Finish(137, "");
        Assert.Equal("error", slow.Snapshot().Status);
        Assert.Equal(new RunItem("error", "timed out"), slow.Snapshot().Items[^1]);
    }

    [Fact]
    public void FinalSnapshotIsAlwaysPublished()
    {
        var pub = new List<SessionRunSnapshot>();
        var run = NewRun(published: pub);
        run.Changed(force: true);
        run.Apply("""{"type":"assistant","message":{"id":"m","content":[{"type":"text","text":"hi"}]}}""");
        run.Changed(); // within 250 ms: deferred
        run.Finish(0, "");
        Assert.Equal("error", pub[^1].Status); // no result line from claude
    }

    [Fact]
    public void ItemsAreCappedButThePromptStays()
    {
        var run = NewRun();
        for (var i = 0; i < 400; i++)
            run.Apply($$$"""{"type":"assistant","message":{"id":"m{{{i}}}","content":[{"type":"tool_use","name":"Read","input":{"file_path":"/f{{{i}}}"}}]}}""");
        var items = run.Snapshot().Items;
        Assert.Equal(200, items.Count);
        Assert.Equal("user", items[0].Role);
        Assert.Equal("Read: /f399", items[^1].Text);
    }

    [Fact]
    public void CodexJsonBecomesItems()
    {
        var run = NewRun("codex");
        Assert.True(run.Apply("""{"type":"item.completed","item":{"id":"i","type":"command_execution","command":"ls -la"}}"""));
        Assert.True(run.Apply("""{"type":"item.completed","item":{"id":"j","type":"agent_message","text":"Done."}}"""));
        Assert.False(run.Apply("""{"type":"turn.started"}"""));
        run.Finish(0, "");
        Assert.Equal("done", run.Snapshot().Status);
        Assert.Equal([new("user", "hello"), new("tool", "Bash: ls -la"), new("assistant", "Done.")], run.Snapshot().Items);
    }

    [Theory]
    [InlineData("Bash", """{"command":"git  status\n--short","description":"x"}""", "Bash: git status --short")]
    [InlineData("Edit", """{"file_path":"/a/b.cs","old_string":"x"}""", "Edit: /a/b.cs")]
    [InlineData("Grep", """{"pattern":"TODO","path":"src"}""", "Grep: TODO")]
    [InlineData("WebFetch", """{"url":"https://x.dev","prompt":"p"}""", "WebFetch: https://x.dev")]
    [InlineData("mcp__github__create_issue", """{}""", "github › create_issue")]
    [InlineData("TodoWrite", """{"todos":[]}""", "TodoWrite")]
    public void ToolSummaries(string name, string input, string expected) =>
        Assert.Equal(expected, SessionPrompts.ToolSummary(name, JsonDocument.Parse(input).RootElement));

    [Fact]
    public void ToolSummaryIsTruncated()
    {
        var s = SessionPrompts.ToolSummary("Bash", JsonDocument.Parse($$$"""{"command":"{{{new string('a', 500)}}}"}""").RootElement);
        Assert.Equal(121, s.Length);
        Assert.EndsWith("…", s);
    }

    [Fact]
    public void BinaryResolutionPrefersPathThenTheUsualPlaces()
    {
        var home = "/Users/me";
        string[] present = ["/Users/me/.local/bin/claude", "/opt/homebrew/bin/claude", "/custom/claude"];
        Assert.Equal("/custom/claude", SessionPrompts.ResolveBinary("claude", "/usr/bin:/custom", home, present.Contains));
        Assert.Equal("/Users/me/.local/bin/claude", SessionPrompts.ResolveBinary("claude", "/usr/bin:/bin", home, present.Contains));
        Assert.Equal("/opt/homebrew/bin/claude", SessionPrompts.ResolveBinary("claude", null, home, p => p == "/opt/homebrew/bin/claude"));
        Assert.Equal("/Users/me/.bun/bin/claude", SessionPrompts.ResolveBinary("claude", "", home, p => p == "/Users/me/.bun/bin/claude"));
        Assert.Null(SessionPrompts.ResolveBinary("codex", "/usr/bin", home, present.Contains));
    }

    [Fact]
    public void CodexSessionIdComesFromTheRolloutName()
    {
        Assert.Equal("0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b",
            SessionPrompts.CodexSessionId("codex:rollout-2025-10-01T10-20-30-0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b.jsonl"));
        Assert.Null(SessionPrompts.CodexSessionId("codex:rollout-x.jsonl"));
    }

    [Fact]
    public void CwdStaysOffTheWire()
    {
        var json = JsonSerializer.Serialize(new AgentSession("claude:x", "claude", "n", "p", "idle", null, 0, 0, Cwd: "/secret"));
        Assert.DoesNotContain("secret", json);
    }
}
