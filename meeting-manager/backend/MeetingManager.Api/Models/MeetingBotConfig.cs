namespace MeetingManager.Api.Models;

/// <summary>
/// How the notetaker behaves for a person, a team, or the whole site.
///
/// Settings resolve most-specific-first — your own, then your team's, then the
/// site default — the same way AI provider settings already work here. So an
/// individual can change how their own meetings are handled without affecting
/// anyone, a team admin can set a policy for everybody who has not, and the site
/// has a sensible default underneath both.
///
/// There is no API key here. The bot is ours: a container we start, not an
/// account we rent, so nothing about running it needs a credential from anyone.
/// </summary>
public class MeetingBotConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>user | organization | platform. See <see cref="MeetingBotScopes"/>.</summary>
    public string Scope { get; set; } = MeetingBotScopes.User;

    /// <summary>User id or organisation id. Null for the site-wide default.</summary>
    public string? OwnerId { get; set; }

    /// <summary>
    /// Which meetings this covers: "*" for all, or a platform key such as
    /// google / microsoft / zoom, so a team can treat Zoom differently.
    /// </summary>
    public string MeetingPlatform { get; set; } = "*";

    /// <summary>What attendees see in the participant list.</summary>
    public string BotName { get; set; } = "GenPrompt Notetaker";

    /// <summary>
    /// Arm every calendar meeting with a join link automatically, instead of
    /// choosing them one at a time. Off by default: recording every meeting a
    /// person is invited to should be a decision, not something that starts
    /// happening to them.
    /// </summary>
    public bool AutoRecord { get; set; }

    /// <summary>Signal tools to run when a meeting ends, JSON array of tool keys.</summary>
    public string AutoToolsJson { get; set; } = "[\"summaries\"]";

    /// <summary>Leave the call after this long, whatever the calendar said.</summary>
    public int MaxMinutes { get; set; } = 240;

    /// <summary>Turn the notetaker off at this level without deleting the settings.</summary>
    public bool Enabled { get; set; } = true;

    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public static class MeetingBotScopes
{
    public const string User = "user";
    public const string Organization = "organization";
    public const string Platform = "platform";

    /// <summary>Most specific first. The resolver walks this in order.</summary>
    public static readonly string[] Precedence = [User, Organization, Platform];

    public static bool IsValid(string? scope) =>
        scope is not null && Precedence.Contains(scope, StringComparer.OrdinalIgnoreCase);
}
