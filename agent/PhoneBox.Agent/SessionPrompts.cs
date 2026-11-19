using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <param name="Role">user | assistant | tool | error</param>
public sealed record RunItem(string Role, string Text);

/// <param name="Status">running | done | error | cancelled</param>
/// <param name="RealSessionId">For a new session ("new:&lt;runId&gt;"): "claude:&lt;uuid&gt;" once Claude Code reports it.</param>
public sealed record SessionRunSnapshot(string RunId, string SessionId, string Status, long StartedAt, long? EndedAt, IReadOnlyList<RunItem> Items,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? RealSessionId = null);

/// <summary>
/// Prompts sent from the phone to a Claude Code / Codex session, run through the CLI's headless resume
/// (<c>claude -p --resume</c>, <c>codex exec resume</c>) in the session's folder. The reply streams back as
/// "session-run" events; the last run per session is kept in memory. Tool permissions of a headless Claude run go
/// through the same PermissionRequest hook as the terminal ones (ClaudeHook), so they are answered on the phone.
/// </summary>
public sealed class SessionPrompts(ConfigStore cfg, SessionMonitor sessions, PushStore push, ILogger<SessionPrompts> log)
{
    public const int MaxPromptChars = 8000;
    private const int MaxActive = 3;
    private const int MaxItems = 200;
    private const int MaxItemChars = 4000;
    private const int MaxTotalChars = 32_000; // every snapshot travels whole in an event
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(15);
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private readonly ConcurrentDictionary<string, Run> _last = new(); // by session id
    private readonly ConcurrentDictionary<string, Run> _byRunId = new();
    private readonly object _startLock = new();

    public SessionRunSnapshot? Get(string sessionId) => _last.TryGetValue(sessionId, out var r) ? r.Snapshot() : null;

    public bool Cancel(string runId)
    {
        if (!_byRunId.TryGetValue(runId, out var r)) return false;
        r.Cancel();
        return true;
    }

    /// <returns>HTTP status and body for the prompt endpoint.</returns>
    public (int Status, object Body) Start(string? sessionId, string? text)
    {
        if (!cfg.Get().Claude.PhonePrompts) return (403, new { error = "Sending prompts from the phone is turned off in the settings" });
        var s = sessions.Snapshot().FirstOrDefault(x => x.Id == sessionId);
        if (s is null) return (404, new { error = "no such session" });
        if (string.IsNullOrWhiteSpace(text)) return (400, new { error = "the prompt is empty" });
        if (text.Length > MaxPromptChars) return (400, new { error = $"the prompt is longer than {MaxPromptChars} characters" });
        if (s.Status is "busy" or "waiting" or "shell") return (409, new { error = "busy" });
        if (string.IsNullOrEmpty(s.Cwd) || !Directory.Exists(s.Cwd)) return (400, new { error = "the session's folder is unknown" });

        var (binary, args) = Command(s, text);
        if (binary is null) return (400, new { error = $"{s.Tool} was not found on this PC" });

        Run run;
        lock (_startLock)
        {
            if (_last.TryGetValue(s.Id, out var prev) && prev.Active) return (409, new { error = "busy" });
            if (_last.Values.Count(r => r.Active) >= MaxActive) return (409, new { error = "busy" });
            run = new Run(ApprovalStore.NewId(), s.Id, s.Tool, text, Publish);
            if (prev is not null) _byRunId.TryRemove(prev.RunId, out _);
            _last[s.Id] = run;
            _byRunId[run.RunId] = run;
        }
        log.LogInformation("prompt run {Run} for {Session} started from the phone", run.RunId, s.Id);
        run.Changed(force: true);
        _ = Task.Run(() => ExecuteAsync(run, binary, args, s.Cwd));
        return (200, new { runId = run.RunId });
    }

