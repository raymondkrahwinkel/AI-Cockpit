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
    public string StatusText => _reference.State switch { "completed" => "Done", "running" => "Running", "failed" => "Failed", "stopped" or "cancelled" or "interrupted" => "Stopped", _ => "Status unknown" };
    public string TypeText => string.IsNullOrWhiteSpace(_reference.AgentType) ? "unknown" : _reference.AgentType;
    public string StatusBrushKey => _reference.State switch
    {
        "running" => "CockpitStatusBackgroundBrush",
        "completed" => "CockpitStatusDoneBrush",
        "failed" => "CockpitStatusErrorBrush",
        _ => "CockpitTextFaintBrush",
    };
    public bool HasFilledStatus => _reference.State is "running" or "completed" or "failed";
    public double DotFillOpacity => HasFilledStatus ? 1 : 0;
    public string TimeText
    {
        get
        {
            var milliseconds = _reference.State == "running" && _reference.StartedAtUnixMilliseconds is { } start && start >= 0
                ? Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - start) : _reference.DurationMilliseconds;
            return milliseconds is >= 0 ? $"{milliseconds.Value / 60000}:{milliseconds.Value / 1000 % 60:00}" : "unknown";
        }
    }
    public string LastToolText => _reference.LastTool ?? "unknown";
    public string TokensText => _reference.Tokens is >= 0 ? SessionUsageMeter.FormatTokens(_reference.Tokens.Value) : "unknown";
    internal void RefreshTime() => OnPropertyChanged(nameof(TimeText));
    public void Update(BackgroundTask reference)
    {
        _reference = reference;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(TypeText));
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(StatusBrushKey));
        OnPropertyChanged(nameof(DotFillOpacity));
        OnPropertyChanged(nameof(LastToolText));
        OnPropertyChanged(nameof(TokensText));
    }
}
