namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// One session option a provider understands, in the provider's own vocabulary (AC-649) — the schema behind the
/// otherwise opaque <c>OptionDefaults</c>/options map, so a consumer can read what a key means and which values it
/// takes instead of knowing it by heart. Claude declares <c>permission-mode</c>/<c>model</c>/<c>effort</c>, Codex
/// declares <c>sandbox</c>; a provider that reads no options declares none. Unlike
/// <see cref="PluginSessionLaunchOption"/>, which asks the New-session dialog to render a control, this only states
/// what exists — it renders nothing.
/// </summary>
/// <param name="Key">
/// The options-map key, e.g. <see cref="WellKnownPluginSessionOptions.Model"/> or a private one like <c>effort</c>.
/// </param>
/// <param name="Label">
/// What a human reads for the option itself.
/// </param>
/// <param name="KnownValues">
/// The values it takes, or <see langword="null"/> for a free-form option (a model id, a path).
/// </param>
/// <param name="CurrentValueHint">
/// The value that applies when nobody sets one, when the provider knows it.
/// </param>
public sealed record PluginSessionOptionDescriptor(
    string Key,
    string Label,
    IReadOnlyList<PluginSessionOptionValue>? KnownValues = null,
    string? CurrentValueHint = null);
