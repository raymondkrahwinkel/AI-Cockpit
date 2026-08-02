using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Shortcuts;
using Cockpit.Core.Shortcuts;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Shortcuts;

// Persists the app-action shortcuts under the `shortcuts` section of `cockpit.json` (same
// file/pattern as the other settings stores), reading-modifying-writing the whole file so sibling sections
// stay intact. When nothing was ever saved, `LoadAsync` returns
// `ShortcutSettings.Default`.
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
            // Whether the operator has since bound the session switch themselves is a question about what is
            // *saved*, so it has to read the raw persisted gestures — not ToDomain(), which back-fills every
            // catalog action with its default and would therefore always report both session gestures present the
            // moment any shortcuts section exists (AC-35). The DTO's keys are the ShortcutAction names as written.
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

    // Toggle zoom used to default to Ctrl+B. One modifier is not enough to survive a focused terminal — and a
    // zoomed pane is exactly when the terminal has focus — so the default moved to a two-modifier chord that
    // passes the gate (AC-401). Any other saved gesture is left alone.
    //
    // It matches on the value, not on when it was written, and it runs on every load — so it cannot tell the old
    // default apart from a Ctrl+B the operator deliberately chose, and will take that one too, every start. That
    // is the same shape as `_MigrateSessionSwitchOffArrowKeys` and accepted here for the same
    // practical reason: a one-shot would need a marker in the config, and Ctrl+B for zoom is a gesture that does
    // not work where zoom is used. Anyone who wants the old key back has every other free gesture.
    private static ShortcutSettings _MigrateZoomOffCtrlB(ShortcutSettings settings) =>
        settings.Gestures.TryGetValue(ShortcutAction.ToggleZoom, out var zoom) && zoom == "Ctrl+B"
            ? settings.With(ShortcutAction.ToggleZoom, ShortcutCatalog.DefaultGesture(ShortcutAction.ToggleZoom))
            : settings;

    // The session switch used to default to Ctrl+Up / Ctrl+Down; those are now the spatial pane-focus gestures
    // and the session switch has moved to Ctrl+Shift+Up/Down (AC-31). A config that saved the old defaults
    // explicitly would otherwise double-bind Ctrl+Up/Down with the new "focus pane up/down", so migrate exactly
    // those two values to the new gesture. A gesture the operator changed to anything else is left alone, and a
    // config that never saved them keeps taking the (now Shift+) catalog default. Idempotent: after the operator
    // next saves, the shortcuts section holds the new gesture and this matches nothing.
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

    // The session switch used to be its own setting (a master on/off plus a modifier, arrowed by a hard-coded
    // handler); it is now two ordinary shortcuts. A config written by an older build still carries that section,
    // so translate it into gestures rather than silently resetting someone's choice to Ctrl. The first save
    // writes the result into the shortcuts section, after which this is a no-op.
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
