using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Tests.Sessions;

/// <summary>
/// What a spawn may change about a profile's start options (AC-648). The two halves worth pinning are that an
/// override changes only what it names, and that the keys deciding what a session may do to the machine cannot be
/// named at all — the second asserted as a refusal, not as a value that happened to stay put.
/// </summary>
public class SpawnOptionOverridesTests
{
    private static readonly PluginSessionCapabilities Claude =
        new(SupportsTools: true, SupportsPermissions: true)
        {
            DeclaredOptions =
            [
                new("permission-mode", "Permission mode",
                    [new("default", "Ask permissions"), new("bypassPermissions", "Bypass permissions")], "default"),
                new("model", "Model"),
                new("effort", "Effort", [new("low", "Low"), new("high", "High")], "medium"),
            ],
        };

    private static readonly PluginSessionCapabilities Codex =
        new(SupportsTools: true, SupportsPermissions: true)
        {
            DeclaredOptions = [new("sandbox", "Sandbox", [new("read-only", "read-only")], "read-only")],
        };

    private static readonly Dictionary<string, string> ProfileDefaults = new()
    {
        ["permission-mode"] = "bypassPermissions",
        ["model"] = "opus",
        ["effort"] = "high",
    };

    [Fact]
    public void NoOverrides_ChangeNothing_SoAnOrdinarySpawnStaysExactlyWhatItWas()
    {
        // Criterion 1. Null rather than a copy of the defaults: the launch path reads null as "whatever the profile
        // says", which is the one behaviour every spawn made before this ticket relied on.
        foreach (var overrides in new IReadOnlyDictionary<string, string>?[] { null, new Dictionary<string, string>() })
        {
            var (merged, refusal) = SpawnOptionOverrides.Merge("Claude", Claude, ProfileDefaults, overrides);
            Assert.Null(merged);
            Assert.Null(refusal);
        }
    }

    [Fact]
    public void OverridingOneKey_LeavesEveryOtherAtTheProfilesOwnValue()
    {
        // Criterion 4, and the reason merging is per key: a caller that asks for lighter effort must not silently
        // hand the session a permission mode nobody chose — including the app default, which is not this profile's.
        var (merged, refusal) = SpawnOptionOverrides.Merge(
            "Claude", Claude, ProfileDefaults, new Dictionary<string, string> { ["effort"] = "low" });

        Assert.Null(refusal);
        Assert.Equal("low", merged!["effort"]);
        Assert.Equal("bypassPermissions", merged["permission-mode"]);
        Assert.Equal("opus", merged["model"]);
    }

    [Theory]
    [InlineData("permission-mode")]
    [InlineData("Permission-Mode")]
    [InlineData(" permission-mode ")]
    [InlineData("sandbox")]
    public void ThePermissionModeKey_IsRefused_HoweverItIsSpelledAndWhateverTheProviderDeclares(string key)
    {
        // Criterion 3 (Raymond, 2026-08-08). Asserted as a refusal rather than as "the mode stayed default": a merge
        // that quietly dropped the key would pass that second assertion while the caller was told its spawn ran as
        // asked. `sandbox` is here because Codex answers the same launch-time question with another word, and both
        // providers are asked so neither can be refused only where it happens to be declared.
        var (mergedByClaude, claudeRefusal) = SpawnOptionOverrides.Merge(
            "Claude", Claude, ProfileDefaults, new Dictionary<string, string> { [key] = "bypassPermissions" });
        var (mergedByCodex, codexRefusal) = SpawnOptionOverrides.Merge(
            "Codex", Codex, new Dictionary<string, string> { ["sandbox"] = "read-only" },
            new Dictionary<string, string> { [key] = "danger-full-access" });

        Assert.Null(mergedByClaude);
        Assert.Null(mergedByCodex);
        Assert.Contains("not something a spawn may set", claudeRefusal);
        Assert.Contains("not something a spawn may set", codexRefusal);
    }

    [Fact]
    public void AKeyThisProviderDoesNotDeclare_IsRefusedWithWhatItDoesTake()
    {
        // Criterion 2. `effort` is Claude's word and Codex has no concept of it at all — accepted here it would
        // reach the CLI as a flag it does not take, or be dropped, and either way the spawn would report success.
        var (merged, refusal) = SpawnOptionOverrides.Merge(
            "Codex", Codex, optionDefaults: null, new Dictionary<string, string> { ["effort"] = "low" });

        Assert.Null(merged);
        Assert.Contains("no option called 'effort'", refusal);
        Assert.Contains("'sandbox'", refusal);
    }

    [Fact]
    public void AProviderThatDeclaresNothing_TakesNoOverrideAtAll()
    {
        // An HTTP-backed model reads no options map, so there is nothing to override — said plainly rather than
        // accepted into a map nobody will look at.
        var (merged, refusal) = SpawnOptionOverrides.Merge(
            "LM Studio", capabilities: null, optionDefaults: null, new Dictionary<string, string> { ["effort"] = "low" });

        Assert.Null(merged);
        Assert.Contains("declares no options at all", refusal);
    }

    [Fact]
    public void AValueTheOptionDoesNotTake_IsRefused_AndSoIsAnEmptyOne()
    {
        // The other half of "not mis-sent to the CLI": a closed set is closed, and a blank is a caller that meant to
        // leave the key out.
        Assert.Contains("is not a value", SpawnOptionOverrides.Merge(
            "Claude", Claude, ProfileDefaults, new Dictionary<string, string> { ["effort"] = "ludicrous" }).Refusal);

        Assert.Contains("no value", SpawnOptionOverrides.Merge(
            "Claude", Claude, ProfileDefaults, new Dictionary<string, string> { ["model"] = "  " }).Refusal);
    }

    [Fact]
    public void AFreeFormOption_TakesWhateverItIsGiven()
    {
        // A model id is not a closed set — a pinned snapshot is as valid as an alias, and refusing what the provider
        // never enumerated would make this stricter than the provider itself.
        var (merged, refusal) = SpawnOptionOverrides.Merge(
            "Claude", Claude, ProfileDefaults, new Dictionary<string, string> { ["model"] = "claude-opus-5-20260501" });

        Assert.Null(refusal);
        Assert.Equal("claude-opus-5-20260501", merged!["model"]);
    }

    [Fact]
    public void TheMemoryCapIsTheHostsOwnKey_AcceptedWhateverTheProviderDeclares()
    {
        // AC-661: no provider declares `cockpit.memory-cap-mb` and no driver reads it — the host applies it to the
        // OS. Checked against Codex, which declares no `effort` either, so this proves it passes on its own merit
        // rather than by happening to be in some provider's list.
        var (merged, refusal) = SpawnOptionOverrides.Merge(
            "Codex", Codex, optionDefaults: null, new Dictionary<string, string> { [SessionMemoryCap.OptionKey] = "4096" });

        Assert.Null(refusal);
        Assert.Equal("4096", merged![SessionMemoryCap.OptionKey]);

        // And a value that is not a cap is refused rather than quietly read as "no cap at all".
        Assert.NotNull(SpawnOptionOverrides.Merge(
            "Codex", Codex, optionDefaults: null, new Dictionary<string, string> { [SessionMemoryCap.OptionKey] = "4 GB" }).Refusal);
    }
}
