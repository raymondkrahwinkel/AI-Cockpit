namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// Where an <see cref="AutopilotTemplate"/> came from (AC-189), which decides whether the operator may edit or delete
/// it. A <see cref="Builtin"/> ships with the Autopilot plugin; a <see cref="Plugin"/> is contributed by another
/// plugin at run time; a <see cref="User"/> template was authored by the operator. Builtin and Plugin templates can be
/// edited (the edit is kept as an override) but never deleted — the original registration stays the source. A User
/// template is the operator's own, so it is theirs to both edit and delete.
/// </summary>
internal enum AutopilotTemplateOrigin
{
    /// <summary>Ships with the Autopilot plugin itself.</summary>
    Builtin,

    /// <summary>Contributed by another plugin at run time.</summary>
    Plugin,

    /// <summary>Authored by the operator.</summary>
    User,
}
