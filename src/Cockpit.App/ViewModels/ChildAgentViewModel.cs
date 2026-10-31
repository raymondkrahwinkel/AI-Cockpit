using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewModels;

public sealed class ChildAgentViewModel(SessionViewModel owner, BackgroundTask reference) : ObservableObject
{
    public SessionViewModel Owner { get; } = owner;
    private BackgroundTask _reference = reference;
    public BackgroundTask Reference => _reference;
    public string Title => string.IsNullOrWhiteSpace(_reference.Description) ? _reference.AgentId ?? _reference.TaskId : _reference.Description;
    public string Detail => $"{Owner.UsageProviderId ?? "Provider"} · {_reference.State} · Read-only";
    public void Update(BackgroundTask reference)
    {
        _reference = reference;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Detail));
    }
}
