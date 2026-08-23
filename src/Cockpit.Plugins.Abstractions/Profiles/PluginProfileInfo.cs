using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugins.Abstractions.Profiles;

/// <summary>
/// One of the cockpit's session profiles as a plugin sees it: enough to find the state that profile keeps on
/// disk, without exposing the host's own profile model. A plugin that reads a provider's on-disk artefacts
/// (the Claude CLI's transcripts, say) needs the directories the operator actually configured, not a guess at
/// the well-known ones.
/// </summary>
/// <param name="Label">
/// Display name, as shown in the profile picker.
/// </param>
/// <param name="Provider">
/// Which backend the profile runs under, as the host's provider name (<c>ClaudeCli</c>, <c>Ollama</c>,
/// <c>LmStudio</c>, <c>Plugin</c>). A string rather than an enum so the contract does not have to change every
/// time the host gains a provider — match on the ones you care about and ignore the rest.
/// </param>
/// <param name="ConfigDirectory">
/// The provider's per-profile config directory (for a Claude-CLI profile: its <c>CLAUDE_CONFIG_DIR</c>, holding
/// that identity's credentials, config and <c>projects/</c> transcripts). Empty for a profile whose provider
/// keeps no such directory.
/// </param>
public sealed record PluginProfileInfo(string Label, string Provider, string ConfigDirectory)
{
    /// <summary>
    /// The models this profile lets a consumer pick from, where its provider offers a choice — for a picker like
    /// Autopilot's CEO-model field. Empty when the profile pins its own model or offers no static list.
    /// </summary>
    /// <remarks>
    /// This is a <em>set</em>, not a ranking — its order carries no promise about price or capability; use
    /// <see cref="ModelCostEstimatesCheapestFirst"/> for that (AC-256).
    /// </remarks>
    public IReadOnlyList<string> ModelSuggestions { get; init; } = [];

    /// <summary>
    /// What this profile's models cost as its own provider estimates them, cheapest first — passed through from
    /// the provider's declaration, never worked out by the host.
    /// </summary>
    /// <remarks>
    /// Empty when the provider ranks nothing; treat <see cref="ModelSuggestions"/> as unordered then.
    /// </remarks>
    public IReadOnlyList<PluginModelCostEstimate> ModelCostEstimatesCheapestFirst { get; init; } = [];

    /// <summary>
    /// Whether this profile runs a model on local hardware rather than a paid hosted API — a cost signal for a
    /// consumer that routes work across profiles.
    /// </summary>
    /// <remarks>
    /// Host-supplied, like <see cref="ModelSuggestions"/>. Defaults to <see langword="false"/> — treat an unknown
    /// provider as paid, the safe assumption for a cost decision.
    /// </remarks>
    public bool RunsLocally { get; init; }
}
