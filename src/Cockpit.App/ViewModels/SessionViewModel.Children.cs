using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewModels;

public partial class SessionViewModel
{
    public override bool CanPrepareChildRequest => CanTakeAPrompt && IsLinkUp;

    internal override bool PrepareChildRequest(ChildAgentViewModel child, BackgroundTask reference, string request)
    {
        if (!CanPrepareChildRequest || !IsCurrentChild(child, reference))
        {
            return false;
        }
        ReturnToParentCommand.Execute(null);
        InputText = request;
        return true;
    }

    protected override Task<ChildTranscriptPage> ReadChildAsync(BackgroundTask reference, string? cursor, CancellationToken cancellationToken) =>
        _control.ReadChildTranscriptAsync(reference.AgentId ?? reference.TaskId, reference.RunId, cursor, cancellationToken);
}
