namespace Cockpit.Plugin.Autopilot;

// Where an `AutopilotTemplate` came from (AC-189), which decides whether the operator may edit or delete it.
// Builtin and Plugin templates can be edited (kept as an override) but never deleted — the original
// registration stays the source. A User template is the operator's own, so it is theirs to both edit and delete.
internal enum AutopilotTemplateOrigin
{
    // Ships with the Autopilot plugin itself.
    Builtin,

    // Contributed by another plugin at run time.
    Plugin,

    // Authored by the operator.
    User,
}
