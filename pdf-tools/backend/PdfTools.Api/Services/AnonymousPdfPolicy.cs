using System.Collections.Concurrent;

namespace PdfTools.Api.Services;

/// <summary>
/// Quota and abuse controls for signed-out use of the free PDF tools.
///
/// WHY THIS EXISTS
/// The free tool pages (/free-pdf-tools/merge and friends) rank for high-volume
/// queries but previously could not convert anything — they asked the visitor to
/// sign up first, which is backwards from how iLovePDF and Smallpdf acquire
/// users. Opening conversion to anonymous callers is the acquisition channel;
/// this class is what stops it becoming free unlimited infrastructure.
///
/// DEFENCE IN DEPTH — no single control is load-bearing:
///   1. Per-IP daily quota      — bounds how much one visitor can consume
///   2. File size cap           — bounds the work in a single call
///   3. Page count cap          — size alone does not bound work (a 200KB PDF
///                                can hold thousands of pages)
///   4. Per-IP concurrency      — stops one caller occupying the worker pool
///   5. Global anonymous ceiling— anonymous traffic can never starve paying users
///   6. Per-call timeout        — enforced by the caller, bounds pathological input
///
/// The per-IP controls depend on ForwardedHeaders being configured, otherwise
/// every visitor shares the proxy's IP and the quota is global by accident.
/// See Program.cs — that fix and this class must ship together.
///
/// STORAGE: in-memory and per-instance. On a single Container App replica this
/// is exact. Scaled out, each replica keeps its own counters, so effective
/// limits multiply by replica count — acceptable for a free tier (the caps are
/// deliberately conservative), but it is the reason the global ceiling exists
/// as a second bound. Moving to Redis is the upgrade path if abuse appears.
/// </summary>
public class AnonymousPdfPolicy
{
    /// <summary>Conversions allowed per client IP per rolling 24h.</summary>
    public const int DailyQuotaPerIp = 3;

    /// <summary>Anonymous upload ceiling. Signed-in users get 100MB.</summary>
    public const long MaxBytes = 10 * 1024 * 1024;

    /// <summary>
    /// Page ceiling. Bytes alone are a poor proxy for work: compressed or
    /// text-only PDFs can carry thousands of pages inside a small file.
    /// </summary>
    public const int MaxPages = 20;

    /// <summary>In-flight anonymous jobs allowed per IP.</summary>
    public const int MaxConcurrentPerIp = 2;

    /// <summary>
    /// In-flight anonymous jobs allowed across the whole instance. Paying users
    /// and org API keys never pass through here, so they cannot be starved by
    /// free traffic.
    /// </summary>
    public const int MaxConcurrentGlobal = 8;

    /// <summary>Wall-clock ceiling for a single anonymous conversion.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(30);

    private sealed class IpUsage
    {
        public int Count;
        public DateTimeOffset WindowStart;
        public int InFlight;
    }

    private readonly ConcurrentDictionary<string, IpUsage> _usage = new();
    private int _globalInFlight;
    private DateTimeOffset _lastSweep = DateTimeOffset.UtcNow;

    private readonly ILogger<AnonymousPdfPolicy> _logger;

    public AnonymousPdfPolicy(ILogger<AnonymousPdfPolicy> logger) => _logger = logger;

    public sealed record Decision(bool Allowed, string? Reason = null, int? RetryAfterSeconds = null, int Remaining = 0);

    /// <summary>
    /// Claim one conversion slot for this IP. On success the caller MUST call
    /// <see cref="Release"/> in a finally block, or concurrency slots leak and
    /// the tool wedges shut for that IP.
    /// </summary>
    public Decision TryAcquire(string clientIp)
    {
        if (string.IsNullOrWhiteSpace(clientIp)) clientIp = "unknown";

        SweepIfDue();

        // Global ceiling first: cheapest check, and the one protecting paid users.
        if (Volatile.Read(ref _globalInFlight) >= MaxConcurrentGlobal)
        {
            _logger.LogWarning("Anonymous PDF: global concurrency ceiling hit ({Max})", MaxConcurrentGlobal);
            return new Decision(false, "The free tools are busy right now. Try again in a moment.", 30);
        }

        var now = DateTimeOffset.UtcNow;
        var entry = _usage.GetOrAdd(clientIp, _ => new IpUsage { WindowStart = now });

        // Lock per entry rather than globally so one busy IP cannot serialise
        // every other caller.
        lock (entry)
        {
            // Rolling 24h window: reset once the window has fully elapsed.
            if (now - entry.WindowStart >= TimeSpan.FromHours(24))
            {
                entry.Count = 0;
                entry.WindowStart = now;
            }

            if (entry.InFlight >= MaxConcurrentPerIp)
                return new Decision(false, "You already have conversions running. Wait for those to finish.", 10);

            if (entry.Count >= DailyQuotaPerIp)
            {
                var resetsIn = (int)Math.Ceiling((entry.WindowStart.AddHours(24) - now).TotalSeconds);
                return new Decision(
                    false,
                    $"You've used your {DailyQuotaPerIp} free conversions for today. Create a free account for unlimited conversions.",
                    resetsIn);
            }

            entry.Count++;
            entry.InFlight++;
            Interlocked.Increment(ref _globalInFlight);
            return new Decision(true, Remaining: DailyQuotaPerIp - entry.Count);
        }
    }

    /// <summary>
    /// Release a slot claimed by <see cref="TryAcquire"/>.
    ///
    /// <paramref name="succeeded"/> false refunds the daily quota: a failed
    /// conversion consumed no useful work, and charging for our own errors is
    /// how a user's first impression becomes their last. Concurrency is always
    /// released regardless.
    /// </summary>
    public void Release(string clientIp, bool succeeded)
    {
        if (string.IsNullOrWhiteSpace(clientIp)) clientIp = "unknown";

        if (_usage.TryGetValue(clientIp, out var entry))
        {
            lock (entry)
            {
                if (entry.InFlight > 0) entry.InFlight--;
                if (!succeeded && entry.Count > 0) entry.Count--;
            }
        }

        // Floor at zero: a double Release must not drive the global counter
        // negative and hand out unlimited free slots.
        if (Volatile.Read(ref _globalInFlight) > 0)
            Interlocked.Decrement(ref _globalInFlight);
    }

    /// <summary>Remaining conversions today, for display before a request.</summary>
    public int RemainingFor(string clientIp)
    {
        if (string.IsNullOrWhiteSpace(clientIp)) clientIp = "unknown";
        if (!_usage.TryGetValue(clientIp, out var entry)) return DailyQuotaPerIp;

        lock (entry)
        {
            if (DateTimeOffset.UtcNow - entry.WindowStart >= TimeSpan.FromHours(24))
                return DailyQuotaPerIp;
            return Math.Max(0, DailyQuotaPerIp - entry.Count);
        }
    }

    /// <summary>
    /// Drop entries whose window has expired. Without this the dictionary grows
    /// unbounded with one entry per IP ever seen — a slow memory leak that only
    /// shows up once the tools are actually popular.
    /// </summary>
    private void SweepIfDue()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastSweep < TimeSpan.FromMinutes(30)) return;
        _lastSweep = now;

        foreach (var kv in _usage)
        {
            var e = kv.Value;
            lock (e)
            {
                if (e.InFlight == 0 && now - e.WindowStart >= TimeSpan.FromHours(24))
                    _usage.TryRemove(kv.Key, out _);
            }
        }
    }
}
