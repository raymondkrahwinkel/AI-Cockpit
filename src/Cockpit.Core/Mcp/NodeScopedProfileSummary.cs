using Cockpit.Core.Profiles;

namespace Cockpit.Core.Mcp;

// AC-794: an allow-list of what a scoped controller may see about a profile — not `SessionProfile`, whose fields
// aren't all secret-shaped (AC-353). New fields are added here explicitly; AC-1351 added `SkipsApprovals`, a yes/no
// read off the profile's options (gap E of AC-1283), because a controller must know a profile runs unattended.
public sealed record NodeScopedProfileSummary(string Label, SessionProvider Provider, string? Purpose, bool SkipsApprovals = false);
