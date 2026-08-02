namespace Cockpit.Plugin.LocalCi.Execution;

// How a local run ended. Only two of these are a verdict; the rest are silence. A job this plugin refuses, a
// machine that cannot run one, an operator who said no, a run that was stopped, and a request that arrived while
// another run held the machine all mean "nothing was learned". Folding any of them into `Passed` is
// exactly the failure this feature exists to prevent.
internal enum LocalRunOutcome
{
    // act ran the job and it succeeded.
    Passed,

    // act ran the job and it failed.
    Failed,

    // The job was not attempted: the classification says it cannot run on this machine.
    Refused,

    // The job was not attempted: Docker or act is missing or not answering.
    CouldNotRun,

    // The job was not attempted: the operator did not approve running it.
    NotApproved,

    // The run was stopped before it reached a verdict.
    Cancelled,

    // The job was not attempted: another local run already holds the machine.
    AlreadyRunning,
}
