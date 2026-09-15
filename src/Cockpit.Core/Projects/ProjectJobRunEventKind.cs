namespace Cockpit.Core.Projects;

public enum ProjectJobRunEventKind
{
    Started,

    // The agent's own account of the work so far, in the operator's terms. A claim, never a measurement.
    Progress,
}
