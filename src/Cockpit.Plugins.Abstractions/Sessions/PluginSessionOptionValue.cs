namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// One value a <see cref="PluginSessionOptionDescriptor"/> accepts (AC-649).
/// </summary>
/// <param name="Value">The raw value the driver receives in its options map — never the label.</param>
/// <param name="Label">What a human reads for it; the value itself when the provider has no friendlier word.</param>
public sealed record PluginSessionOptionValue(string Value, string Label);
