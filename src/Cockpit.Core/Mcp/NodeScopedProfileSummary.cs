using Cockpit.Core.Profiles;

namespace Cockpit.Core.Mcp;

// AC-794: an allow-list (like `SharedProjectPublishDefinition` at the Depot boundary) of what a scoped
// controller may see about a profile — not `SessionProfile` itself, since fields like `ProviderConfig` or
// `SystemPrompt` aren't secret-shaped (AC-353), so "strip secrets" wouldn't catch them. New fields must be added here explicitly.
// AC-1351: `SkipsApprovals` crosses because a controller starting a session there has to know it runs unattended
// (gap E of AC-1283) — a yes/no read off the profile's own options, never the options themselves.
public sealed record NodeScopedProfileSummary(string Label, SessionProvider Provider, string? Purpose, bool SkipsApprovals = false);
