namespace Cockpit.Plugin.ClaudeProvider;

/// <summary>
/// The provider ids this plugin registers under. <see cref="Claude"/> deliberately equals the host's existing
/// Claude TTY id (<c>ClaudeTtySessionProvider.Id</c>), so the resolver can route a Claude profile to this plugin
/// while the in-tree provider stays as a transition fallback.
/// </summary>
internal static class ClaudeProviderIds
{
    public const string Claude = "claude";
}
