namespace Cockpit.Core.Sessions;

/// <summary>
/// A ceiling on how much memory a session's CLI may use. The Claude CLI is Node, and Node grows its heap to whatever
/// the conversation needs — a long session on this machine sits at 700 MB, four times the whole cockpit. This is the
/// only lever there is from the outside: <c>--max-old-space-size</c>, which makes V8 collect harder rather than grow.
/// <para>
/// It is a loaded gun, and it is off unless a profile asks for it. A session that genuinely needs more memory than the
/// cap does not slow down — it dies, mid-turn, and the work in that turn is gone. So the number is per profile, never
/// a default, and the dialog says this in as many words.
/// </para>
/// <para>
/// An existing <c>NODE_OPTIONS</c> is kept and appended to: the operator may have put something there for reasons of
/// their own, and quietly replacing it would be its own small betrayal.
/// </para>
/// </summary>
public static class SessionMemoryLimit
{
    /// <summary>Below this, the CLI cannot start a conversation at all, so a number under it is a session that never runs.</summary>
    public const int MinimumMegabytes = 256;

    /// <summary>What <c>NODE_OPTIONS</c> should be, given what it already is and what the profile asks for. Null means: leave it alone.</summary>
    public static string? NodeOptions(string? existing, int? megabytes)
    {
        if (megabytes is not { } limit || limit < MinimumMegabytes)
        {
            return null;
        }

        var flag = $"--max-old-space-size={limit}";
        var current = existing?.Trim() ?? string.Empty;

        if (current.Length == 0)
        {
            return flag;
        }

        // Already capped by hand: the operator's own number wins. A profile that silently overrode what someone
        // typed into their environment would be the kind of surprise nobody can debug.
        return current.Contains("--max-old-space-size", StringComparison.Ordinal)
            ? current
            : $"{current} {flag}";
    }
}
