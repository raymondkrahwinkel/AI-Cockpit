using Cockpit.Core.Profiles;

namespace Cockpit.Core.Mcp;

// AC-794: an allow-list (like `SharedProjectPublishDefinition` at the Depot boundary) of what a scoped
// controller may see about a profile — not `SessionProfile` itself, since fields like `ProviderConfig` or
// `SystemPrompt` aren't secret-shaped (AC-353), so "strip secrets" wouldn't catch them. New fields must be added here explicitly.
public sealed record NodeScopedProfileSummary(string Label, SessionProvider Provider, string? Purpose);
