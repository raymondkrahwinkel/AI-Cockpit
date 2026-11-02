namespace Cockpit.Core.Sessions;

public sealed record ReadOnlyTurnReceipt(ReadOnlyTurnAdmission Admission, Task Started, Task<string> Completion)
{
    public static ReadOnlyTurnReceipt Refused(ReadOnlyTurnAdmission admission) =>
        new(admission, Task.CompletedTask, Task.FromResult(string.Empty));
}
