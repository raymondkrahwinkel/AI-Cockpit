namespace Cockpit.Core.SessionSwitching;

/// <summary>
/// User-configurable keyboard-switch settings, persisted under the <c>sessionSwitching</c> section
/// of <c>cockpit.json</c> (same store pattern as the profiles and notifications). Holds the master
/// on/off switch and which modifier arms the arrow-key switch gesture.
/// </summary>
public sealed record SessionSwitchSettings
{
    /// <summary>Default modifier for the switch gesture: Ctrl + arrow.</summary>
    public const SessionSwitchModifier DefaultModifier = SessionSwitchModifier.Ctrl;

    /// <summary>Master switch. When false, the arrow-key session switch is inactive.</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>Modifier that arms the switch gesture. Defaults to <see cref="DefaultModifier"/>.</summary>
    public SessionSwitchModifier Modifier { get; init; } = DefaultModifier;
}
