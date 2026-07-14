using Cockpit.Core.Profiles;

namespace Cockpit.Core.Sessions.Tty;

/// <summary>
/// What the <c>claude</c> CLI needs on top of the host's base environment (<see cref="TtyEnvironment.BuildBase"/>).
/// Pure and side-effect-free, so the composition rules are unit-testable without reading the real process
/// environment — and deliberately its own type: everything Claude-shaped stands apart from the environment every
/// TUI shares, which is what makes it movable when Claude becomes a plugin.
/// </summary>
public static class ClaudeTtyEnvironment
{
    /// <summary>
    /// The profile's config directory, and a heap cap when the profile asks for one.
    /// <para>
    /// A profile on a non-default directory exports <c>CLAUDE_CONFIG_DIR</c>; a profile pinned to the CLI's
    /// default (<c>~/.claude</c>) <em>clears</em> any inherited value so the CLI uses its native home-root config
    /// and login — setting it to the default directory is not a no-op (see
    /// <see cref="ClaudeConfigDirectory.ResolveSpawnOverride"/>, and the onboarding bug that taught us). A
    /// profile-less session leaves an inherited value untouched, since the transcript tailers resolve the
    /// directory through that same variable.
    /// </para>
    /// </summary>
    /// <returns>
    /// An overlay in <see cref="Abstractions.Sessions.TtyLaunchSpec.EnvironmentOverlay"/>'s shape: a value to
    /// set, or <see langword="null"/> to remove the variable from the base map.
    /// </returns>
    public static IReadOnlyDictionary<string, string?> BuildOverlay(
        IReadOnlyDictionary<string, string> baseEnvironment,
        SessionProfile? profile,
        string userProfileDirectory)
    {
        var overlay = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (profile is null)
        {
            return overlay;
        }

        overlay[ClaudeConfigDirectory.EnvironmentVariable] =
            ClaudeConfigDirectory.ResolveSpawnOverride(profile, userProfileDirectory);

        // A memory ceiling, when the profile asks for one. Off unless it does: a capped session that needs more
        // memory than the cap does not slow down, it dies mid-turn.
        if (SessionMemoryLimit.NodeOptions(baseEnvironment.GetValueOrDefault("NODE_OPTIONS"), profile.MemoryLimitMb) is { } options)
        {
            overlay["NODE_OPTIONS"] = options;
        }

        return overlay;
    }
}
