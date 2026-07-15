namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// What a plugin hands the host via <see cref="ICockpitHost.AddSessionProvider"/> (#45) to register a new
/// session provider: its stable id and display label, a factory minting its driver from the profile's
/// opaque config JSON, the capabilities it supports, and a factory for its "add/edit profile" config view.
/// </summary>
/// <param name="ProviderId">
/// Stable id for this provider, namespaced by the plugin (e.g. <c>"gemini-provider.gemini"</c>) so two
/// plugins can never collide. Persisted on a profile's <c>PluginProviderConfig</c> — must not change once
/// profiles exist under it.
/// </param>
/// <param name="DisplayName">Shown in the provider picker, e.g. <c>"Gemini (OpenAI-compatible)"</c>.</param>
/// <param name="CreateDriverFactory">Builds the <see cref="IPluginSessionDriverFactory"/> for this provider, given the host's service provider.</param>
/// <param name="Capabilities">What this provider's driver supports, so the session UI renders the right controls.</param>
/// <param name="CreateConfigView">Builds the "add/edit profile" config view; the argument is the existing config JSON (edit) or <see langword="null"/> (add).</param>
/// <param name="DefaultBaseUrl">Pre-filled default base URL for this provider's config view, when it has one.</param>
public sealed record SessionProviderRegistration(
    string ProviderId,
    string DisplayName,
    Func<IServiceProvider, IPluginSessionDriverFactory> CreateDriverFactory,
    PluginSessionCapabilities Capabilities,
    Func<string?, IPluginProviderConfigView> CreateConfigView,
    string DefaultBaseUrl = "")
{
    /// <summary>
    /// The per-session start defaults this provider wants the New-session dialog to ask about (sandbox, model, …),
    /// the SDK-session mirror of <see cref="TtyProviderRegistration.Options"/>. Empty when it wants none. An
    /// init-only property rather than a primary-ctor parameter so adding it does not change the record's
    /// constructor signature — an already-compiled plugin keeps constructing this the old way and simply reports
    /// no options.
    /// </summary>
    public IReadOnlyList<PluginSessionLaunchOption> Options { get; init; } = [];
}
