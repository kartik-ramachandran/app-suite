using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Colony.Api.Controllers;

/// <summary>
/// Reads the coding-agent sessions this machine already has on disk and reports
/// them as a flat thread list. The Colony page draws one crew member per thread.
///
/// STRICTLY READ-ONLY. Someone's real work is in these files: nothing here
/// opens a file for writing, and the transcript is only ever read in a bounded
/// head chunk. A session being written *right now* is a normal thing to trip
/// over, so a malformed record is skipped rather than throwing the pass away.
///
/// PRIVACY — read this before changing anything below.
///
/// The data this returns is NOT application data. It is whatever the person at
/// this keyboard has been working on: repository names, branch names, prompt
/// text and absolute paths, frequently belonging to their employer or clients.
/// It is not owned by the app, it is not the caller's to see by virtue of
/// having an account, and it must never be served to a remote caller.
///
/// [Authorize] alone is NOT sufficient — it admits *any* signed-in user, and
/// registration on this app is open. Three independent gates apply:
///
///   1. The request must arrive over loopback. A remote address is refused
///      before any file is touched.
///   2. The server must not look like a hosted deployment.
///   3. If Colony:OwnerUserId is configured, the caller must be that user.
///
/// Any one of these failing returns "unavailable" with no thread data at all.
/// </summary>
[Authorize]
[ApiController]
[Route("api/colony")]
public class ColonyController : ControllerBase
{
    private readonly ILogger<ColonyController> _logger;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;

    public ColonyController(
        ILogger<ColonyController> logger,
        IConfiguration config,
        IWebHostEnvironment env)
    {
        _logger = logger;
        _config = config;
        _env = env;
    }

    /// <summary>Only ever read this much of a transcript. They reach 30MB+.</summary>
    private const int HeadBytes = 192 * 1024;

    private static string Home =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Where the CLI keeps raw transcripts: ~/.claude/projects/&lt;dir&gt;/&lt;id&gt;.jsonl</summary>
    private static string CliProjects => Path.Combine(Home, ".claude", "projects");

    /// <summary>One file per live CLI process: {pid, sessionId, cwd, ...}.</summary>
    private static string CliLive => Path.Combine(Home, ".claude", "sessions");

