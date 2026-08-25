using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Shortcuts;
using Cockpit.Core.Shortcuts;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Shortcuts;

// Persists the app-action shortcuts under the `shortcuts` section of `cockpit.json` (same
// file/pattern as the other settings stores), reading-modifying-writing the whole file so sibling
// sections stay intact. When nothing was ever saved, `LoadAsync` returns `ShortcutSettings.Default`.
internal sealed class ShortcutSettingsStore : IShortcutSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public ShortcutSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path.
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
            // Whether the operator has since rebound the session switch must read the raw persisted gestures,
            // not ToDomain() — that back-fills every catalog action with its default and would always report
            // both session gestures present the moment any shortcuts section exists (AC-35).
            var savedGestures = configFile.Shortcuts?.Gestures;
            var alreadyRebound = savedGestures is not null &&
                                 (savedGestures.ContainsKey(nameof(ShortcutAction.PreviousSession)) ||
                                  savedGestures.ContainsKey(nameof(ShortcutAction.NextSession)));

            if (!alreadyRebound)
            {
                settings = _CarryOverLegacySessionSwitch(settings, legacySessionSwitch);
            }
        }

        return _MigrateZoomOffCtrlB(_MigrateSessionSwitchOffArrowKeys(settings));
    }

    // Toggle zoom used to default to Ctrl+B; a single modifier can't survive a focused terminal, so the default
    // moved to a two-modifier chord (AC-401). Matches on value, not on when it was written, so it also takes a
    // Ctrl+B the operator deliberately chose, every start — accepted since a one-shot would need a config marker.
    private static ShortcutSettings _MigrateZoomOffCtrlB(ShortcutSettings settings) =>
        settings.Gestures.TryGetValue(ShortcutAction.ToggleZoom, out var zoom) && zoom == "Ctrl+B"
            ? settings.With(ShortcutAction.ToggleZoom, ShortcutCatalog.DefaultGesture(ShortcutAction.ToggleZoom))
            : settings;

    // The session switch used to default to Ctrl+Up/Down; those are now pane-focus gestures and session switch
    // moved to Ctrl+Shift+Up/Down (AC-31). Migrates only configs that saved the old defaults explicitly, to
    // avoid double-binding with "focus pane up/down"; idempotent, since a save rewrites the new gesture.
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

    // The session switch used to be its own on/off-plus-modifier setting; it is now two ordinary shortcuts.
    // A config from an older build still carries that section, so translate it rather than silently resetting
    // someone's choice to Ctrl. First save writes the result into the shortcuts section, after which this is a no-op.
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
