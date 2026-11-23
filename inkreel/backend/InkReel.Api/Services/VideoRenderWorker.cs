using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using InkReel.Api.Data;

namespace InkReel.Api.Services;

/// <summary>A queued render: the request plus who asked for it.</summary>
public record VideoWorkItem(string JobId, string UserId, VideoRequest Request);

/// <summary>Single in-process queue. Bounded so a burst can't pin memory.</summary>
public class VideoQueue
{
    private readonly Channel<VideoWorkItem> _channel =
        Channel.CreateBounded<VideoWorkItem>(new BoundedChannelOptions(50) { FullMode = BoundedChannelFullMode.Wait });

    public ValueTask EnqueueAsync(VideoWorkItem item, CancellationToken ct = default) => _channel.Writer.WriteAsync(item, ct);
    public IAsyncEnumerable<VideoWorkItem> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>
/// Video jobs persisted in a raw-SQL table (same pattern as every other module
/// table here) so the list survives a restart. The MP4 lives on disk under
/// Video:OutputDir/{jobId}/video.mp4.
/// </summary>
public class VideoJobStore
{
    private readonly AppDbContext _db;
    private static bool _ensured;

    public VideoJobStore(AppDbContext db) { _db = db; }

    public async Task EnsureAsync()
    {
        if (_ensured) return;
        var sqlite = _db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ?? false;
        _ = sqlite; // Id is a GUID string — no provider-specific PK needed.
        await _db.Database.ExecuteSqlRawAsync(@"CREATE TABLE IF NOT EXISTS ""VideoJobs"" (
            ""Id"" TEXT PRIMARY KEY, ""UserId"" TEXT NOT NULL, ""Title"" TEXT, ""Status"" TEXT NOT NULL,
            ""Error"" TEXT, ""SceneCount"" INTEGER NOT NULL DEFAULT 0, ""ScenesDone"" INTEGER NOT NULL DEFAULT 0,
            ""DurationSeconds"" REAL NOT NULL DEFAULT 0, ""HasAudio"" INTEGER NOT NULL DEFAULT 0,
            ""ScriptJson"" TEXT, ""CreatedAt"" TEXT, ""UpdatedAt"" TEXT)");
        await _db.Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_VideoJobs_UserId"" ON ""VideoJobs"" (""UserId"", ""CreatedAt"")");
        _ensured = true;
    }

    public async Task CreateAsync(string id, string userId, string title)
    {
        await EnsureAsync();
        var now = DateTime.UtcNow.ToString("o");
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""VideoJobs"" (""Id"", ""UserId"", ""Title"", ""Status"", ""CreatedAt"", ""UpdatedAt"")
            VALUES ({id}, {userId}, {title}, {"queued"}, {now}, {now})");
    }

    public async Task UpdateAsync(string id, string status, int? sceneCount = null, int? scenesDone = null,
        double? duration = null, bool? hasAudio = null, string? error = null, string? scriptJson = null)
    {
        await EnsureAsync();
        var now = DateTime.UtcNow.ToString("o");
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE ""VideoJobs"" SET
                ""Status""={status},
                ""SceneCount""=COALESCE({sceneCount}, ""SceneCount""),
                ""ScenesDone""=COALESCE({scenesDone}, ""ScenesDone""),
                ""DurationSeconds""=COALESCE({duration}, ""DurationSeconds""),
                ""HasAudio""=COALESCE({(hasAudio.HasValue ? (hasAudio.Value ? 1 : 0) : (int?)null)}, ""HasAudio""),
                ""Error""=COALESCE({error}, ""Error""),
                ""ScriptJson""=COALESCE({scriptJson}, ""ScriptJson""),
                ""UpdatedAt""={now}
            WHERE ""Id""={id}");
    }

    private sealed class Row
    {
        public string Id { get; set; } = "";
        public string? Title { get; set; }
        public string Status { get; set; } = "";
        public string? Error { get; set; }
        public int SceneCount { get; set; }
        public int ScenesDone { get; set; }
        public double DurationSeconds { get; set; }
        public int HasAudio { get; set; }
        public string? CreatedAt { get; set; }
    }

