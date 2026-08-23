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
/// <param name="DisplayName">
/// Shown in the provider picker, e.g. <c>"Gemini (OpenAI-compatible)"</c>.
/// </param>
/// <param name="CreateDriverFactory">
/// Builds the <see cref="IPluginSessionDriverFactory"/> for this provider, given the host's service provider.
/// </param>
/// <param name="Capabilities">
/// What this provider's driver supports, so the session UI renders the right controls.
/// </param>
/// <param name="CreateConfigView">
/// Builds the "add/edit profile" config view; the argument is the existing config JSON (edit) or <see langword="null"/> (add).
/// </param>
/// <param name="DefaultBaseUrl">
/// Pre-filled default base URL for this provider's config view, when it has one.
/// </param>
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
    /// the SDK-session mirror of <see cref="TtyProviderRegistration.Options"/>. Empty when it wants none.
    /// </summary>
    public IReadOnlyList<PluginSessionLaunchOption> Options { get; init; } = [];

    /// <summary>
    /// What sessions under this provider can run out of (AC-229) — the SDK mirror of
    /// <see cref="TtyProviderRegistration.UsageSignals"/>.
    /// </summary>
    /// <remarks>
    /// An SDK driver already reports its figures through <c>PluginSessionStatus</c> at each turn boundary; this
    /// says what those figures are. Empty (the default) when the provider measures nothing.
    /// </remarks>
    public IReadOnlyList<PluginUsageSignal> UsageSignals { get; init; } = [];

    /// <summary>
    /// An optional way to refresh <see cref="Options"/> with live values when the New-session dialog opens for a
    /// profile under this provider — Codex fills its Model choices from the app-server's <c>model/list</c> here.
    /// </summary>
    /// <remarks>
    /// The argument is the profile's opaque config JSON (whatever <see cref="CreateConfigView"/> round-trips); the
    /// result replaces the declared options for that dialog. The dialog renders the declared <see cref="Options"/>
    /// first and calls this in the background, so opening is never blocked; on <see langword="null"/>, a timeout,
    /// or any failure it keeps the declared options.
    /// </remarks>
    public Func<string, CancellationToken, Task<IReadOnlyList<PluginSessionLaunchOption>>>? ResolveOptionsAsync { get; init; }

    /// <summary>
    /// Answers whether a profile under this provider is logged in, from its opaque <c>ConfigJson</c> — the SDK
    /// mirror of <see cref="TtyProviderRegistration.IsLoggedIn"/>. Existence-only by contract (Iron Law #8).
    /// </summary>
    /// <remarks>
    /// Called synchronously on the UI thread, once per profile; a provider whose real check costs a subprocess
    /// answers from a cache and must never block here. <see langword="null"/> (the default) when the provider has
    /// no login concept, and the host treats such a profile as always ready.
    /// </remarks>
    public Func<string, bool>? IsLoggedIn { get; init; }

    /// <summary>
    /// Starts an in-app login attempt for a profile under this provider — the SDK mirror of
    /// <see cref="TtyProviderRegistration.StartLogin"/>. <see langword="null"/> (the default) when the provider offers no in-app login.
    /// </summary>
    public Func<string, CancellationToken, ILoginFlow>? StartLogin { get; init; }
}
