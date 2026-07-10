namespace Cockpit.Core.Profiles;

/// <summary>
/// The provider-specific connection settings a profile runs under (#26). The Claude-CLI provider uses
/// the profile's own <see cref="ClaudeProfile.ConfigDir"/>/<see cref="ClaudeProfile.ExecutablePath"/>
/// fields, so it needs no config record — a <see langword="null"/> provider config on a profile means
/// Claude-CLI. The HTTP providers (Ollama, LM Studio) carry their own base-URL/model here.
/// </summary>
public abstract record ProviderConfig(SessionProvider Provider);