    private static VideoJobStatus Map(Row r) => new()
    {
        Id = r.Id, Title = r.Title ?? "", Status = r.Status, Error = r.Error,
        SceneCount = r.SceneCount, ScenesDone = r.ScenesDone,
        DurationSeconds = r.DurationSeconds, HasAudio = r.HasAudio == 1, CreatedAt = r.CreatedAt,
    };

    public async Task<List<VideoJobStatus>> ListAsync(string userId)
    {
        await EnsureAsync();
        var rows = await _db.Database.SqlQuery<Row>($@"
            SELECT ""Id"", ""Title"", ""Status"", ""Error"", ""SceneCount"", ""ScenesDone"", ""DurationSeconds"", ""HasAudio"", ""CreatedAt""
            FROM ""VideoJobs"" WHERE ""UserId""={userId} ORDER BY ""CreatedAt"" DESC LIMIT 50").ToListAsync();
        return rows.Select(Map).ToList();
    }

    public async Task<VideoJobStatus?> GetAsync(string id, string userId)
    {
        await EnsureAsync();
        var row = (await _db.Database.SqlQuery<Row>($@"
            SELECT ""Id"", ""Title"", ""Status"", ""Error"", ""SceneCount"", ""ScenesDone"", ""DurationSeconds"", ""HasAudio"", ""CreatedAt""
            FROM ""VideoJobs"" WHERE ""Id""={id} AND ""UserId""={userId}").ToListAsync()).FirstOrDefault();
        return row == null ? null : Map(row);
    }

    public async Task<string?> GetScriptAsync(string id, string userId)
    {
        await EnsureAsync();
        return (await _db.Database.SqlQuery<string?>($@"
            SELECT ""ScriptJson"" AS ""Value"" FROM ""VideoJobs"" WHERE ""Id""={id} AND ""UserId""={userId}").ToListAsync()).FirstOrDefault();
    }

    /// <summary>Jobs started by this user in the last 24h (cost cap).</summary>
    public async Task<int> CountRecentAsync(string userId)
    {
        await EnsureAsync();
        var since = DateTime.UtcNow.AddHours(-24).ToString("o");
        return (await _db.Database.SqlQuery<int>($@"
            SELECT COUNT(*) AS ""Value"" FROM ""VideoJobs"" WHERE ""UserId""={userId} AND ""CreatedAt"" >= {since}").ToListAsync()).FirstOrDefault();
    }

    /// <summary>Jobs for this user still in flight (one at a time per user).</summary>
    public async Task<int> CountActiveAsync(string userId)
    {
        await EnsureAsync();
        return (await _db.Database.SqlQuery<int>($@"
            SELECT COUNT(*) AS ""Value"" FROM ""VideoJobs""
            WHERE ""UserId""={userId} AND ""Status"" NOT IN ('done', 'failed')").ToListAsync()).FirstOrDefault();
    }

    public async Task DeleteAsync(string id, string userId)
    {
        await EnsureAsync();
        await _db.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM ""VideoJobs"" WHERE ""Id""={id} AND ""UserId""={userId}");
    }

    /// <summary>A crash mid-render leaves rows 'rendering' forever; fail them on boot.</summary>
    public async Task FailOrphansAsync()
    {
        await EnsureAsync();
        await _db.Database.ExecuteSqlRawAsync(@"
            UPDATE ""VideoJobs"" SET ""Status""='failed', ""Error""='The server restarted while this video was rendering — please create it again.'
            WHERE ""Status"" NOT IN ('done', 'failed')");
    }
}

/// <summary>
/// The renderer: script → per-scene narration (TTS) → per-scene slide PNG →
/// per-scene MP4 segment timed to its narration → concat into one MP4.
///
/// ffmpeg is invoked with an argument LIST (never a shell string), so nothing
/// a user typed can reach a command line. Without Azure Speech configured the
/// video still renders: each slide holds for its narration's estimated reading
/// time over a silent track, and the job reports HasAudio=false.
/// </summary>
public class VideoRenderWorker : BackgroundService
{
    private readonly VideoQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _cfg;
    private readonly ILogger<VideoRenderWorker> _logger;

    public VideoRenderWorker(VideoQueue queue, IServiceScopeFactory scopes, IConfiguration cfg, ILogger<VideoRenderWorker> logger)
    { _queue = queue; _scopes = scopes; _cfg = cfg; _logger = logger; }

    public static string OutputRoot(IConfiguration cfg, IWebHostEnvironment env)
    {
        var dir = cfg["Video:OutputDir"];
        return string.IsNullOrWhiteSpace(dir) ? Path.Combine(env.ContentRootPath, "videos") : dir;
    }

    private string Ffmpeg => _cfg["Video:FfmpegPath"] is { Length: > 0 } p ? p : "ffmpeg";
    private string Ffprobe => _cfg["Video:FfprobePath"] is { Length: > 0 } p ? p : "ffprobe";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var scope = _scopes.CreateScope())
        {
            try { await scope.ServiceProvider.GetRequiredService<VideoJobStore>().FailOrphansAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not fail orphaned video jobs"); }
        }

        await foreach (var item in _queue.ReadAllAsync(stoppingToken))
        {
            try { await RenderAsync(item, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Video job {Job} failed", item.JobId);
                using var scope = _scopes.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<VideoJobStore>();
                await store.UpdateAsync(item.JobId, "failed", error: Friendly(ex));
            }
        }
    }

    private static string Friendly(Exception ex) => ex switch
    {
        InvalidOperationException ioe => ioe.Message,
        System.ComponentModel.Win32Exception => "ffmpeg is not installed on the server (or Video:FfmpegPath is wrong).",
        _ => "The video could not be rendered — please try again.",
    };

    private async Task RenderAsync(VideoWorkItem item, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var store = sp.GetRequiredService<VideoJobStore>();
        var script = sp.GetRequiredService<VideoScriptService>();
        var speech = sp.GetRequiredService<SpeechService>();
        var slides = sp.GetRequiredService<SlideRenderService>();
        var env = sp.GetRequiredService<IWebHostEnvironment>();

        var req = item.Request;
        var work = Path.Combine(OutputRoot(_cfg, env), item.JobId);
        Directory.CreateDirectory(work);

        // 1) Script
        await store.UpdateAsync(item.JobId, "scripting");
        var scenes = await script.WriteScriptAsync(req, ct);
        await store.UpdateAsync(item.JobId, "narrating", sceneCount: scenes.Count, scenesDone: 0,
            scriptJson: JsonSerializer.Serialize(scenes, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var ttsConfigured = speech.IsAvailable;
        var anySilent = false;
        var segments = new List<string>();
        double total = 0;

        // 2) Title card (2.5s, silent)
        var titlePng = Path.Combine(work, "title.png");
        await File.WriteAllBytesAsync(titlePng, slides.RenderTitleCard(req), ct);
        var titleSeg = Path.Combine(work, "seg_000.mp4");
        await RunAsync(Ffmpeg, SegmentArgs(titlePng, null, 2.5, titleSeg), ct);
        segments.Add(titleSeg);
        total += 2.5;

        // 3) Scenes
        for (var i = 0; i < scenes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var scene = scenes[i];

            string? audioPath = null;
            double duration;
            if (ttsConfigured)
            {
                var mp3 = await speech.SynthesiseAsync(scene.Narration, req.VoiceId, req.NarrationStyle, ct);
                if (mp3 != null)
                {
                    audioPath = Path.Combine(work, $"scene_{i + 1:000}.mp3");
                    await File.WriteAllBytesAsync(audioPath, mp3, ct);
                }
            }

            if (audioPath != null)
                duration = await ProbeDurationAsync(audioPath, ct) + 0.6; // breath between scenes
            else
            {
                // Silent fallback: hold for estimated reading time.
                var words = scene.Narration.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                duration = Math.Clamp(words / 2.5, 4, 30);
                anySilent = true;
            }

            await store.UpdateAsync(item.JobId, "rendering", scenesDone: i);

            var png = Path.Combine(work, $"scene_{i + 1:000}.png");
            await File.WriteAllBytesAsync(png, slides.RenderScene(req, scene, i, scenes.Count), ct);

            var seg = Path.Combine(work, $"seg_{i + 1:000}.mp4");
            await RunAsync(Ffmpeg, SegmentArgs(png, audioPath, duration, seg), ct);
            segments.Add(seg);
            total += duration;

            await store.UpdateAsync(item.JobId, "rendering", scenesDone: i + 1);
        }

        // 4) Concatenate (all segments share codec params, so stream copy is safe)
        await store.UpdateAsync(item.JobId, "assembling");
        var list = Path.Combine(work, "segments.txt");
        await File.WriteAllLinesAsync(list, segments.Select(s => $"file '{Path.GetFileName(s)}'"), ct);
        var output = Path.Combine(work, "video.mp4");
        await RunAsync(Ffmpeg, new[]
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "concat", "-safe", "0", "-i", list,
            "-c", "copy", "-movflags", "+faststart", output,
        }, ct, workingDir: work);

        // 5) Clean intermediates; keep the MP4 and slide PNGs (thumbnails).
        foreach (var f in Directory.GetFiles(work))
        {
            var name = Path.GetFileName(f);
            if (name == "video.mp4" || name.EndsWith(".png")) continue;
            try { File.Delete(f); } catch { /* best effort */ }
        }

        // HasAudio = every scene is narrated. A partial TTS failure reports false
        // so the UI can say "some scenes are silent" instead of implying full narration.
        await store.UpdateAsync(item.JobId, "done", scenesDone: scenes.Count, duration: Math.Round(total, 1), hasAudio: ttsConfigured && !anySilent);
        _logger.LogInformation("Video job {Job} done: {Scenes} scenes, {Seconds}s", item.JobId, scenes.Count, Math.Round(total));
    }

    /// <summary>One still slide held for `duration` seconds, with a short fade in/out.</summary>
    private static string[] SegmentArgs(string png, string? audio, double duration, string output)
    {
        var d = duration.ToString("0.###", CultureInfo.InvariantCulture);
        var fadeOut = Math.Max(0, duration - 0.35).ToString("0.###", CultureInfo.InvariantCulture);
        var vf = $"scale=1920:1080,format=yuv420p,fade=t=in:st=0:d=0.35,fade=t=out:st={fadeOut}:d=0.35";

        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-loop", "1", "-framerate", "30", "-i", png };
        if (audio != null) args.AddRange(new[] { "-i", audio });
        else args.AddRange(new[] { "-f", "lavfi", "-i", "anullsrc=channel_layout=mono:sample_rate=24000" });

        args.AddRange(new[]
        {
            "-t", d, "-vf", vf,
            "-c:v", "libx264", "-preset", "veryfast", "-tune", "stillimage", "-r", "30",
            "-c:a", "aac", "-b:a", "128k", "-ar", "24000", "-ac", "1",
            "-af", "apad", // pad short narration to the segment length
            "-shortest", output,
        });
        return args.ToArray();
    }

    private async Task<double> ProbeDurationAsync(string file, CancellationToken ct)
    {
        var output = await RunAsync(Ffprobe, new[]
        {
            "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", file,
        }, ct);
        return double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0 ? d : 5;
    }

    private static async Task<string> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct, string? workingDir = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (workingDir != null) psi.WorkingDirectory = workingDir;
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}.");
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
        {
            var err = await stderr;
            throw new Exception($"{Path.GetFileName(exe)} exited {proc.ExitCode}: {(err.Length > 400 ? err[..400] : err)}");
        }
        return await stdout;
    }
}
