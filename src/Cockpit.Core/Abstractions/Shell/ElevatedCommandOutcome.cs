namespace Cockpit.Core.Abstractions.Shell;

// AC-1335: how one elevated command ended. Declined is the operator's "No" on the UAC prompt, never an error.
public enum ElevatedCommandOutcome
{
    Completed,
    Declined,
    TimedOut,
    Failed,
}
