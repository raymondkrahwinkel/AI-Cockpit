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

    public static TheoryData<IReadOnlyDictionary<string, string>?> NoOverrides() =>
        [null, new Dictionary<string, string>()];

    [Theory]
    [InlineData("permission-mode")]
    [InlineData("Permission-Mode")]
    [InlineData(" permission-mode ")]
    [InlineData("sandbox")]
    [InlineData("approvalPolicy")]
    public void ThePermissionModeKey_IsRefused_HoweverItIsSpelledAndWhateverTheProviderDeclares(string key)
    {
        // Criterion 3 (Raymond, 2026-08-08; approvalPolicy added AC-1101). Asserted as a refusal rather than as "the
        // mode stayed default": a merge that quietly dropped the key would pass that second assertion while the
        // caller was told its spawn ran as asked. `sandbox`/`approvalPolicy` are here because Codex answers the
        // same launch-time question (what may this session do to the machine, unattended) with its own words, and
        // both providers are asked so neither can be refused only where it happens to be declared.
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

}
