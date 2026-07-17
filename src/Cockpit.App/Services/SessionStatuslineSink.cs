using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.App.Services;

/// <summary>
/// The live <see cref="ISessionStatuslineSink"/> (#AC-13): sets a session's statusline over the cockpit view-model
/// on behalf of the <c>cockpit-session</c> MCP server's <c>set_status</c> tool, marshalling to the UI thread. This
/// is the Infrastructure→App direction the orchestrator's <see cref="Core.Abstractions.Delegation.IDelegationService.TasksChanged"/>
/// also uses; registered in the App's DI so the endpoint host resolves it in place of the null sink.
/// </summary>
public sealed class SessionStatuslineSink(CockpitViewModel cockpit) : ISessionStatuslineSink, ISingletonService
{
    public Task<bool> SetStatuslineAsync(string paneId, string statusline)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return Task.FromResult(cockpit.SetSessionStatusline(paneId, statusline));
        }

        return Dispatcher.UIThread.InvokeAsync(() => cockpit.SetSessionStatusline(paneId, statusline)).GetTask();
    }
}
