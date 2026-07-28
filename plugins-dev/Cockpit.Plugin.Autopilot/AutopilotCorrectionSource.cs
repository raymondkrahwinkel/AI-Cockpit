namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// Who set a step's <see cref="AutopilotCorrectionKind"/> (AC-347) — so a manual adjustment stays visible as an
/// adjustment rather than reading as if the run itself had classified it that way. <c>IPluginStorage</c> serializes
/// this enum as its plain integer (no <c>JsonStringEnumConverter</c>), so persisted history stores the numbers below,
/// not the names — a new value must be added last, never inserted between existing ones, or old history would
/// silently read back as classified by someone else.
/// </summary>
internal enum AutopilotCorrectionSource
{
    /// <summary>Classified by <see cref="AutopilotCorrection.Classify"/> at settle — the default.</summary>
    Automatic = 0,

    /// <summary>The operator picked this classification by hand, overriding whatever was classified automatically.</summary>
    Operator,
}
