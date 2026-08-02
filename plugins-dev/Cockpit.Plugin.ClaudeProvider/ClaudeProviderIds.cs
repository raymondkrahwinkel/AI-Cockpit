namespace Cockpit.Plugin.ClaudeProvider;

// The provider ids this plugin registers under. `Claude` deliberately equals the host's existing
// Claude TTY id (`ClaudeTtySessionProvider.Id`), so the resolver can route a Claude profile to this plugin
// while the in-tree provider stays as a transition fallback.
internal static class ClaudeProviderIds
{
    public const string Claude = "claude";
}
