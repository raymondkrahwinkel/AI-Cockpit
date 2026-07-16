using System.Text.RegularExpressions;

namespace Cockpit.Core.Profiles;

/// <summary>
/// One environment variable a <see cref="SessionProfile"/> injects into its sessions at spawn, on both the
/// TTY and the SDK route (AC-22). Lets the operator close a gap a GUI/AppImage launch leaves — a variable an
/// interactive shell exports that the cockpit process never inherited — per profile, instead of leaning on
/// shell startup. <see cref="IsSecret"/> marks the value as a credential: it persists encrypted and the
/// profile editor masks it.
/// </summary>
public sealed partial record ProfileEnvironmentVariable(string Key, string Value, bool IsSecret = false)
{
    /// <summary>
    /// Whether <paramref name="key"/> is a POSIX-style variable name (letters, digits and underscores, not
    /// starting with a digit). The editor refuses anything else — a name a shell could not set either only
    /// defers the failure to spawn time.
    /// </summary>
    public static bool IsValidKey(string? key) => !string.IsNullOrEmpty(key) && _KeyPattern().IsMatch(key);

    /// <summary>
    /// The profile's variables as a spawn overlay (key → value), the shape <c>TtyEnvironment.Compose</c> and
    /// the SDK spawn paths consume. A later duplicate wins, matching what an operator expects from a list
    /// edited top to bottom.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> ToOverlay(IEnumerable<ProfileEnvironmentVariable> variables)
    {
        // Ordinal: environment variable names are case-sensitive on POSIX; the TTY composition applies its own
        // case rule per platform when it merges this overlay in.
        var overlay = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var variable in variables)
        {
            overlay[variable.Key] = variable.Value;
        }

        return overlay;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex _KeyPattern();
}
