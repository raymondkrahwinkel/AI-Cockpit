using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;

namespace Cockpit.Core.Abstractions.Sessions;

// What the cockpit knows about a session it is about to start, handed to an
// `ITtySessionProvider` so it can compose its `TtyLaunchSpec`.
//
// `Profile`: The profile the session runs under, or null for the host's own configuration.
// `Options`:
// The start defaults the operator chose, in the *provider's* vocabulary — Claude speaks
// `permission-mode`/`model`/`effort` (see `TtyLaunchOption`), Codex speaks
// `sandbox`. Deliberately a string map rather than typed fields: the moment the cockpit names these
// knobs itself, every other provider has to pretend to be Claude to be understood.
// `WorkingDirectory`: Absolute path the pty child runs in, already resolved by the host.
// `Resume`:
// Pick up an earlier conversation instead of starting cold. Typed rather than an option string because it is
// genuinely cross-provider: every agent CLI worth hosting can continue its last conversation or open one by id.
// `BaseEnvironment`:
// The host's environment for the child, already scrubbed. A provider reads it to compose an overlay that
// extends an inherited value rather than replacing it; it never returns this map, only what it adds to it.
public sealed record TtyLaunchContext(
    SessionProfile? Profile,
    IReadOnlyDictionary<string, string> Options,
    string WorkingDirectory,
    SessionResume? Resume,
    IReadOnlyDictionary<string, string> BaseEnvironment)
{
    // The per-session MCP-server selection (#44) the operator made in the New-session dialog: the enabled server
    // names to narrow the shared registry to. `null` means no narrowing (every eligible server
    // passes) — the same convention `Cockpit.Core.Mcp.McpServerRegistryFilter.ApplySessionSelection`
    // reads. An init property rather than a positional parameter so the many existing constructor call sites keep
    // compiling; only the launcher fills it in.
    public IReadOnlySet<string>? EnabledMcpServerNames { get; init; }

    // The project this session was started under (AC-218), or `null` for one belonging to none. A
    // provider that fans the shared registry into its config (Claude's `--mcp-config`) resolves it against
    // that project's own registry view — its own servers and its by-name overrides — instead of the unscoped
    // registry. An init property for the same reason as `EnabledMcpServerNames`: the many existing
    // constructor call sites keep compiling; only the launcher fills it in.
    public string? ProjectId { get; init; }
}

// The option keys the cockpit's own New-session dialog fills in today. They are Claude's words, and that is
// exactly why they live here as constants rather than as fields on `TtyLaunchContext`: a second
// provider declares its own keys and the dialog renders those, without the core having to learn either
// vocabulary (the declarative option catalogue — fase 2 of the provider-plugin plan).
public static class TtyLaunchOption
{
    public const string PermissionMode = "permission-mode";

    public const string Model = "model";

    public const string Effort = "effort";
}
