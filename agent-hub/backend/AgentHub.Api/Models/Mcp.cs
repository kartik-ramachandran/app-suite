namespace AgentHub.Api.Models;

/// <summary>
/// A server from the official MCP registry (registry.modelcontextprotocol.io),
/// synced nightly. This is the browsable catalogue behind the "Add connection"
/// picker — never hand-maintained, so new servers appear without a deploy.
/// One row per server NAME (latest active version only); the registry returns a
/// row per version, which would otherwise show as duplicates in the picker.
/// </summary>
public class McpCatalogServer
{
    public int Id { get; set; }
    /// Reverse-DNS registry name, e.g. "io.github.acme/weather". Unique.
    public string Name { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    /// Derived from the name/description — powers the picker's category filter.
    public string Category { get; set; } = "Other";
    /// "remote" (streamable-http/sse) or "package" (stdio — not callable by us yet).
    public string Transport { get; set; } = "remote";
    /// First usable remote endpoint, empty for package-only servers.
    public string RemoteUrl { get; set; } = string.Empty;
    /// Publisher domain segment of the reverse-DNS name, e.g. "io.github.acme".
    public string Publisher { get; set; } = string.Empty;
    public string? WebsiteUrl { get; set; }
    /// False for package-only servers: shown greyed out with "needs local runtime".
    public bool IsConnectable { get; set; } = true;
    public DateTime RegistryUpdatedAt { get; set; }
    public DateTime SyncedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A user's (or org's) connection to one MCP server. Credentials are stored
/// encrypted at rest — never returned to the client, not even masked.
/// </summary>
public class McpConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string UserId { get; set; } = string.Empty;
    public string? OrganizationId { get; set; }
    public string ServerName { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string RemoteUrl { get; set; } = string.Empty;
    /// none | bearer | header | oauth
    public string AuthType { get; set; } = "none";
    /// Encrypted credential blob (bearer token, header value, or OAuth token set).
    public string? SecretEncrypted { get; set; }
    /// Header name when AuthType == "header", e.g. "X-Api-Key".
    public string? HeaderName { get; set; }
    /// connected | error | disabled | needs_auth
    public string Status { get; set; } = "connected";
    public string? LastError { get; set; }
    public DateTime? LastCheckedAt { get; set; }
    public int ToolCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A tool discovered on a connection. Enablement is per TOOL, not per server:
/// connecting to a server should not silently grant every capability it exposes.
/// </summary>
public class McpConnectionTool
{
    public int Id { get; set; }
    public string ConnectionId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string InputSchemaJson { get; set; } = "{}";
    public bool Enabled { get; set; } = true;
    /// auto | ask — "ask" pauses a run for approval before this tool executes.
    public string ApprovalMode { get; set; } = "ask";
    /// Heuristic from the tool name/description: read-only tools default to auto.
    public bool IsWrite { get; set; } = true;
    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;
}
