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

        Assert.NotNull(options);
        Assert.Equal("You are Olaf.", options![WellKnownPluginSessionOptions.AppendSystemPrompt]);
    }

    [Fact]
    public void SdkLaunchOptionsWithInstructions_KeepsTheProvidersOwnOptions()
    {
        var options = Result("You are Olaf.", new Dictionary<string, string> { ["model"] = "opus" })
            .SdkLaunchOptionsWithInstructions;

        Assert.Equal("opus", options!["model"]);
        Assert.Contains(WellKnownPluginSessionOptions.AppendSystemPrompt, options!);
    }

    [Fact]
    public void SdkLaunchOptionsWithInstructions_NoPrompt_LeavesTheOptionsUntouched()
    {
        var provided = new Dictionary<string, string> { ["model"] = "opus" };

        Assert.Same(provided, Result(systemPrompt: null, provided).SdkLaunchOptionsWithInstructions);
    }

    [Fact]
    public void SdkLaunchOptionsWithInstructions_NoPromptAndNoOptions_StaysNull()
    {
        Assert.Null(Result(systemPrompt: null).SdkLaunchOptionsWithInstructions);
    }

    [Fact]
    public void TtyLaunchOptionsWithInstructions_CarriesThePromptToo()
    {
        // The TTY route is a separate launch path; a profile's identity must not be an SDK-only privilege.
        var options = Result("You are Olaf.").TtyLaunchOptionsWithInstructions;

        Assert.Equal("You are Olaf.", options![WellKnownPluginSessionOptions.AppendSystemPrompt]);
    }

    [Fact]
    public void LaunchOptions_BlankPrompt_AddsNothing()
    {
        Assert.Null(Result("   ").SdkLaunchOptionsWithInstructions);
    }
}
