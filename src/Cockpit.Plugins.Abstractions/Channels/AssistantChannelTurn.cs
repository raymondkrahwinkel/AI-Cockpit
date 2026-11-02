namespace Cockpit.Plugins.Abstractions.Channels;

public sealed record AssistantChannelTurn(AssistantTurnAdmission Admission, Task Started, Task<string> Completion)
{
    public static AssistantChannelTurn Refused(AssistantTurnAdmission admission) =>
        new(admission, Task.CompletedTask, Task.FromResult(string.Empty));
}
