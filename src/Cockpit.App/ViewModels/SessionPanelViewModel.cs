using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels;

/// <summary>
/// The surface every cockpit session panel shares regardless of mode (SDK chat or TTY terminal):
/// the sidebar/overview title, selection, coarse status, and profile label, plus disposal. Lets
/// <see cref="CockpitViewModel"/> manage a mixed collection of <see cref="ClaudeSessionViewModel"/>
/// (SDK) and <see cref="ClaudeTtyViewModel"/> (TTY) panels through one type.
/// </summary>
public abstract partial class SessionPanelViewModel : ViewModelBase, IAsyncDisposable
{
    /// <summary>Display title for this session's sidebar/grid panel, e.g. "Claude 1". Set by <see cref="CockpitViewModel"/>.</summary>
    [ObservableProperty]
    private string _title = "Claude";

    /// <summary>True while this is <see cref="CockpitViewModel.SelectedSession"/> — drives the sidebar's active-item highlight. Set by <see cref="CockpitViewModel"/>.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Coarse status for the sidebar/grid overview — see <see cref="ViewModels.SessionStatus"/>.</summary>
    [ObservableProperty]
    private SessionStatus _sessionStatus = SessionStatus.Idle;

    /// <summary>Label of the profile the running session was started under, once known.</summary>
    [ObservableProperty]
    private string? _activeProfileLabel;

    /// <summary>
    /// True while a close is awaiting confirmation for this panel, so its sidebar row shows an inline
    /// "Close? / Keep" prompt rather than dropping a busy session on a single click (mirrors the
    /// Manage-profiles remove confirm, L11).
    /// </summary>
    [ObservableProperty]
    private bool _isConfirmingClose;

    /// <summary>
    /// True when closing would interrupt a running turn, so the close asks first. Idle/waiting/done
    /// sessions close on a single click.
    /// </summary>
    public bool RequiresCloseConfirmation => SessionStatus == SessionStatus.Busy;

    /// <summary>Short human-readable label for <see cref="SessionStatus"/>, for the sidebar status row.</summary>
    public string SessionStatusLabel => SessionStatus switch
    {
        SessionStatus.Busy => "Busy",
        SessionStatus.WaitingForInput => "Waiting for input",
        SessionStatus.NeedsAttention => "Needs attention",
        SessionStatus.Done => "Done",
        _ => "Idle",
    };

    /// <summary>Theme brush resource key for the status dot — resolved in the view via a converter.</summary>
    public string SessionStatusBrushKey => SessionStatus switch
    {
        SessionStatus.Busy => "CockpitStatusBusyBrush",
        SessionStatus.WaitingForInput or SessionStatus.NeedsAttention => "CockpitStatusWaitingBrush",
        SessionStatus.Done => "CockpitStatusDoneBrush",
        _ => "CockpitTextFaintBrush",
    };

    /// <summary>Keeps the derived status label/brush in sync whenever <see cref="SessionStatus"/> changes.</summary>
    partial void OnSessionStatusChanged(SessionStatus value)
    {
        OnPropertyChanged(nameof(SessionStatusLabel));
        OnPropertyChanged(nameof(SessionStatusBrushKey));
        OnPropertyChanged(nameof(RequiresCloseConfirmation));
    }

    public abstract ValueTask DisposeAsync();
}
