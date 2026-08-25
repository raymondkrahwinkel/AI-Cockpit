namespace Cockpit.Plugin.Autopilot;

// Who set a step's `AutopilotCorrectionKind` (AC-347), so a manual adjustment stays visible as such.
// Persisted as the plain integer (no `JsonStringEnumConverter`), so a new value must be added last,
// never inserted between existing ones, or old history silently reads back as classified by someone else.
internal enum AutopilotCorrectionSource
{
    // Classified by `AutopilotCorrection.Classify` at settle — the default.
    Automatic = 0,

    // The operator picked this classification by hand, overriding whatever was classified automatically.
    Operator,
}
