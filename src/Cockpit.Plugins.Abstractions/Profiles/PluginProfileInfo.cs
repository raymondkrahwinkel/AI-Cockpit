namespace Cockpit.Plugins.Abstractions.Profiles;

/// <summary>
/// One of the cockpit's session profiles as a plugin sees it: enough to find the state that profile keeps on
/// disk, without exposing the host's own profile model. A plugin that reads a provider's on-disk artefacts
/// (the Claude CLI's transcripts, say) needs the directories the operator actually configured, not a guess at
/// the well-known ones.
/// </summary>
/// <param name="Label">Display name, as shown in the profile picker.</param>
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
public sealed record PluginProfileInfo(string Label, string Provider, string ConfigDirectory);
