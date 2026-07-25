using FluentAssertions;
using Cockpit.App.ViewModels;
using Cockpit.Core.Profiles;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// How the resolved instructions reach a provider (AC-142): folded into the launch options under the well-known
/// append-system-prompt key, which every provider already honours. If they do not land here they reach no session
/// at all, whatever the profile says.
/// </summary>
public class NewSessionResultInstructionsTests
{
    private static NewSessionResult Result(string? systemPrompt, IReadOnlyDictionary<string, string>? sdkOptions = null) =>
        new(
            SessionKind.Sdk,
            new SessionProfile("personal", new ClaudeConfig("~/.claude")),
            SessionOptionCatalog.DefaultPermissionMode,
            SessionOptionCatalog.DefaultModel,
            SessionOptionCatalog.DefaultEffort,
            SessionName: null,
            SdkLaunchOptions: sdkOptions,
            SystemPrompt: systemPrompt);

    [Fact]
    public void SdkLaunchOptionsWithInstructions_AddsThePromptUnderTheWellKnownKey()
    {
        var options = Result("You are Olaf.").SdkLaunchOptionsWithInstructions;

        options.Should().NotBeNull();
        options![WellKnownPluginSessionOptions.AppendSystemPrompt].Should().Be("You are Olaf.");
    }

    [Fact]
    public void SdkLaunchOptionsWithInstructions_KeepsTheProvidersOwnOptions()
    {
        var options = Result("You are Olaf.", new Dictionary<string, string> { ["model"] = "opus" })
            .SdkLaunchOptionsWithInstructions;

        options!["model"].Should().Be("opus");
        options.Should().ContainKey(WellKnownPluginSessionOptions.AppendSystemPrompt);
    }

    [Fact]
    public void SdkLaunchOptionsWithInstructions_NoPrompt_LeavesTheOptionsUntouched()
    {
        var provided = new Dictionary<string, string> { ["model"] = "opus" };

        Result(systemPrompt: null, provided).SdkLaunchOptionsWithInstructions.Should().BeSameAs(provided);
    }

    [Fact]
    public void SdkLaunchOptionsWithInstructions_NoPromptAndNoOptions_StaysNull()
    {
        Result(systemPrompt: null).SdkLaunchOptionsWithInstructions.Should().BeNull();
    }

    [Fact]
    public void TtyLaunchOptionsWithInstructions_CarriesThePromptToo()
    {
        // The TTY route is a separate launch path; a profile's identity must not be an SDK-only privilege.
        var options = Result("You are Olaf.").TtyLaunchOptionsWithInstructions;

        options![WellKnownPluginSessionOptions.AppendSystemPrompt].Should().Be("You are Olaf.");
    }

    [Fact]
    public void LaunchOptions_BlankPrompt_AddsNothing()
    {
        Result("   ").SdkLaunchOptionsWithInstructions.Should().BeNull();
    }
}
