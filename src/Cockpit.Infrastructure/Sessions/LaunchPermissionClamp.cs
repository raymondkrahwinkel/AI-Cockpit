using Cockpit.Core.Provisioning;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

// AC-1593: the launch-time half of the mode ceiling, shared by the SDK start and the TTY launch so both routes clamp alike.
internal static class LaunchPermissionClamp
{
    // The options the driver reads its mode from, clamped: every spelling of the key goes, the ceiling takes its place.
    // `fallback` is the mode that runs when no option is there. Null when nothing needed clamping.
    public static IReadOnlyDictionary<string, string>? Clamp(string? ceiling, IReadOnlyDictionary<string, string>? options, string? fallback)
    {
        var given = (options ?? new Dictionary<string, string>())
            .Where(option => _IsModeKey(option.Key) && option.Value.Length > 0)
            .Select(string? (option) => option.Value)
            .ToList();
        var effective = given.Count > 0 ? given : [fallback];
        if (ceiling is null || effective.All(mode => OrgPolicyCeilings.Clamp(ceiling, mode) is null))
        {
            return null;
        }

        var clamped = (options ?? new Dictionary<string, string>())
            .Where(option => !_IsModeKey(option.Key))
            .ToDictionary(option => option.Key, option => option.Value, StringComparer.Ordinal);
        clamped[WellKnownPluginSessionOptions.PermissionMode] = ceiling;
        return clamped;
    }

    private static bool _IsModeKey(string key) => string.Equals(key, WellKnownPluginSessionOptions.PermissionMode, StringComparison.OrdinalIgnoreCase);
}
