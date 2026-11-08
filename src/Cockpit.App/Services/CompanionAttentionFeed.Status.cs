using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.CompanionTools;
using Material.Icons;

namespace Cockpit.App.Services;

public sealed partial class CompanionAttentionFeed
{
    public const string StatusToolId = "cockpit.status";
    public event Action? StatusChanged;
    public IReadOnlyList<SessionPanelViewModel> StatusSessions { get; private set; } = [];
    public int RunningCount { get; private set; }
    public int BackgroundCount { get; private set; }
    public int WaitingCount => _cockpit.WaitingCount;
    internal CockpitViewModel Cockpit => _cockpit;

    public CompanionToolRegistration CreateStatusTool() =>
        new(StatusToolId, "Status", _ => new CompanionStatusPanel(this))
        {
            Tooltip = "Cockpit status and selected agent",
            IconKind = MaterialIconKind.CircleOutline,
        };

    internal void RefreshStatus(List<SessionPanelViewModel> sessions)
    {
        foreach (var gone in _watched.Where(session => !sessions.Contains(session)).ToList())
        {
            gone.PropertyChanged -= _OnSessionChanged;
            _watched.Remove(gone);
        }
        StatusSessions = sessions;
        RunningCount = sessions.Count(session => session.SessionStatus is SessionStatus.Busy or SessionStatus.WorkingBackground);
        BackgroundCount = sessions.Count(session => !_cockpit.Sessions.Contains(session)
            && session.SessionStatus is SessionStatus.Busy or SessionStatus.WorkingBackground)
            + _cockpit.DelegatedTasks.Tasks.Count(task => task.Status == Core.Delegation.DelegatedTaskStatus.Running);
        StatusChanged?.Invoke();
    }
}
