using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The AC-139 arm of <see cref="EditableProfileViewModel"/>: a profile's "Default kind" (SDK/TTY) — offered only
/// for a provider that has a TTY route of its own (<see cref="EditableProfileViewModel.HasTtyProvider"/>, the same
/// question <see cref="SessionKindDefaults.HasTtyRoute"/> answers for the New-session dialog) — must round-trip
/// through the editor, and must collapse to no saved default at all for a provider that has none, since a choice
/// that could never take effect is not a real setting.
/// </summary>
public class EditableProfileViewModelDefaultKindTests
{
    private static SessionProfile ClaudeProfile(ProfileSessionKind? defaultKind = null) =>
        new("work", ClaudePluginProfile.Create("/home/r/.claude-work", null)) { DefaultKind = defaultKind };

    private static SessionProfile LocalProfile(ProfileSessionKind? defaultKind = null) =>
        new("local", new OllamaConfig("http://localhost:11434", "llama3.1", null)) { DefaultKind = defaultKind };

    [Fact]
    public void ForAClaudeProfile_HasTtyProviderIsTrue_SoTheToggleIsOffered()
    {
        var editable = new EditableProfileViewModel(ClaudeProfile(), isLoggedIn: true);

        editable.HasTtyProvider.Should().BeTrue();
    }

    // AC-139: a provider with no TTY route (a local HTTP model) never offers a real Default-kind choice — the
    // toggle's TTY side stays disabled, an "(SDK-only)" label explains why (see ManageProfilesDialog.axaml).
    [Fact]
    public void ForALocalProfile_HasTtyProviderIsFalse_SoTheToggleIsSdkOnly()
    {
        var editable = new EditableProfileViewModel(LocalProfile(), isLoggedIn: true);

        editable.HasTtyProvider.Should().BeFalse();
    }

    [Fact]
    public void Load_SeedsSelectedDefaultKind_FromTheProfilesSavedDefault()
    {
        var sdk = new EditableProfileViewModel(ClaudeProfile(ProfileSessionKind.Sdk), isLoggedIn: true);
        var tty = new EditableProfileViewModel(ClaudeProfile(ProfileSessionKind.Tty), isLoggedIn: true);

        sdk.SelectedDefaultKind.Should().Be(SessionKind.Sdk);
        tty.SelectedDefaultKind.Should().Be(SessionKind.Tty);
    }

    // AC-6: a profile saved before this setting existed has no DefaultKind at all — the editor must still show it
    // exactly as today (TTY), not some other fallback.
    [Fact]
    public void Load_WithNoSavedDefaultKind_SeedsTty_SoAnOlderProfileEditsExactlyAsBefore()
    {
        var editable = new EditableProfileViewModel(ClaudeProfile(), isLoggedIn: true);

        editable.SelectedDefaultKind.Should().Be(SessionKind.Tty);
    }

    [Fact]
    public void Save_RoundTripsTheChosenDefaultKind_ForAProviderWithATtyRoute()
    {
        var editable = new EditableProfileViewModel(ClaudeProfile(), isLoggedIn: true);

        editable.SelectDefaultKindSdkCommand.Execute(null);
        editable.ToProfile().DefaultKind.Should().Be(ProfileSessionKind.Sdk);

        editable.SelectDefaultKindTtyCommand.Execute(null);
        editable.ToProfile().DefaultKind.Should().Be(ProfileSessionKind.Tty);
    }

    // AC-139: the setting is meaningless for an SDK-only provider — persisting it anyway would be a choice that can
    // never take effect, so Save collapses it to null (no saved default) regardless of what the disabled toggle reads.
    [Fact]
    public void Save_ForALocalProfile_PersistsNoDefaultKind_RegardlessOfTheDisabledToggle()
    {
        var editable = new EditableProfileViewModel(LocalProfile(), isLoggedIn: true);

        editable.ToProfile().DefaultKind.Should().BeNull();
    }

    [Fact]
    public void HasTtyProvider_UsesTheInjectedResolver_ForAPluginProviderThatRegisteredOne()
    {
        var profile = new SessionProfile("codex", new PluginProviderConfig("codex", "{}"));

        // HasTtyProvider asks the resolver about a placeholder profile (only the provider identity matters, per
        // _ToRouteCheckProfile — see EditableProfileViewModel), not the operator-facing profile passed to the ctor,
        // so the match is on the plugin id rather than reference/value equality with `profile`.
        var resolver = Substitute.For<ITtySessionProviderResolver>();
        resolver.Resolve(Arg.Is<SessionProfile>(p => p.ProviderConfig as PluginProviderConfig != null && (p.ProviderConfig as PluginProviderConfig)!.ProviderId == "codex"))
            .Returns(Substitute.For<ITtySessionProvider>());
        var providers = new[] { new SessionProviderOption("Codex", SessionProvider.Plugin, "codex") };

        var editable = new EditableProfileViewModel(profile, isLoggedIn: true, providers: providers, ttyProviderResolver: resolver);

        editable.HasTtyProvider.Should().BeTrue();
    }

    [Fact]
    public void HasTtyProvider_IsFalse_ForAPluginProviderThatRegisteredNone()
    {
        var resolver = Substitute.For<ITtySessionProviderResolver>();
        resolver.Resolve(Arg.Any<SessionProfile>()).Returns((ITtySessionProvider?)null);
        var profile = new SessionProfile("http-only", new PluginProviderConfig("http-only", "{}"));
        var providers = new[] { new SessionProviderOption("HTTP-only", SessionProvider.Plugin, "http-only") };

        var editable = new EditableProfileViewModel(profile, isLoggedIn: true, providers: providers, ttyProviderResolver: resolver);

        editable.HasTtyProvider.Should().BeFalse();
    }
}
