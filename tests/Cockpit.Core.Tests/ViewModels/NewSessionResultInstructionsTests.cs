using Cockpit.App.ViewModels;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// How the resolved instructions reach a provider (AC-142): folded into the launch options under the well-known
/// append-system-prompt key, which every provider already honours. If they do not land here they reach no session
/// at all, whatever the profile says.
/// <para>
/// AC-544: a profile with nothing of its own to say no longer leaves the key off entirely — it falls back to
/// <see cref="AgentStatusSystemPrompt.Default"/>, so a session started with no profile identity still starts
/// knowing to keep its own statusline current. A profile that wrote its own prompt still wins outright, unchanged
/// from AC-142 (see <see cref="AgentStatusSystemPrompt"/>'s own remarks for why the two are never merged).
/// </para>
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
        // StartsWith rather than Equal since AC-544: the standing status instruction now rides after the profile's
        // own words. What this test is about is that the profile's prompt reaches the well-known key intact and
        // first — that it is not the only thing there is asserted by the criterion-5 test at the bottom.
        Assert.StartsWith("You are Olaf.", options![WellKnownPluginSessionOptions.AppendSystemPrompt]);
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
    public void SdkLaunchOptionsWithInstructions_NoPrompt_KeepsTheProvidersOwnOptions()
    {
        var provided = new Dictionary<string, string> { ["model"] = "opus" };

        var options = Result(systemPrompt: null, provided).SdkLaunchOptionsWithInstructions;

        Assert.Equal("opus", options!["model"]);
    }

    // AC-544: a profile with no SystemPrompt of its own used to leave the append-system-prompt key off entirely
    // (asserted here as Null before this ticket). Now the key is always present, carrying the status default —
    // a test that stayed green with that default deleted would not actually be testing this ticket.
    [Fact]
    public void SdkLaunchOptionsWithInstructions_NoPromptAndNoOptions_FallsBackToTheStatusDefault()
    {
        var options = Result(systemPrompt: null).SdkLaunchOptionsWithInstructions;

        Assert.NotNull(options);
        Assert.Equal(AgentStatusSystemPrompt.Default, options![WellKnownPluginSessionOptions.AppendSystemPrompt]);
    }

    [Fact]
    public void TtyLaunchOptionsWithInstructions_CarriesThePromptToo()
    {
        // The TTY route is a separate launch path; a profile's identity must not be an SDK-only privilege.
        var options = Result("You are Olaf.").TtyLaunchOptionsWithInstructions;

        Assert.StartsWith("You are Olaf.", options![WellKnownPluginSessionOptions.AppendSystemPrompt]);
    }

    [Fact]
    public void TtyLaunchOptionsWithInstructions_NoPrompt_FallsBackToTheStatusDefaultToo()
    {
        var options = Result(systemPrompt: null).TtyLaunchOptionsWithInstructions;

        Assert.Equal(AgentStatusSystemPrompt.Default, options![WellKnownPluginSessionOptions.AppendSystemPrompt]);
    }

    [Fact]
    public void LaunchOptions_BlankPrompt_FallsBackToTheStatusDefault()
    {
        var options = Result("   ").SdkLaunchOptionsWithInstructions;

        Assert.Equal(AgentStatusSystemPrompt.Default, options![WellKnownPluginSessionOptions.AppendSystemPrompt]);
    }

    // AC-544 criterion 5, held to its own precedent: the delegation instruction rides alongside a profile's prompt
    // rather than being displaced by it (ClaudeTtyProvider._AppendedInstructions), and this one does the same. The
    // profile's words come first and are intact; the standing instruction is still there. A profile that carries an
    // identity is precisely the one doing ticket work, so losing the status nudge on exactly those sessions would
    // have been the expensive way round.
    [Fact]
    public void SdkLaunchOptionsWithInstructions_ProfileOwnPrompt_KeepsItAndStillCarriesTheStatusInstruction()
    {
        var options = Result("You are Olaf.").SdkLaunchOptionsWithInstructions;

        var prompt = options![WellKnownPluginSessionOptions.AppendSystemPrompt];
        Assert.StartsWith("You are Olaf.", prompt);
        Assert.Contains(AgentStatusSystemPrompt.Default, prompt);
    }

    [Fact]
    public void TtyLaunchOptionsWithInstructions_ProfileOwnPrompt_KeepsItAndStillCarriesTheStatusInstruction()
    {
        // Asserted per route rather than once: read-aloud (AC-97) landed SDK-only while the complaint was about TTY,
        // and this is the same shape of feature — one that reaches a session through two separate option maps.
        var options = Result("You are Olaf.").TtyLaunchOptionsWithInstructions;

        var prompt = options![WellKnownPluginSessionOptions.AppendSystemPrompt];
        Assert.StartsWith("You are Olaf.", prompt);
        Assert.Contains(AgentStatusSystemPrompt.Default, prompt);
    }
}
