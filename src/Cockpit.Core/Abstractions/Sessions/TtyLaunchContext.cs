using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;

namespace Cockpit.Core.Abstractions.Sessions;

// AC-1013: What the cockpit knows about a session it is about to start, handed to an ITtySessionProvider
// so it can compose its TtyLaunchSpec. Trimmed: why Options is a string map (not typed fields, so a
// second provider need not pretend to be Claude); Resume is typed (cross-provider resume-by-id); BaseEnvironment is overlay-only, a provider only adds to it, it never returns the map.
public sealed record TtyLaunchContext(
    SessionProfile? Profile,
    IReadOnlyDictionary<string, string> Options,
    string WorkingDirectory,
    SessionResume? Resume,
    IReadOnlyDictionary<string, string> BaseEnvironment)
{
    // AC-1013: Per-session MCP-server selection (#44); null means no narrowing. Trimmed: this follows the
    // same convention as McpServerRegistryFilter.ApplySessionSelection, and is an init property (not a
    // positional param) so existing constructor call sites keep compiling — only the launcher sets it.
    public IReadOnlySet<string>? EnabledMcpServerNames { get; init; }

    // AC-1013: Project this session was started under (AC-218), or null for none. Trimmed: a provider
    // fanning the shared registry into its config (e.g. Claude's --mcp-config) resolves against this
    // project's own registry view instead of the unscoped one; init property for the same reason as above.
    public string? ProjectId { get; init; }
}

// AC-1013: Option keys the cockpit's New-session dialog fills in today — Claude's words, kept as constants
// here (not fields on TtyLaunchContext) so a second provider can declare its own without the core learning
// either vocabulary. Trimmed: this is a stopgap ahead of the declarative option catalogue (fase 2).
public static class TtyLaunchOption
{
    public const string PermissionMode = "permission-mode";

    public const string Model = "model";

    public const string Effort = "effort";
}
