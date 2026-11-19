using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

public sealed record ClaudeProject(string Cwd, string Name, long LastUsed);

/// <summary>
/// The Claude Code project folders the phone may start a new session in: the folders of the running sessions plus
/// every ~/.claude/projects/*/ folder, whose real path is the "cwd" of a line in its most recent transcript (the
/// folder name is a lossy encoding of it). Only folders that still exist; the 20 most recently used.
/// </summary>
public sealed class ClaudeProjects(SessionMonitor sessions)
{
    public const int Max = 20;
    internal string ProjectsDir { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
    // a transcript's cwd never changes: remembered by file path
    private readonly ConcurrentDictionary<string, string> _cwdByFile = new();
    private readonly object _lock = new();
    private IReadOnlyList<ClaudeProject>? _cached;
    private long _cachedAt;

    public IReadOnlyList<ClaudeProject> List()
    {
        lock (_lock)
        {
            if (_cached is not null && Environment.TickCount64 - _cachedAt < 15_000) return _cached;
            _cached = Scan(ProjectsDir, sessions.Snapshot(), Directory.Exists, _cwdByFile);
            _cachedAt = Environment.TickCount64;
            return _cached;
        }
    }

    /// <summary>True only for a folder the projects list offers, compared exactly: never an arbitrary path.</summary>
    public bool IsAllowed(string? cwd) => !string.IsNullOrEmpty(cwd) && List().Any(p => p.Cwd == cwd);

    internal static IReadOnlyList<ClaudeProject> Scan(string projectsDir, IEnumerable<AgentSession> running, Func<string, bool> dirExists,
        ConcurrentDictionary<string, string>? cwdCache = null)
    {
        var lastUsed = new Dictionary<string, long>(StringComparer.Ordinal);
        void Add(string cwd, long at)
        {
            cwd = cwd.TrimEnd('/', '\\') is { Length: > 0 } t ? t : cwd;
            if (cwd.Length == 0) return;
            lastUsed[cwd] = Math.Max(at, lastUsed.GetValueOrDefault(cwd));
        }

        foreach (var s in running)
            if (s.Tool == "claude" && !string.IsNullOrEmpty(s.Cwd) && !SessionMonitor.IsCodexBarProbe(s.Cwd))
                Add(s.Cwd, Math.Max(s.Since, s.StartedAt));

        if (Directory.Exists(projectsDir))
            foreach (var dir in new DirectoryInfo(projectsDir).EnumerateDirectories())
            {
                try
                {
                    var latest = dir.EnumerateFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
                    if (latest is null) continue;
                    var cwd = cwdCache is not null && cwdCache.TryGetValue(latest.FullName, out var c) ? c : ReadCwd(latest.FullName);
                    if (cwd is null) continue;
                    cwdCache?.TryAdd(latest.FullName, cwd);
                    if (SessionMonitor.IsCodexBarProbe(cwd)) continue;
                    Add(cwd, new DateTimeOffset(latest.LastWriteTimeUtc).ToUnixTimeMilliseconds());
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

        return lastUsed.Where(kv => !IsTemporary(kv.Key) && dirExists(kv.Key))
            .OrderByDescending(kv => kv.Value)
            .Take(Max)
            .Select(kv => new ClaudeProject(kv.Key, Path.GetFileName(kv.Key) is { Length: > 0 } n ? n : kv.Key, kv.Value))
            .ToList();
    }

    /// <summary>Throwaway folders (mktemp's tmp.*, /tmp) are not projects anyone wants to start a session in.</summary>
    internal static bool IsTemporary(string cwd)
    {
        var p = cwd.Replace('\\', '/');
        return p.StartsWith("/tmp/", StringComparison.Ordinal) || p.StartsWith("/private/tmp/", StringComparison.Ordinal)
            || Path.GetFileName(p).StartsWith("tmp.", StringComparison.Ordinal); // mktemp's folders
    }

    /// <summary>The top-level "cwd" of the first transcript line that has one (the first lines often do not).</summary>
    internal static string? ReadCwd(string path, int maxBytes = 4 * 1024 * 1024)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        long read = 0;
        while (read < maxBytes && reader.ReadLine() is { } line)
        {
            read += line.Length + 1;
            if (!line.Contains("\"cwd\"", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("cwd", out var c)
                    && c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 } cwd) return cwd;
            }
            catch (JsonException) { }
        }
        return null;
    }
}
