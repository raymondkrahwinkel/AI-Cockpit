using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zyra.Voice.Core.Abstractions;
using Zyra.Voice.Core.Abstractions.Audio;

namespace Zyra.Voice.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, ITransientService
{
    private static readonly Core.Audio.AudioFormat Format = new();

    private readonly IAudioCaptureService? _captureService;
    private readonly IAudioPlaybackService? _playbackService;

    private readonly List<byte> _recordedPcm = [];
    private CancellationTokenSource? _recordingCancellation;

    [ObservableProperty]
    private string _status = "Ready.";

    public ClaudeSessionViewModel ClaudeSession { get; }

    // Parameterless constructor kept for the Avalonia previewer design-time context.
    public MainWindowViewModel()
    {
        ClaudeSession = new ClaudeSessionViewModel();
    }

    public MainWindowViewModel(IAudioCaptureService captureService, IAudioPlaybackService playbackService, ClaudeSessionViewModel claudeSession)
    {
        _captureService = captureService;
        _playbackService = playbackService;
        ClaudeSession = claudeSession;
    }

    [RelayCommand]
    private async Task RecordAsync()
    {
        if (_captureService is null)
        {
            return;
        }

        _recordedPcm.Clear();
        _recordingCancellation = new CancellationTokenSource();
        Status = "Recording...";

        try
        {
            await foreach (var frame in _captureService.CaptureAsync(Format, _recordingCancellation.Token))
            {
                _recordedPcm.AddRange(frame.ToArray());
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when StopRecording cancels the capture stream.
        }

        Status = $"Recorded {_recordedPcm.Count} bytes.";
    }

    [RelayCommand]
    private void StopRecording()
    {
        _recordingCancellation?.Cancel();
    }

    [RelayCommand]
    private async Task PlayAsync()
    {
        if (_playbackService is null || _recordedPcm.Count == 0)
        {
            Status = "Nothing recorded yet.";
            return;
        }

        Status = "Playing...";
        await _playbackService.PlayAsync(_recordedPcm.ToArray(), Format);
        Status = "Playback done.";
    }
}
