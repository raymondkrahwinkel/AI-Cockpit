using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;

namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// What the cockpit knows about a session it is about to start, handed to an
/// <see cref="ITtySessionProvider"/> so it can compose its <see cref="TtyLaunchSpec"/>.
/// </summary>
/// <param name="Profile">The profile the session runs under, or null for the host's own configuration.</param>
/// <param name="Options">
/// The start defaults the operator chose, in the <em>provider's</em> vocabulary — Claude speaks
/// <c>permission-mode</c>/<c>model</c>/<c>effort</c> (see <see cref="TtyLaunchOption"/>), Codex speaks
/// <c>sandbox</c>. Deliberately a string map rather than typed fields: the moment the cockpit names these
/// knobs itself, every other provider has to pretend to be Claude to be understood.
/// </param>
/// <param name="WorkingDirectory">Absolute path the pty child runs in, already resolved by the host.</param>
/// <param name="Resume">
/// Pick up an earlier conversation instead of starting cold. Typed rather than an option string because it is
/// genuinely cross-provider: every agent CLI worth hosting can continue its last conversation or open one by id.
/// </param>
/// <param name="BaseEnvironment">
/// The host's environment for the child, already scrubbed. A provider reads it to compose an overlay that
/// depends on an inherited value (Claude's heap cap extends any inherited <c>NODE_OPTIONS</c> rather than
/// replacing it); it never returns this map, only what it adds to it.
/// </param>
public sealed record TtyLaunchContext(
    SessionProfile? Profile,
    IReadOnlyDictionary<string, string> Options,
    string WorkingDirectory,
    SessionResume? Resume,
    IReadOnlyDictionary<string, string> BaseEnvironment);

/// <summary>
/// The option keys the cockpit's own New-session dialog fills in today. They are Claude's words, and that is
/// exactly why they live here as constants rather than as fields on <see cref="TtyLaunchContext"/>: a second
/// provider declares its own keys and the dialog renders those, without the core having to learn either
/// vocabulary (the declarative option catalogue — fase 2 of the provider-plugin plan).
/// </summary>
public static class TtyLaunchOption
{
    public const string PermissionMode = "permission-mode";

    public const string Model = "model";

    public const string Effort = "effort";
}
