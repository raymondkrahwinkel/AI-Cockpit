using Cockpit.Core.SessionSwitching;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// On-disk shape of <see cref="SessionSwitchSettings"/> in the <c>sessionSwitching</c> section of
/// <c>cockpit.json</c>. Stores the modifier as its enum name so the JSON stays human-editable.
/// </summary>
internal sealed class SessionSwitchSettingsEntry
{
    public bool IsEnabled { get; set; } = true;

    public SessionSwitchModifier Modifier { get; set; } = SessionSwitchSettings.DefaultModifier;

    public static SessionSwitchSettingsEntry FromDomain(SessionSwitchSettings settings) => new()
    {
        IsEnabled = settings.IsEnabled,
        Modifier = settings.Modifier,
    };

    public SessionSwitchSettings ToDomain() => new()
    {
        IsEnabled = IsEnabled,
        Modifier = Modifier,
    };
}
