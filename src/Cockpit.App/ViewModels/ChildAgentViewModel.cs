using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewModels;

public sealed class ChildAgentViewModel(SessionPanelViewModel owner, BackgroundTask reference) : ObservableObject
{
    public SessionPanelViewModel Owner { get; } = owner;
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
            long? milliseconds = _reference.State == "running" && _reference.StartedAtUnixMilliseconds is { } start && start >= 0
                ? Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - start) : _reference.DurationMilliseconds;
            return milliseconds is >= 0 ? $"{milliseconds.Value / 60000}:{milliseconds.Value / 1000 % 60:00}" : "unknown";
        }
    }
    public string LastToolText => _reference.LastTool ?? "unknown";
    public string TokensText => _reference.Tokens is >= 0 ? SessionUsageMeter.FormatTokens(_reference.Tokens.Value) : "unknown";
    public bool IsFinished => _reference.State == "completed";
    public bool IsShownInSidebar => !IsFinished || Owner.ShowsFinishedChildAgents;
    public double SidebarOpacity => IsFinished ? 0.62 : 1;
    public string SidebarDetail
    {
        get
        {
            string lead = _reference.State == "running" ? _reference.LastTool ?? "" : StatusText;
            string time = TimeText == "unknown" ? "" : TimeText;
            return lead.Length > 0 && time.Length > 0 ? $"{lead} · {time}" : lead + time;
        }
    }
    public bool IsOpenInTranscript => ReferenceEquals(Owner.ChildTranscript?.Child, this);
    internal void RefreshSidebarVisibility()
    {
        OnPropertyChanged(nameof(IsShownInSidebar));
        OnPropertyChanged(nameof(IsOpenInTranscript));
    }
    internal void RefreshTime()
    {
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(SidebarDetail));
    }
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
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(IsShownInSidebar));
        OnPropertyChanged(nameof(SidebarOpacity));
        OnPropertyChanged(nameof(SidebarDetail));
    }
}
