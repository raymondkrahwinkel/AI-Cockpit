using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewModels;

public partial class SessionViewModel
{
    protected override Task<ChildTranscriptPage> ReadChildAsync(BackgroundTask reference, string? cursor, CancellationToken cancellationToken) =>
        _control.ReadChildTranscriptAsync(reference.AgentId ?? reference.TaskId, reference.RunId, cursor, cancellationToken);
}
