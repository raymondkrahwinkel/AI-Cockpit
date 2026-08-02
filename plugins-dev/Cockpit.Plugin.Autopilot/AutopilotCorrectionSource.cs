namespace Cockpit.Plugin.Autopilot;

// Who set a step's `AutopilotCorrectionKind` (AC-347) — so a manual adjustment stays visible as an
// adjustment rather than reading as if the run itself had classified it that way. `IPluginStorage` serializes
// this enum as its plain integer (no `JsonStringEnumConverter`), so persisted history stores the numbers below,
// not the names — a new value must be added last, never inserted between existing ones, or old history would
// silently read back as classified by someone else.
internal enum AutopilotCorrectionSource
{
    // Classified by `AutopilotCorrection.Classify` at settle — the default.
    Automatic = 0,

    // The operator picked this classification by hand, overriding whatever was classified automatically.
    Operator,
}
