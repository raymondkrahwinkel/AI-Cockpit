using Cockpit.Core.Provisioning;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

// AC-1593: the launch-time half of the ceilings, shared by the SDK start and the TTY launch so both routes clamp alike.
internal static class LaunchPermissionClamp
{
    // The options clamped under `ceiling`, or null when nothing needed it. `fallback` is the mode that runs when no option
    // names one; with neither, a mode (and a sandbox and approval policy) is stated, as a provider default may be permissive.
    // `Lowered` says something the operator chose was brought down, which they are told.
    public static (IReadOnlyDictionary<string, string>? Options, bool Lowered) Clamp(string? ceiling, IReadOnlyDictionary<string, string>? options, string? fallback)
    {
        if (ceiling is null)
        {
            return (null, false);
        }

        var source = options ?? new Dictionary<string, string>();
        var given = source.Where(option => _IsModeKey(option.Key) && !string.IsNullOrWhiteSpace(option.Value)).Select(option => option.Value).ToList();
        var effective = given.Count > 0 ? given : string.IsNullOrWhiteSpace(fallback) ? [] : [fallback];
        var lowered = effective.Any(mode => OrgPolicyCeilings.Clamp(ceiling, mode) is not null);
        var stated = OrgPolicyCeilings.StatedOptions(ceiling)
            .Where(option => _IsModeKey(option.Key) ? effective.Count == 0 : !source.Keys.Any(key => string.Equals(key, option.Key, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var unsupervised = source.Any(option => OrgPolicyCeilings.SafeOption(ceiling, option.Key, option.Value) is not null);
        if (!lowered && stated.Count == 0 && !unsupervised)
        {
            return (null, false);
        }

        var clamped = source
            .Where(option => !lowered || !_IsModeKey(option.Key))
            .ToDictionary(option => option.Key, option => OrgPolicyCeilings.SafeOption(ceiling, option.Key, option.Value) ?? option.Value, StringComparer.Ordinal);
        foreach (var (key, value) in stated)
        {
            clamped[key] = value;
        }

        if (lowered)
        {
            clamped[WellKnownPluginSessionOptions.PermissionMode] = ceiling;
        }

        return (clamped, lowered || unsupervised);
    }

    // AC-1593: the model the launch options name, in any spelling of the key; null when they name none.
    public static string? ModelOf(IReadOnlyDictionary<string, string>? options) =>
        options?.FirstOrDefault(option => string.Equals(option.Key, WellKnownPluginSessionOptions.Model, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(option.Value)).Value;

    private static bool _IsModeKey(string key) => string.Equals(key, WellKnownPluginSessionOptions.PermissionMode, StringComparison.OrdinalIgnoreCase);
}