    /// <summary>
    /// A new Claude Code session (<c>claude -p</c>, no --resume) in one of the folders <see cref="ClaudeProjects"/>
    /// lists: <paramref name="allowed"/> is that check, so the phone can never pick an arbitrary path.
    /// </summary>
    public (int Status, object Body) StartNew(string? cwd, string? text, Func<string?, bool> allowed)
    {
        if (!cfg.Get().Claude.PhonePrompts) return (403, new { error = "Sending prompts from the phone is turned off in the settings" });
        if (string.IsNullOrWhiteSpace(text)) return (400, new { error = "the prompt is empty" });
        if (text.Length > MaxPromptChars) return (400, new { error = $"the prompt is longer than {MaxPromptChars} characters" });
        if (!allowed(cwd) || !Directory.Exists(cwd)) return (400, new { error = "not a known project folder" });
        var binary = ResolveBinary("claude", Environment.GetEnvironmentVariable("PATH"), Home, File.Exists);
        if (binary is null) return (400, new { error = "claude was not found on this PC" });

        Run run;
        lock (_startLock)
        {
            if (_last.Values.Count(r => r.Active) >= MaxActive) return (409, new { error = "busy" });
            // finished new-session runs are only kept for the phone to read back: the last few are enough
            foreach (var old in _last.Values.Where(r => !r.Active && r.SessionId.StartsWith("new:")).OrderByDescending(r => r.StartedAt).Skip(10).ToList())
            {
                _last.TryRemove(old.SessionId, out _);
                _byRunId.TryRemove(old.RunId, out _);
            }
            var id = ApprovalStore.NewId();
            run = new Run(id, "new:" + id, "claude", text, Publish);
            _last[run.SessionId] = run;
            _byRunId[id] = run;
        }
        log.LogInformation("new session run {Run} in {Cwd} started from the phone", run.RunId, cwd);
        run.Changed(force: true);
        _ = Task.Run(() => ExecuteAsync(run, binary, ["-p", "--output-format", "stream-json", "--verbose", text], cwd!));
        return (200, new { runId = run.RunId, sessionId = run.SessionId });
    }

