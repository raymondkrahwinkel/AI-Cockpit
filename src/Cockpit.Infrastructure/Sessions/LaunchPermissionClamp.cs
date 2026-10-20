using Cockpit.Core.Profiles;
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

    // AC-1593: refuses a start outside providers.allowed, or pins it to an allowed model on both the option and the typed
    // model, since drivers differ in which of the two wins. Shared by the SDK start and the TTY launch.
    public static (IReadOnlyDictionary<string, string>? Options, string? Model) PinModel(
        ProvisionPolicy? policy, SessionProfile? profile, IReadOnlyDictionary<string, string>? options, string? model)
    {
        var source = options ?? new Dictionary<string, string>();
        var stated = source.Where(option => _IsModelKey(option.Key)).Select(option => (string?)option.Value).Append(model).ToArray();
        var (refusal, pinned) = OrgProviderPolicy.Check(policy, profile, stated);
        if (refusal is not null)
        {
            throw new OrgPolicyRefusedException(refusal);
        }

        if (pinned is null)
        {
            return (options, model);
        }

        var comparer = (source as Dictionary<string, string>)?.Comparer ?? StringComparer.Ordinal;
        var result = source.Where(option => !_IsModelKey(option.Key)).ToDictionary(option => option.Key, option => option.Value, comparer);
        result[WellKnownPluginSessionOptions.Model] = pinned;
        return (result, pinned);
    }

    private static bool _IsModelKey(string key) => string.Equals(key, WellKnownPluginSessionOptions.Model, StringComparison.OrdinalIgnoreCase);

    private static bool _IsModeKey(string key) => string.Equals(key, WellKnownPluginSessionOptions.PermissionMode, StringComparison.OrdinalIgnoreCase);
}
