using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.Core.Profiles;

// AC-1292: whether a profile starts its sessions past their own approval gate, shown beside it on the Nodes page.
// Providers share no shape, so this asks each one's own key — Claude's `permission-mode`, Codex's `sandbox` and
// `approvalPolicy`. A provider declaring none of them (LM Studio, OpenRouter) simply never matches.
public static class UnsupervisedProfile
{
    // Only what a profile explicitly sets. `OptionDefaults` being null or missing a key means that option falls
    // back to its own default, which is the provider's to decide and not visible here — so this under-reports
    // rather than guesses, and is never a complete list of what runs unsupervised.
    public static bool SkipsApprovals(ProfileDefaults? defaults) =>
        defaults?.OptionDefaults is { } options
        && (_Is(options, TtyLaunchOption.PermissionMode, "bypassPermissions")
            || _Is(options, "sandbox", "danger-full-access")
            || _Is(options, "approvalPolicy", "never"));

    private static bool _Is(IReadOnlyDictionary<string, string> options, string key, string value) =>
        options.TryGetValue(key, out var actual) && string.Equals(actual, value, StringComparison.OrdinalIgnoreCase);
}
