using Cockpit.App.ViewModels;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Workspaces;
using FluentAssertions;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// The Autopilot isolate-in-worktree gate blocker: an embedded run's explicit permission mode (its autonomy mode)
/// must win over the profile's own stored permission-mode default, or a profile saved on <c>bypassPermissions</c>
/// makes the fail-closed confinement gate unpassable and Autopilot can never run on it.
///
/// The chain proven end to end: <see cref="CockpitViewModel._EmbeddedLaunchOptions"/> assembles the launch options
/// (the host-side fix — it drops the profile's stored permission-mode when the request names its own), then
/// <see cref="PluginSessionDriverAdapter"/> resolves the effective mode the confinement check reads. A
/// permission-based confining provider (Claude — <see cref="PluginSessionCapabilities.ConfinesViaPermissionsOnly"/>)
/// stands in, so the effective mode drives the vouch the isolation gate reads.
/// Provider-neutral: nothing here is pinned to a brand; the precedence is keyed on the well-known permission-mode option.
/// </summary>
public class EmbeddedAutopilotPermissionModeTests
{
    private static readonly McpAuthKey _authKey = new();

    // A provider whose confinement rests on its permission system (Claude): a bypass mode disables the guard, so the
    // effective per-session mode decides whether it vouches confinement.
    private static PluginSessionCapabilities _PermissionConfining() =>
        new(SupportsTools: true, SupportsPermissions: true) { ConfinesFileAccessToWorkingDirectory = true, ConfinesViaPermissionsOnly = true };

    // A profile stored on bypassPermissions, the way the operator's "work" Claude profile is — its start defaults live
    // in the generic OptionDefaults map (Ordinal, as ClaudePluginProfile mints them), keyed by the well-known option.
    private static SessionProfile _WorkProfileSavedInBypass() =>
        new("work", new ClaudeConfig("/config/dir"))
        {
            Defaults = new ProfileDefaults(string.Empty, string.Empty, string.Empty)
            {
                OptionDefaults = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [WellKnownPluginSessionOptions.PermissionMode] = "bypassPermissions",
                    ["model"] = "sonnet",
                },
            },
        };

    // The Autopilot step's embed request: it isolates in a worktree and asks for its autonomy mode (acceptEdits) —
    // exactly the request the coordinator builds for an impl / review step.
    private static EmbeddedSessionRequest _IsolatedStepRequestingAcceptEdits() =>
        new() { ProfileId = "work", PermissionMode = "acceptEdits", IsolateInWorktree = true };

    [Fact]
    public void EmbeddedLaunchOptions_WhenTheRequestNamesItsOwnMode_DropTheProfilesStoredPermissionModeDefault()
    {
        var options = CockpitViewModel._EmbeddedLaunchOptions(_WorkProfileSavedInBypass(), _IsolatedStepRequestingAcceptEdits());

        // The profile's stored bypass must not survive into the launch options, or the driver's launch-option merge
        // would keep it over the explicit typed request mode. Its other defaults (model) are left untouched.
        options.Should().NotContainKey(WellKnownPluginSessionOptions.PermissionMode);
        options.Should().ContainKey("model").WhoseValue.Should().Be("sonnet");
    }

    [Fact]
    public void EmbeddedLaunchOptions_WhenTheRequestNamesNoMode_KeepTheProfilesStoredPermissionModeDefault()
    {
        // A plain embedded session (the CEO planning round leaves PermissionMode null) still honours the profile's own
        // stored default — the drop only applies when the run makes an explicit choice, so this path does not regress.
        var request = new EmbeddedSessionRequest { ProfileId = "work" };

        var options = CockpitViewModel._EmbeddedLaunchOptions(_WorkProfileSavedInBypass(), request);

        options.Should().ContainKey(WellKnownPluginSessionOptions.PermissionMode).WhoseValue.Should().Be("bypassPermissions");
    }

    [Fact]
    public async Task AnIsolatedStep_OnAProfileSavedInBypass_Confines_BecauseTheExplicitAcceptEditsRequestModeWins()
    {
        var profile = _WorkProfileSavedInBypass();
        var request = _IsolatedStepRequestingAcceptEdits();

        // The host assembles the launch options (the fix), then the driver adapter resolves the effective mode — the
        // same two steps the real embedded start runs. The typed permissionMode is the request's resolved mode.
        var launchOptions = CockpitViewModel._EmbeddedLaunchOptions(profile, request);
        var inner = new FakePluginSessionDriver { Capabilities = _PermissionConfining() };
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        await adapter.StartAsync(profile, permissionMode: request.PermissionMode, launchOptions: launchOptions);

        // The effective mode the driver started on — and that the confinement check reads — is the explicit acceptEdits,
        // not the profile's stored bypass. So the session vouches confinement and the isolate-in-worktree gate proceeds.
        inner.LastLaunchOptions.Should().ContainKey(WellKnownPluginSessionOptions.PermissionMode)
            .WhoseValue.Should().Be("acceptEdits");
        adapter.Capabilities.ConfinesFileAccessToWorkingDirectory.Should().BeTrue();
    }

    [Fact]
    public async Task WithoutDroppingTheProfileDefault_TheBypassOverridesTheRequestMode_AndTheSessionDoesNotConfine()
    {
        // The bug, made concrete: had the profile's stored permission-mode reached the driver in the launch options
        // (as it did before the fix), the launch-option merge keeps bypass over the explicit acceptEdits — so the
        // session reports unconfined and the fail-closed gate refuses the run. This proves the drop is load-bearing.
        var profile = _WorkProfileSavedInBypass();
        var inner = new FakePluginSessionDriver { Capabilities = _PermissionConfining() };
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        await adapter.StartAsync(profile, permissionMode: "acceptEdits", launchOptions: profile.Defaults!.OptionDefaults);

        inner.LastLaunchOptions.Should().ContainKey(WellKnownPluginSessionOptions.PermissionMode)
            .WhoseValue.Should().Be("bypassPermissions");
        adapter.Capabilities.ConfinesFileAccessToWorkingDirectory.Should().BeFalse();
    }
}
