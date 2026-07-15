namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// A start default a session provider wants the New-session dialog to ask about — Codex has a sandbox and a
/// model. The provider declares them on its <see cref="SessionProviderRegistration.Options"/>; the host renders
/// them and hands the answers back to <see cref="IPluginSessionDriver.StartAsync(string?, string?, string?, System.Collections.Generic.IReadOnlyDictionary{string, string}?, System.Threading.CancellationToken)"/>.
/// The host never learns what any of them mean — the SDK-session mirror of <see cref="PluginTtyLaunchOption"/>,
/// so a dialog can serve session providers it has never heard of.
/// </summary>
/// <param name="Key">How the answer comes back in the driver's options map.</param>
/// <param name="Label">What the operator reads.</param>
/// <param name="Choices">The values on offer. Empty means free text.</param>
/// <param name="DefaultValue">Pre-selected, or <see langword="null"/> to leave the option unset (the provider's own default then applies).</param>
public sealed record PluginSessionLaunchOption(
    string Key,
    string Label,
    IReadOnlyList<string> Choices,
    string? DefaultValue = null);
