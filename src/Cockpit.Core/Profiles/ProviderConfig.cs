namespace Cockpit.Core.Profiles;

// The provider-specific connection settings a profile runs under (#26). Every provider has one, including the
// Claude CLI (`ClaudeConfig`) — it used to be the exception that needed none, which made it the
// provider a profile had when it had no provider at all.
public abstract record ProviderConfig(SessionProvider Provider);
