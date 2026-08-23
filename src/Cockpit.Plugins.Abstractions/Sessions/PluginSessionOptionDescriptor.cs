namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// One session option a provider understands, in its own vocabulary (AC-649) — the schema behind the otherwise
/// opaque <c>OptionDefaults</c>/options map. Unlike <see cref="PluginSessionLaunchOption"/>, which asks the
/// New-session dialog to render a control, this only states what exists.
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
