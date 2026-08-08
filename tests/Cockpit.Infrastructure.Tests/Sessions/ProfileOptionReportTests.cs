using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Tests.Sessions;

/// <summary>
/// What a profile is configured to run at, read off its provider's declared schema (AC-647). The whole point of
/// this class is that the three providers below come back looking different from each other: the moment a Codex
/// profile reports Claude's fields, the assistant is being told something no provider means.
/// </summary>
public class ProfileOptionReportTests
{
    private static readonly PluginSessionCapabilities Claude =
        new(SupportsTools: true, SupportsPermissions: true)
        {
            DeclaredOptions =
            [
                new("permission-mode", "Permission mode",
                    [new("default", "Ask permissions"), new("bypassPermissions", "Bypass permissions")], "default"),
                new("model", "Model"),
                new("effort", "Effort", [new("medium", "Medium"), new("high", "High")], "medium"),
            ],
        };

    private static readonly PluginSessionCapabilities Codex =
        new(SupportsTools: true, SupportsPermissions: true)
        {
            DeclaredOptions = [new("sandbox", "Sandbox", [new("read-only", "read-only")], "read-only")],
        };

    [Fact]
    public void AClaudeProfile_ReportsWhatItIsSetTo_WithTheProvidersOwnWordForEachValue()
    {
        // The profile that started AC-647: `bypassPermissions`/`opus`/`high` sat in cockpit.json where nothing but
        // filesystem access could read them, and "bypassPermissions" is not a phrase that reads as what it does.
        var options = ProfileOptionReport.For(
            Claude,
            new Dictionary<string, string> { ["permission-mode"] = "bypassPermissions", ["model"] = "opus", ["effort"] = "high" });

        Assert.Equal(["permission-mode", "model", "effort"], options.Select(option => option.Key));
        Assert.Equal(["Permission mode", "Model", "Effort"], options.Select(option => option.Label));
        Assert.Equal(["bypassPermissions", "opus", "high"], options.Select(option => option.Value));
        Assert.Equal(["Bypass permissions", "opus", "High"], options.Select(option => option.ValueLabel));
        Assert.All(options, option => Assert.True(option.SetOnProfile));
    }

    [Fact]
    public void TheCodexProfile_ReportsItsSandbox_AndNoneOfClaudesThreeFields()
    {
        // Criterion 3. A schema that quietly forced every provider into permission-mode/model/effort would pass a
        // test that only ever asked about Claude, so this one asserts what is absent as much as what is there.
        var options = ProfileOptionReport.For(Codex, new Dictionary<string, string> { ["sandbox"] = "read-only" });

        var option = Assert.Single(options);
        Assert.Equal("sandbox", option.Key);
        Assert.Equal("read-only", option.ValueLabel);
        Assert.DoesNotContain(options, reported => reported.Key is "permission-mode" or "effort");
    }

    [Fact]
    public void AProviderThatDeclaresNothing_ReportsNothing_RatherThanBorrowedFields()
    {
        // Criterion 4: LM Studio and Ollama take their model from the provider config, not from an options map.
        // Empty here is the honest answer, and the caller says so — it is not a gap to fill with Claude's list.
        Assert.Empty(ProfileOptionReport.For(
            new PluginSessionCapabilities(SupportsTools: false, SupportsPermissions: false),
            new Dictionary<string, string> { ["effort"] = "high" }));

        Assert.Empty(ProfileOptionReport.For(capabilities: null, optionDefaults: null));
    }

    [Fact]
    public void AnOptionTheProfileNeverSet_ComesBackAsTheProvidersDefault_MarkedAsNobodysChoice()
    {
        // The distinction AC-648 will need: what the operator chose versus what merely applies. Reporting the hint
        // as if the profile had set it would make every profile look deliberately configured.
        var options = ProfileOptionReport.For(Claude, optionDefaults: null);

        Assert.All(options, option => Assert.False(option.SetOnProfile));
        Assert.Equal("Ask permissions", options.Single(option => option.Key == "permission-mode").ValueLabel);

        // A free-form option with no default the provider can name has no value to report — better said than guessed.
        Assert.Null(options.Single(option => option.Key == "model").Value);
    }

    [Fact]
    public void AStoredKeyTheProviderDoesNotDeclare_IsNotReported()
    {
        // A leftover from another provider or an older build. The host has no idea what it means, and reading it
        // out would be the guesswork this ticket exists to remove.
        var options = ProfileOptionReport.For(Codex, new Dictionary<string, string> { ["effort"] = "high" });

        Assert.Equal("sandbox", Assert.Single(options).Key);
    }
}
