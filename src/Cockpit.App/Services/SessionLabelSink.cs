using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.App.Services;

/// <summary>
/// The live <see cref="ISessionLabelSink"/> (#AC-13, #AC-312): sets a session's statusline and proposes its name over
/// the cockpit view-model on behalf of the <c>cockpit-session</c> MCP server's <c>set_status</c> tool, marshalling to
/// the UI thread. This is the Infrastructure→App direction the orchestrator's
/// <see cref="Core.Abstractions.Delegation.IDelegationService.TasksChanged"/> also uses; registered in the App's DI so
/// the endpoint host resolves it in place of the null sink.
/// </summary>
public sealed class SessionLabelSink(CockpitViewModel cockpit) : ISessionLabelSink, ISingletonService
{
    public Task<bool> SetStatuslineAsync(string paneId, string statusline) =>
        _OnUiThread(() => cockpit.SetSessionStatusline(paneId, statusline));

    public Task<bool> SuggestNameAsync(string paneId, string name) =>
        _OnUiThread(() => cockpit.SuggestSessionName(paneId, name));

    private static Task<bool> _OnUiThread(Func<bool> mutate) =>
        Dispatcher.UIThread.CheckAccess()
            ? Task.FromResult(mutate())
            : Dispatcher.UIThread.InvokeAsync(mutate).GetTask();
}
