namespace Cockpit.Plugin.Workflows.Model;

public enum WorkflowRunPhase
{
    Running,
    WaitingForPermission,
    Completed,
}

public enum WorkflowRunReason
{
    None,
    Disabled,
    AlreadyRunning,
    Missed,
    ConsentDenied,
}

public enum WorkflowApprovalOrigin
{
    None,
    Unknown,
    Local,
    Discord,
}