    private void Publish(SessionRunSnapshot snap) =>
        push.Emit("session-run", JsonSerializer.SerializeToElement(snap, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private static (string? Binary, string[] Args) Command(AgentSession s, string text)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (s.Tool == "claude" && s.Id.StartsWith("claude:"))
            return (ResolveBinary("claude", path, Home, File.Exists),
                ["-p", "--resume", s.Id["claude:".Length..], "--output-format", "stream-json", "--verbose", text]);
        if (s.Tool == "codex" && CodexSessionId(s.Id) is { } cid)
            return (ResolveBinary("codex", path, Home, File.Exists), ["exec", "--json", "resume", cid, text]);
        return (null, []);
    }

    /// <summary>The session UUID at the end of a rollout file name ("codex:rollout-…-&lt;uuid&gt;.jsonl").</summary>
    internal static string? CodexSessionId(string id) =>
        Regex.Match(id, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\.jsonl$") is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>The usual places CLIs land in; the agent under launchd / systemd gets a minimal PATH.</summary>
    internal static IEnumerable<string> SearchDirs(string? pathEnv, string home) =>
        (pathEnv ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat([Path.Combine(home, ".local", "bin"), "/opt/homebrew/bin", "/usr/local/bin",
                Path.Combine(home, ".npm-global", "bin"), Path.Combine(home, ".bun", "bin")])
            .Distinct();

    internal static string? ResolveBinary(string name, string? pathEnv, string home, Func<string, bool> exists)
    {
        // no .cmd / .bat shims on Windows: cmd.exe would parse the prompt
        string[] names = OperatingSystem.IsWindows() ? [name + ".exe"] : [name];
        return SearchDirs(pathEnv, home).SelectMany(d => names.Select(n => Path.Combine(d.Trim('"', ' '), n))).FirstOrDefault(exists);
    }

    private async Task ExecuteAsync(Run run, string binary, string[] args, string cwd)
    {
        var stderr = new StringBuilder();
        try
        {
            var psi = new ProcessStartInfo(binary)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = cwd,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            // the run's own tools (Bash: git, node…) need a real PATH too
            psi.Environment["PATH"] = string.Join(Path.PathSeparator, SearchDirs(Environment.GetEnvironmentVariable("PATH"), Home).Concat(["/usr/bin", "/bin", "/usr/sbin", "/sbin"]).Distinct());
            using var p = Process.Start(psi) ?? throw new IOException("could not start " + binary);
            p.StandardInput.Close(); // headless: nothing to type
            run.Attach(p);
            var errPump = Task.Run(async () =>
            {
                try
                {
                    while (await p.StandardError.ReadLineAsync() is { } l)
                        lock (stderr) { stderr.AppendLine(l); if (stderr.Length > 4000) stderr.Remove(0, stderr.Length - 2000); }
                }
                catch { }
            });
            using var deadline = new CancellationTokenSource(Timeout);
            using var reg = deadline.Token.Register(() => run.Kill("timed out"));
            while (await p.StandardOutput.ReadLineAsync() is { } line)
                if (run.Apply(line)) run.Changed();
            await p.WaitForExitAsync();
            await Task.WhenAny(errPump, Task.Delay(1000));
            string tail;
            lock (stderr) tail = stderr.ToString().Trim();
            run.Finish(p.ExitCode, tail);
        }
        catch (Exception ex)
        {
            log.LogWarning("prompt run {Run} failed: {Msg}", run.RunId, ex.Message);
            run.Finish(-1, ex.Message);
        }
        log.LogInformation("prompt run {Run} ended: {Status}", run.RunId, run.Snapshot().Status);
    }

    /// <summary>One run: its items, parsed from the CLI's JSON lines, and its process.</summary>
    internal sealed class Run(string runId, string sessionId, string tool, string prompt, Action<SessionRunSnapshot> publish)
    {
        public string RunId { get; } = runId;
        public string SessionId { get; } = sessionId;
        public long StartedAt => _startedAt;
        private string? _realSessionId;
        private readonly object _lock = new();
        private readonly List<RunItem> _items = [new("user", Clip(prompt))];
        private readonly long _startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        private long? _endedAt;
        private string _status = "running";
        private string? _killReason;
        private string? _lastMessageId;
        private bool _sawResult, _resultError, _sawAssistant;
        private Process? _process;
        private long _lastPublish;
        private bool _flushPending;

        public bool Active { get { lock (_lock) return _status == "running"; } }

        public SessionRunSnapshot Snapshot()
        {
            lock (_lock) return new(RunId, SessionId, _status, _startedAt, _endedAt, _items.ToList(), _realSessionId);
        }

        public void Attach(Process p)
        {
            bool kill;
            lock (_lock) { _process = p; kill = _killReason is not null; }
            if (kill) KillProcess(p);
        }

        public void Cancel() => Kill(null);

        /// <param name="reason">null = cancelled by the user; otherwise an error reason (timed out).</param>
        public void Kill(string? reason)
        {
            Process? p;
            lock (_lock)
            {
                if (_status != "running" || _killReason is not null) return;
                _killReason = reason ?? "";
                p = _process;
            }
            if (p is not null) KillProcess(p);
        }

        private static void KillProcess(Process p) { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { } }

        /// <summary>Parses one output line; false when it changed nothing (unknown, malformed, empty).</summary>
        public bool Apply(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || line[0] != '{') return false;
            try
            {
                using var doc = JsonDocument.Parse(line);
                lock (_lock) return tool == "codex" ? ApplyCodex(doc.RootElement) : ApplyClaude(doc.RootElement);
            }
            catch (JsonException) { return false; }
            catch (InvalidOperationException) { return false; } // an element of an unexpected kind
        }

        private bool ApplyClaude(JsonElement r)
        {
            switch (Str(r, "type"))
            {
                case "assistant":
                    if (!r.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object
                        || !msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return false;
                    var id = Str(msg, "id");
                    var changed = false;
                    foreach (var block in content.EnumerateArray())
                    {
                        if (Str(block, "type") == "text" && Str(block, "text") is { Length: > 0 } t)
                        {
                            // Claude Code writes one line per content block; blocks of one message read as one reply
                            if (id is not null && id == _lastMessageId && _items[^1].Role == "assistant")
                                _items[^1] = _items[^1] with { Text = Clip(_items[^1].Text + "\n\n" + t) };
                            else Add("assistant", t);
                            _sawAssistant = changed = true;
                        }
                        else if (Str(block, "type") == "tool_use" && Str(block, "name") is { } name)
                        {
                            Add("tool", ToolSummary(name, block.TryGetProperty("input", out var input) ? input : default));
                            changed = true;
                        }
                    }
                    _lastMessageId = id;
                    return changed;
                // a new session learns its id from the init line, so the phone can open the real session later
                case "system" when SessionId.StartsWith("new:") && _realSessionId is null && Str(r, "session_id") is { Length: > 0 } sid:
                    _realSessionId = "claude:" + sid;
                    return true;
                case "result":
                    _sawResult = true;
                    _resultError = r.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
                    var result = Str(r, "result");
                    if (_resultError) Add("error", result is { Length: > 0 } ? result : Str(r, "subtype") ?? "the run failed");
                    else if (!_sawAssistant && result is { Length: > 0 }) Add("assistant", result);
                    return true;
                default: return false;
            }
        }

        // codex exec --json: newer builds write {"type":"item.completed","item":{...}}, older ones {"msg":{"type":...}}
        private bool ApplyCodex(JsonElement r)
        {
            if (Str(r, "type") == "item.completed" && r.TryGetProperty("item", out var item))
            {
                switch (Str(item, "type") ?? Str(item, "item_type"))
                {
                    case "agent_message" or "assistant_message" when Str(item, "text") is { Length: > 0 } t: Add("assistant", t); _sawAssistant = true; return true;
                    case "command_execution" when Str(item, "command") is { } c: Add("tool", Truncate("Bash: " + c, 120)); return true;
                    case "error" when Str(item, "message") is { } m: Add("error", m); return true;
                }
                return false;
            }
            if (Str(r, "type") is "turn.failed" or "error")
            {
                var m = r.TryGetProperty("error", out var err) ? Str(err, "message") : Str(r, "message");
                Add("error", m ?? "the run failed");
                _resultError = true;
                return true;
            }
            if (r.TryGetProperty("msg", out var msg))
                switch (Str(msg, "type"))
                {
                    case "agent_message" when Str(msg, "message") is { Length: > 0 } t: Add("assistant", t); _sawAssistant = true; return true;
                    case "exec_command_begin" when msg.TryGetProperty("command", out var cmd):
                        Add("tool", Truncate("Bash: " + (cmd.ValueKind == JsonValueKind.Array ? string.Join(' ', cmd.EnumerateArray().Select(x => x.ToString())) : cmd.ToString()), 120));
                        return true;
                    case "error" when Str(msg, "message") is { } m: Add("error", m); _resultError = true; return true;
                }
            return false;
        }

        public void Finish(int exitCode, string stderrTail)
        {
            lock (_lock)
            {
                if (_status != "running") return;
                if (_killReason == "") _status = "cancelled";
                else if (_killReason is not null) { _status = "error"; Add("error", _killReason); }
                else if (_resultError) _status = "error";
                else if (exitCode != 0 || (tool == "claude" && !_sawResult))
                {
                    _status = "error";
                    Add("error", stderrTail.Length > 0 ? Tail(stderrTail, 1000) : $"{tool} exited with code {exitCode}");
                }
                else _status = "done";
                _endedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            Changed(force: true);
        }

        private void Add(string role, string text)
        {
            _items.Add(new(role, Clip(text)));
            // keep the prompt (item 0); drop the oldest of the rest past the caps
            while (_items.Count > 2 && (_items.Count > MaxItems || _items.Sum(i => i.Text.Length) > MaxTotalChars)) _items.RemoveAt(1);
        }

        /// <summary>Publishes the snapshot: at most ~4 per second while it streams, the final one always.</summary>
        public void Changed(bool force = false)
        {
            long wait;
            lock (_lock)
            {
                var now = Environment.TickCount64;
                wait = force ? 0 : 250 - (now - _lastPublish);
                if (wait > 0)
                {
                    if (_flushPending) return;
                    _flushPending = true;
                }
                else _lastPublish = now;
            }
            if (wait <= 0) { publish(Snapshot()); return; }
            _ = Task.Delay((int)wait).ContinueWith(_ =>
            {
                lock (_lock) { _flushPending = false; _lastPublish = Environment.TickCount64; }
                publish(Snapshot());
            });
        }
    }

    /// <summary>"Bash: git status": the tool and its most telling input, short.</summary>
    internal static string ToolSummary(string name, JsonElement input)
    {
        string? detail = null;
        if (input.ValueKind == JsonValueKind.Object)
            foreach (var key in new[] { "command", "file_path", "notebook_path", "pattern", "url", "query", "path", "description", "prompt" })
                if (Str(input, key) is { Length: > 0 } v) { detail = v; break; }
        var title = name.StartsWith("mcp__") ? name[5..].Replace("__", " › ") : name;
        return Truncate(detail is null ? title : title + ": " + Regex.Replace(detail, @"\s+", " ").Trim(), 120);
    }

    private static string Clip(string s) => Truncate(s, MaxItemChars);

    private static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        var cut = char.IsHighSurrogate(s[max - 1]) ? max - 1 : max;
        return s[..cut] + "…";
    }

    private static string Tail(string s, int max) => s.Length <= max ? s : "…" + s[^max..];

    private static string? Str(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
