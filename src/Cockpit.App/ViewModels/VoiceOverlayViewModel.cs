using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Voice;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Drives the floating voice-input pill (#34): one shared instance for the whole cockpit (single-user,
/// one hold at a time), whose <see cref="State"/> the pill's XAML binds its two rows'
/// (Listening/Transcribing) visibility to. While listening, <see cref="PushLevel"/> feeds captured
/// microphone levels into the scrolling <see cref="Bars"/> waveform so the pill shows that sound is
/// actually coming in (#34b). <c>VoicePushToTalkCoordinator</c> owns the transitions and the level feed.
/// </summary>
public partial class VoiceOverlayViewModel : ViewModelBase, ISingletonService
{
    private const int BarCount = 13;
    private const double MinBarHeight = 2;
    private const double MaxBarHeight = 20;

    private readonly WaveformLevelBuffer _waveform = new(BarCount);

    [ObservableProperty]
    private VoiceOverlayState _state = VoiceOverlayState.Hidden;

    /// <summary>What voice is waiting on, shown on the preparing row ("Downloading speech model — 412 MB").</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>
    /// 0..1 for the preparing row's bar, or null when the step has no total to measure against. The bar hides
    /// rather than guess: one that sits at an invented percentage is worse than a number that only counts up.
    /// </summary>
    [ObservableProperty]
    private double? _progress;

    public VoiceOverlayViewModel()
    {
        Bars = new ObservableCollection<VoiceOverlayBarViewModel>();
        for (var i = 0; i < BarCount; i++)
        {
            Bars.Add(new VoiceOverlayBarViewModel { Height = MinBarHeight });
        }
    }

    /// <summary>The waveform bars, oldest-to-newest left-to-right; the "Listening" row binds an ItemsControl to this.</summary>
    public ObservableCollection<VoiceOverlayBarViewModel> Bars { get; }

    public bool IsListening => State == VoiceOverlayState.Listening;

    /// <summary>The hold is not recording, and <see cref="StatusText"/> says why.</summary>
    public bool IsUnavailable => State == VoiceOverlayState.Unavailable;

    public bool IsPreparing => State == VoiceOverlayState.Preparing;

    public bool IsTranscribing => State == VoiceOverlayState.Transcribing;

    /// <summary>Whether the preparing row can show a bar at all — see <see cref="Progress"/>.</summary>
    public bool HasProgress => Progress is not null;

    /// <summary>The bar's width fraction as a plain double, since a XAML bar cannot bind a nullable.</summary>
    public double ProgressValue => Progress ?? 0;

    /// <summary>Feeds one captured microphone level (0..1) into the scrolling waveform. Call on the UI thread.</summary>
    public void PushLevel(double level)
    {
        // Ignore a level that lands after the hold already ended: the capture event fires on another
        // thread, so a frame marshaled in just after the pill left Listening (and _ResetBars ran) would
        // otherwise dirty the waveform, breaking the next hold's "start from silence".
        if (State != VoiceOverlayState.Listening)
        {
            return;
        }

        _waveform.Push(level);
        var levels = _waveform.Levels;
        for (var i = 0; i < Bars.Count; i++)
        {
            Bars[i].Height = MinBarHeight + (levels[i] * (MaxBarHeight - MinBarHeight));
        }
    }

    partial void OnProgressChanged(double? value)
    {
        OnPropertyChanged(nameof(HasProgress));
        OnPropertyChanged(nameof(ProgressValue));
    }

    partial void OnStateChanged(VoiceOverlayState value)
    {
        OnPropertyChanged(nameof(IsListening));
        OnPropertyChanged(nameof(IsUnavailable));
        OnPropertyChanged(nameof(IsPreparing));
        OnPropertyChanged(nameof(IsTranscribing));

        // A stale "Downloading speech model — 91%" behind the next hold's spinner would be a lie the moment
        // this one ends, so the text never outlives the two states that have something to say.
        if (value is not (VoiceOverlayState.Preparing or VoiceOverlayState.Unavailable))
        {
            StatusText = string.Empty;
            Progress = null;
        }

        // Leaving the listening state (transcribing or hidden) flattens the waveform so a fresh hold
        // starts from silence rather than the tail of the previous one.
        if (value != VoiceOverlayState.Listening)
        {
            _ResetBars();
        }
    }

    private void _ResetBars()
    {
        _waveform.Reset();
        foreach (var bar in Bars)
        {
            bar.Height = MinBarHeight;
        }
    }
}
