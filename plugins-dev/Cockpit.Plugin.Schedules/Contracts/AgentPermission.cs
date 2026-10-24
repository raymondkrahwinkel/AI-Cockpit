namespace Cockpit.Plugin.Schedules.Contracts;

// What an agent prompt's run may do. Write lets it change files, so a run nobody started by hand asks first.
public enum AgentPermission
{
    ReadOnly,
    Write,
}
