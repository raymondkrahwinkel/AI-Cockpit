namespace Cockpit.Plugin.Autopilot;

// When the epic gate runs (AC-1341): every K merged subs (EpicWorkflow §5) or once every sub is merged (§14).
internal enum AutopilotEpicGateKind
{
    Mid,
    End,
}
