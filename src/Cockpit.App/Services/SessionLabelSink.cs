using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.App.Services;

// AC-1013: Live `ISessionLabelSink` (#AC-13, #AC-312), marshals statusline/name updates from the MCP `set_status` tool to the UI thread via the cockpit view-model.
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