    /// <summary>
    /// The desktop app's own record per thread. Windows keeps this under
    /// %APPDATA%; macOS uses ~/Library/Application Support.
    /// </summary>
    private static string DesktopSessions
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(appData, "Claude", "claude-code-sessions");
            }
            if (OperatingSystem.IsMacOS())
                return Path.Combine(Home, "Library", "Application Support", "Claude", "claude-code-sessions");

            return Path.Combine(Home, ".config", "Claude", "claude-code-sessions");
        }
    }

    public record ColonyThread(
        string Id,
        string Title,
        string Preview,
        string Project,
        // NOTE: the absolute path is deliberately NOT sent. The deck renders the
        // repo NAME only, and the full path is the most sensitive part of this
        // payload — it carries an employer's or client's directory structure.
        string GitBranch,
        string Model,
        long LastActivityAt,
        long SizeBytes,
        bool Running,
        bool HasError,
        string Source);

    public record ColonyResponse(
        bool Available,
        string? Reason,
        int ThreadCount,
        int ProjectCount,
        List<ColonyThread> Threads);

    /// <summary>
    /// Decide whether this caller is allowed to see the machine's sessions.
    /// Returns null when allowed, or the reason to report when not.
    ///
    /// Deliberately fails CLOSED: anything unexpected (no remote address, an
    /// unreadable claim) denies rather than allows.
    /// </summary>
    private string? DenyReason()
    {
        // 1. Loopback only. This is the gate that matters: it does not care how
        //    the caller authenticated, only that they are on this machine.
        var remote = HttpContext.Connection.RemoteIpAddress;
        if (remote is null)
            return "Could not determine the caller's address, so the colony stayed shut.";

        var local = HttpContext.Connection.LocalIpAddress;
        var isLoopback = System.Net.IPAddress.IsLoopback(remote)
                         // A request to the same host over its own LAN address
                         // is still this machine talking to itself.
                         || (local is not null && remote.Equals(local));

        if (!isLoopback)
        {
            _logger.LogWarning(
                "Colony refused a non-local request from {Remote}. This endpoint reads the "
                + "host's own working files and is never served remotely.", remote);
            return "The colony only answers requests from the machine it runs on.";
        }

        // 2. Do not serve this from anything that looks hosted. Belt and braces
        //    for the case where a container is somehow reached over loopback.
        if (!_env.IsDevelopment()
            && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME")))
        {
            return "The colony is a local-desktop feature and is disabled on hosted deployments.";
        }

        // 3. Optional single-owner pin. Set Colony:OwnerUserId to your own user
        //    id and no other local account can read the sessions either.
        var owner = _config["Colony:OwnerUserId"];
        if (!string.IsNullOrWhiteSpace(owner))
        {
            var caller = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                         ?? User.FindFirst("sub")?.Value
                         ?? User.FindFirst("userId")?.Value;

            if (string.IsNullOrEmpty(caller) || caller != owner)
                return "The colony is pinned to a single user on this machine.";
        }

        return null;
    }

    [HttpGet("threads")]
    public ActionResult<ColonyResponse> GetThreads()
    {
        // Check the caller BEFORE touching the filesystem, so a refused request
        // never even reads a directory listing.
        var denied = DenyReason();
        if (denied is not null)
            return Ok(new ColonyResponse(false, denied, 0, 0, new List<ColonyThread>()));

        var hasCli = Directory.Exists(CliProjects);
        var hasDesktop = Directory.Exists(DesktopSessions);

        if (!hasCli && !hasDesktop)
        {
            return Ok(new ColonyResponse(
                false,
                "No Claude Code sessions found on this machine. The colony reads "
                    + "~/.claude/projects and the desktop app's session records, so it only "
                    + "has anything to show when the API runs on your own computer.",
                0, 0, new List<ColonyThread>()));
        }

        try
        {
            var live = ScanLiveSessions();
            var desktop = ScanDesktopRecords();
            var threads = ScanTranscripts(desktop, live);

            threads.Sort((a, b) => b.LastActivityAt.CompareTo(a.LastActivityAt));

            var projects = threads.Select(t => t.Project)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            return Ok(new ColonyResponse(true, null, threads.Count, projects, threads));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Colony scan failed");
            return Ok(new ColonyResponse(false, "Could not read the session files: " + ex.Message,
                0, 0, new List<ColonyThread>()));
        }
    }

    /// <summary>
    /// Session ids with a CLI process actually alive. The registry keeps files
    /// for processes that have exited, so every pid is probed before it counts.
    /// </summary>
    private HashSet<string> ScanLiveSessions()
    {
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(CliLive)) return live;

        foreach (var file in SafeFiles(CliLive, "*.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(file));
                var root = doc.RootElement;
                if (!root.TryGetProperty("sessionId", out var sid)) continue;
                if (!root.TryGetProperty("pid", out var pidEl) || !pidEl.TryGetInt32(out var pid)) continue;

                try
                {
                    // Throws if there is no such process — the only portable
                    // "does this pid exist" check .NET offers.
                    using var _ = System.Diagnostics.Process.GetProcessById(pid);
                    var id = sid.GetString();
                    if (!string.IsNullOrEmpty(id)) live.Add(id);
                }
                catch (ArgumentException) { /* process is gone */ }
            }
            catch { /* mid-write or malformed — skip this one */ }
        }
        return live;
    }

    private sealed class DesktopRecord
    {
        public string SessionId = "";
        public string CliSessionId = "";
        public string Title = "";
        public string Cwd = "";
        public string Model = "";
        public long LastActivityAt;
        public bool HasError;
    }

    /// <summary>Every thread the desktop app has a record for, keyed by CLI session id.</summary>
    private Dictionary<string, DesktopRecord> ScanDesktopRecords()
    {
        var byCli = new Dictionary<string, DesktopRecord>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(DesktopSessions)) return byCli;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(DesktopSessions, "local_*.json", SearchOption.AllDirectories);
        }
        catch { return byCli; }

        foreach (var file in files)
        {
            try
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(file));
                var r = doc.RootElement;

                var rec = new DesktopRecord
                {
                    SessionId = Str(r, "sessionId"),
                    CliSessionId = Str(r, "cliSessionId"),
                    Title = Str(r, "title"),
                    Cwd = Str(r, "cwd"),
                    Model = Str(r, "model"),
                    LastActivityAt = Num(r, "lastActivityAt"),
                    HasError = r.TryGetProperty("error", out var e)
                               && e.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined),
                };

                if (string.IsNullOrEmpty(rec.CliSessionId)) continue;

                // Resuming an open thread writes a second, untitled record for
                // the same transcript. Keep whichever one actually has a title.
                if (byCli.TryGetValue(rec.CliSessionId, out var existing)
                    && !string.IsNullOrEmpty(existing.Title)
                    && string.IsNullOrEmpty(rec.Title))
                {
                    continue;
                }
                byCli[rec.CliSessionId] = rec;
            }
            catch { /* a session mid-write — skip this pass */ }
        }
        return byCli;
    }

    private List<ColonyThread> ScanTranscripts(
        Dictionary<string, DesktopRecord> desktop,
        HashSet<string> live)
    {
        var threads = new List<ColonyThread>();
        if (!Directory.Exists(CliProjects)) return threads;

        IEnumerable<string> projectDirs;
        try { projectDirs = Directory.EnumerateDirectories(CliProjects); }
        catch { return threads; }

        foreach (var dir in projectDirs)
        {
            foreach (var file in SafeFiles(dir, "*.jsonl"))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                FileInfo info;
                try { info = new FileInfo(file); }
                catch { continue; }

                var meta = ReadTranscriptHead(file);
                desktop.TryGetValue(id, out var rec);

                var cwd = !string.IsNullOrEmpty(rec?.Cwd) ? rec!.Cwd
                        : !string.IsNullOrEmpty(meta.Cwd) ? meta.Cwd
                        : DecodeProjectDir(Path.GetFileName(dir));

                var title = FirstNonEmpty(
                    rec?.Title, meta.CustomTitle, meta.AiTitle, meta.Summary, meta.FirstPrompt)
                    ?? "Untitled thread";

                var lastActivity = rec?.LastActivityAt > 0
                    ? rec.LastActivityAt
                    : new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();

                threads.Add(new ColonyThread(
                    Id: id,
                    Title: Trim(title, 90),
                    Preview: Trim(meta.FirstPrompt, 240),
                    Project: SafeName(cwd),
                    GitBranch: meta.GitBranch,
                    Model: rec?.Model ?? "",
                    LastActivityAt: lastActivity,
                    SizeBytes: info.Length,
                    Running: live.Contains(id),
                    HasError: rec?.HasError ?? false,
                    Source: rec is null ? "cli" : "desktop"));
            }
        }
        return threads;
    }

    private readonly record struct TranscriptMeta(
        string CustomTitle, string AiTitle, string Summary,
        string FirstPrompt, string Cwd, string GitBranch);

    /// <summary>
    /// Pull whatever a transcript knows about itself from its first chunk.
    /// Reading whole files is not an option — these routinely pass 30MB, and a
    /// trailing partial line would otherwise break the JSON parse.
    /// </summary>
    private TranscriptMeta ReadTranscriptHead(string path)
    {
        string customTitle = "", aiTitle = "", summary = "", firstPrompt = "", cwd = "", branch = "";

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[Math.Min(HeadBytes, fs.Length)];
            var read = fs.Read(buffer, 0, buffer.Length);
            var text = System.Text.Encoding.UTF8.GetString(buffer, 0, read);

            // Drop a trailing partial line so JsonDocument never sees half a record.
            var lastNewline = text.LastIndexOf('\n');
            if (lastNewline > 0) text = text[..lastNewline];

            foreach (var line in text.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var r = doc.RootElement;
                    if (r.ValueKind != JsonValueKind.Object) continue;

                    if (customTitle.Length == 0) customTitle = Str(r, "customTitle");
                    if (aiTitle.Length == 0) aiTitle = Str(r, "aiTitle");
                    if (cwd.Length == 0) cwd = Str(r, "cwd");

                    var gb = Str(r, "gitBranch");
                    if (branch.Length == 0 && gb.Length > 0 && gb != "HEAD") branch = gb;

                    if (summary.Length == 0 && Str(r, "type") == "summary")
                        summary = Str(r, "summary");

                    if (firstPrompt.Length == 0 && Str(r, "type") == "user"
                        && r.TryGetProperty("message", out var msg)
                        && msg.ValueKind == JsonValueKind.Object)
                    {
                        var text2 = CleanPrompt(FirstText(msg));
                        // Skip the CLI's own <command>/<system> wrappers.
                        if (text2.Length > 0 && !text2.StartsWith('<')) firstPrompt = text2;
                    }

                    if (customTitle.Length > 0 && cwd.Length > 0 && firstPrompt.Length > 0) break;
                }
                catch { /* one bad record does not spoil the file */ }
            }
        }
        catch { /* unreadable — the caller still gets a thread, just a plainer one */ }

        return new TranscriptMeta(customTitle, aiTitle, summary, firstPrompt, cwd, branch);
    }

    private static string FirstText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) return "";
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";

        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String) return part.GetString() ?? "";
            if (part.ValueKind == JsonValueKind.Object
                && Str(part, "type") == "text")
            {
                var t = Str(part, "text");
                if (t.Length > 0) return t;
            }
        }
        return "";
    }

    /// <summary>Strip the XML-ish noise the CLI wraps around prompts.</summary>
    private static string CleanPrompt(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = System.Text.RegularExpressions.Regex.Replace(
            s, @"<([a-z][\w-]*)(?:\s[^>]*)?>[\s\S]*?</\1>", " ",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
    }

    /// <summary>Reverse of the `-Users-you-Some-Dir` encoding used for folder names.</summary>
    private static string DecodeProjectDir(string name) =>
        name.StartsWith('-') ? "/" + name[1..].Replace('-', '/') : name;

    private static string SafeName(string path)
    {
        if (string.IsNullOrEmpty(path)) return "unknown";
        var trimmed = path.TrimEnd('/', '\\');
        var idx = trimmed.LastIndexOfAny(new[] { '/', '\\' });
        var name = idx >= 0 ? trimmed[(idx + 1)..] : trimmed;
        return name.Length > 0 ? name : "unknown";
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern); }
        catch { return Array.Empty<string>(); }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static long Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetDouble(out var d) ? (long)d : 0;

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string Trim(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";
}
