using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhoneBox.Agent;

/// <param name="Tool">The tool Claude Code wants to use (Bash, Edit, Write, WebFetch, an MCP tool…).</param>
/// <param name="Detail">What it wants to do: the command, the file path, the URL…</param>
/// <param name="Kind">"permission" (Allow / Deny) or "question" (Claude is asking something; it is answered in the terminal).</param>
/// <param name="Diff">For file edits: what changes, for the phone to show as a diff.</param>
public sealed record Approval(string Id, string Source, string Session, string Project, string Tool, string Detail,
    string? Description, long CreatedAt, long ExpiresAt, string Kind = "permission",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ApprovalDiff? Diff = null)
{
    /// <summary>"allow-session" is offered for permission prompts only.</summary>
    public bool CanAllowSession => Kind == "permission";
}

public sealed record ApprovalDiff(string Path, IReadOnlyList<DiffHunk> Hunks);
public sealed record DiffHunk(string Old, string New);

/// <summary>
/// Permission prompts waiting for an answer from the phone. A Claude Code hook posts the request and waits; the
/// phone shows Allow / Deny and posts the decision back. If no phone is around, or nobody answers in time, the request
/// ends without a decision and Claude Code asks in the terminal as usual.
/// </summary>
public sealed class ApprovalStore(PushStore events, ILogger<ApprovalStore> log)
{
    private readonly TimeProvider _clock = TimeProvider.System;
    internal ApprovalStore(PushStore events, ILogger<ApprovalStore> log, TimeProvider clock) : this(events, log) => _clock = clock;

    private sealed record Pending(Approval Approval, TaskCompletionSource<string?> Decision);
    private readonly ConcurrentDictionary<string, Pending> _pending = new();

    /// <summary>When the phone last polled the agent; approvals are only offered to a phone that is actually there.</summary>
    public static long PhoneLastSeen;

    public static bool PhoneConnected => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Interlocked.Read(ref PhoneLastSeen) < 5000;

    /// <summary>The requests the phone can still answer; one past its expiry is left out even before it is cleaned up.</summary>
    public IReadOnlyList<Approval> Snapshot()
    {
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        return _pending.Values.Select(p => p.Approval).Where(a => a.ExpiresAt > now).OrderBy(a => a.CreatedAt).ToList();
    }

    /// <summary>Shows the request on the phone and waits for "allow" / "deny"; null = no answer (fall back to the terminal).</summary>
    public async Task<string?> AskAsync(Approval a, CancellationToken aborted)
    {
        if (!PhoneConnected || a.ExpiresAt <= _clock.GetUtcNow().ToUnixTimeMilliseconds()) return null;
        var pending = new Pending(a, new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously));
        _pending[a.Id] = pending;
        events.Emit("approval", JsonSerializer.SerializeToElement(new { id = a.Id }));
        log.LogInformation("approval {Id}: {Tool} in {Project} sent to the phone", a.Id, a.Tool, a.Project);
        try
        {
            // exactly the time left: Decide refuses an answer from the expiry on, so waiting longer only delays the terminal
            var timeout = TimeSpan.FromMilliseconds(Math.Max(0, a.ExpiresAt - _clock.GetUtcNow().ToUnixTimeMilliseconds()));
            // The hook process goes away when the user answers in the terminal; that cancels the request here.
            await Task.WhenAny(pending.Decision.Task, Task.Delay(timeout, aborted));
            // The decision task is the one outcome: a timeout or cancel closes it with null, so a Decide that loses
            // the race reports false; a Decide that won before this line is returned even though the wait ended.
            pending.Decision.TrySetResult(null);
            return await pending.Decision.Task;
        }
        catch (OperationCanceledException) { pending.Decision.TrySetResult(null); return null; }
        finally
        {
            _pending.TryRemove(a.Id, out _);
            events.Emit("approval-done", JsonSerializer.SerializeToElement(new { id = a.Id }));
        }
    }

    public bool Decide(string id, string decision)
    {
        if (decision is not ("allow" or "deny" or "skip" or "allow-session") || !_pending.TryGetValue(id, out var p)) return false;
        if (decision == "allow-session" && !p.Approval.CanAllowSession) return false;
        if (p.Approval.ExpiresAt <= _clock.GetUtcNow().ToUnixTimeMilliseconds()) return false;
        // accepted only if it wins against the timeout and the cancel (see AskAsync), and only then logged as such
        if (!p.Decision.TrySetResult(decision)) return false;
        log.LogInformation("approval {Id}: {Decision} from the phone", id, decision);
        return true;
    }

    // ---------------------------------------------------------------- "allow for this session"

    private static readonly TimeSpan SessionAllowFor = TimeSpan.FromHours(8);
    private readonly ConcurrentDictionary<(string Session, string Tool, string? Rule), long> _sessionAllows = new();

    /// <summary>Remembers (in memory, 8 h) that the user allowed this tool (and rule content) for the rest of the Claude session.</summary>
    public void AllowForSession(string session, string tool, string? rule)
    {
        if (string.IsNullOrEmpty(session)) return; // no session id: nothing to scope the rule to
        var now = _clock.GetUtcNow();
        foreach (var (k, until) in _sessionAllows)
            if (until <= now.ToUnixTimeMilliseconds()) _sessionAllows.TryRemove(k, out _);
        _sessionAllows[(session, tool, rule)] = (now + SessionAllowFor).ToUnixTimeMilliseconds();
    }

    /// <summary>True when a request matches an "allow for this session" that has not expired.</summary>
    public bool AllowedForSession(string session, string tool, string? rule) =>
        !string.IsNullOrEmpty(session) && _sessionAllows.TryGetValue((session, tool, rule), out var until)
        && until > _clock.GetUtcNow().ToUnixTimeMilliseconds();

    public void LogSessionAllow(string session, string tool) =>
        log.LogInformation("{Tool} in session {Session} allowed: allowed for this session from the phone", tool, session);

    public static string NewId() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
}
