using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewModels;

public sealed class ChildAgentViewModel(SessionViewModel owner, BackgroundTask reference) : ObservableObject
{
    public SessionViewModel Owner { get; } = owner;
    private RelayCommand? _openCommand;
    public RelayCommand OpenCommand => _openCommand ??= new(() => Owner.OpenChild(this));
    private BackgroundTask _reference = reference;
    public BackgroundTask Reference => _reference;
    public string Title => string.IsNullOrWhiteSpace(_reference.Description) ? _reference.AgentId ?? _reference.TaskId : _reference.Description;
    public string Detail => $"{Owner.UsageProviderId ?? "Provider"} · {_reference.State}";
    public string StatusText => _reference.State;
    public string LastToolText => _reference.LastTool ?? "unknown";
    public string TokensText => _reference.Tokens?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
    public void Update(BackgroundTask reference)
    {
        _reference = reference;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(LastToolText));
        OnPropertyChanged(nameof(TokensText));
    }
}
