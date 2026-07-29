namespace Cockpit.Plugin.LocalCi.Execution;

/// <summary>
/// How a local run ended. Only two of these are a verdict; the rest are silence. A job this plugin refuses, a
/// machine that cannot run one, an operator who said no, a run that was stopped, and a request that arrived while
/// another run held the machine all mean "nothing was learned". Folding any of them into <see cref="Passed"/> is
/// exactly the failure this feature exists to prevent.
/// </summary>
internal enum LocalRunOutcome
{
    /// <summary>act ran the job and it succeeded.</summary>
    Passed,

    /// <summary>act ran the job and it failed.</summary>
    Failed,

    /// <summary>The job was not attempted: the classification says it cannot run on this machine.</summary>
    Refused,

    /// <summary>The job was not attempted: Docker or act is missing or not answering.</summary>
    CouldNotRun,

    /// <summary>The job was not attempted: the operator did not approve running it.</summary>
    NotApproved,

    /// <summary>The run was stopped before it reached a verdict.</summary>
    Cancelled,

    /// <summary>The job was not attempted: another local run already holds the machine.</summary>
    AlreadyRunning,
}
