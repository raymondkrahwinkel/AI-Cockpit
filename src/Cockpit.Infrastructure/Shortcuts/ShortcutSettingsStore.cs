using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Shortcuts;
using Cockpit.Core.Shortcuts;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Shortcuts;

/// <summary>
/// Persists the app-action shortcuts under the <c>shortcuts</c> section of <c>cockpit.json</c> (same
/// file/pattern as the other settings stores), reading-modifying-writing the whole file so sibling sections
/// stay intact. When nothing was ever saved, <see cref="LoadAsync"/> returns
/// <see cref="ShortcutSettings.Default"/>.
/// </summary>
internal sealed class ShortcutSettingsStore : IShortcutSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public ShortcutSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    /// <summary>Test seam: point the store at an arbitrary config file path.</summary>
    internal ShortcutSettingsStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<ShortcutSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        var settings = configFile?.Shortcuts?.ToDomain() ?? ShortcutSettings.Default;

        if (configFile?.SessionSwitching is { } legacySessionSwitch)
        {
            // Whether the operator has since bound the session switch themselves is a question about what is
            // *saved*, not about the in-memory settings: ShortcutSettings.Default seeds a gesture for every
            // action, so asking the merged object would always answer "already set" and the legacy value would
            // never carry over.
            var alreadyRebound = configFile.Shortcuts?.ToDomain() is { } saved &&
                                 (saved.Gestures.ContainsKey(ShortcutAction.PreviousSession) ||
                                  saved.Gestures.ContainsKey(ShortcutAction.NextSession));

            if (!alreadyRebound)
            {
                settings = _CarryOverLegacySessionSwitch(settings, legacySessionSwitch);
            }
        }

        return _MigrateSessionSwitchOffArrowKeys(settings);
    }

    /// <summary>
    /// The session switch used to default to Ctrl+Up / Ctrl+Down; those are now the spatial pane-focus gestures
    /// and the session switch has moved to Ctrl+Shift+Up/Down (AC-31). A config that saved the old defaults
    /// explicitly would otherwise double-bind Ctrl+Up/Down with the new "focus pane up/down", so migrate exactly
    /// those two values to the new gesture. A gesture the operator changed to anything else is left alone, and a
    /// config that never saved them keeps taking the (now Shift+) catalog default. Idempotent: after the operator
    /// next saves, the shortcuts section holds the new gesture and this matches nothing.
    /// </summary>
    private static ShortcutSettings _MigrateSessionSwitchOffArrowKeys(ShortcutSettings settings)
    {
        var migrated = settings;
        if (settings.Gestures.TryGetValue(ShortcutAction.PreviousSession, out var previous) && previous == "Ctrl+Up")
        {
            migrated = migrated.With(ShortcutAction.PreviousSession, "Ctrl+Shift+Up");
        }

        if (settings.Gestures.TryGetValue(ShortcutAction.NextSession, out var next) && next == "Ctrl+Down")
        {
            migrated = migrated.With(ShortcutAction.NextSession, "Ctrl+Shift+Down");
        }

        return migrated;
    }

    /// <summary>
    /// The session switch used to be its own setting (a master on/off plus a modifier, arrowed by a hard-coded
    /// handler); it is now two ordinary shortcuts. A config written by an older build still carries that section,
    /// so translate it into gestures rather than silently resetting someone's choice to Ctrl. The first save
    /// writes the result into the shortcuts section, after which this is a no-op.
    /// </summary>
    private static ShortcutSettings _CarryOverLegacySessionSwitch(ShortcutSettings settings, SessionSwitchSettingsEntry legacy)
    {
        if (!legacy.IsEnabled)
        {
            return settings
                .With(ShortcutAction.PreviousSession, string.Empty)
                .With(ShortcutAction.NextSession, string.Empty);
        }

        var modifier = legacy.Modifier switch
        {
            LegacySessionSwitchModifier.CtrlAlt => "Ctrl+Alt",
            LegacySessionSwitchModifier.Alt => "Alt",
            _ => "Ctrl",
        };

        return settings
            .With(ShortcutAction.PreviousSession, $"{modifier}+Up")
            .With(ShortcutAction.NextSession, $"{modifier}+Down");
    }

    public Task SaveAsync(ShortcutSettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.Shortcuts = ShortcutSettingsEntry.FromDomain(settings),
            cancellationToken);
}
