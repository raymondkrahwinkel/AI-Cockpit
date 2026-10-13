using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Projects;

namespace Cockpit.App.ViewModels;

// AC-1528: Home's own composer. What is typed here starts a new conversation and never goes to the assistant.
public sealed partial class SimpleHomeViewModel : ObservableObject, ISimpleComposerTarget
{
    private readonly CockpitViewModel _cockpit;
    private IVoicePushToTalkService? _voice;
    private VoiceOverlayCoordinator? _overlay;

    public SimpleHomeViewModel(CockpitViewModel cockpit)
    {
        _cockpit = cockpit;
        _cockpit.PropertyChanged += _OnCockpitPropertyChanged;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _inputText = string.Empty;

    // Null with `Folder` null too is "No project or folder" (the default).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetLabel))]
    private Project? _project;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetLabel))]
    private string? _folder;

    public string TargetLabel =>
        Project?.Name ?? (Folder is { } folder ? Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) : "No project or folder");

    public ObservableCollection<ImageAttachmentViewModel> PendingAttachments { get; } = [];

    public ObservableCollection<SimpleHomeProjectCard> ProjectCards { get; } = [];

    internal Func<DateTime> Now { get; set; } = () => DateTime.Now;

    internal string? FirstName { get; set; } = _WindowsFirstName();

    public string Greeting
    {
        get
        {
            var part = Now().Hour switch
            {
                < 12 => "Good morning",
                < 18 => "Good afternoon",
                _ => "Good evening",
            };
            return string.IsNullOrWhiteSpace(FirstName) ? part : $"{part}, {FirstName}";
        }
    }

    public bool VoiceEnabled => _cockpit.VoiceEnabled;

    // Raised each time Home is shown, so a window left open over noon does not keep saying good morning.
    internal void Refresh() => OnPropertyChanged(nameof(Greeting));

    [RelayCommand]
    private void ChooseProject(Project? project)
    {
        Project = project;
        Folder = null;
    }

    public void ChooseFolder(string folder)
    {
        Folder = folder;
        Project = null;
    }

    private bool CanSend => !string.IsNullOrWhiteSpace(InputText);

    // Cleared only once the conversation stands, so a start that fails leaves what was typed in the box.
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var images = PendingAttachments.Select(attachment => attachment.PngBytes).ToList();
        if (await _cockpit.StartSimpleConversationAsync(InputText.Trim(), Project, Folder, images) is null)
        {
            return;
        }

        InputText = string.Empty;
        foreach (var attachment in PendingAttachments)
        {
            attachment.Dispose();
        }

        PendingAttachments.Clear();
    }

    public Task<string?> InjectScreenshotAsync(byte[] screenshotPng)
    {
        PendingAttachments.Add(new ImageAttachmentViewModel(screenshotPng, _RemoveAttachment));
        return Task.FromResult<string?>(null);
    }

    public void InjectText(string text) =>
        InputText = string.IsNullOrEmpty(InputText) ? text : $"{InputText} {text}";

    // Wired by `VoicePushToTalkCoordinator`, which owns the one push-to-talk service and the pill.
    internal void UseDictation(IVoicePushToTalkService voice, VoiceOverlayCoordinator overlay)
    {
        _voice = voice;
        _overlay = overlay;
    }

    public bool BeginVoiceHold()
    {
        if (!VoiceEnabled || _voice?.BeginHold() != true)
        {
            return false;
        }

        _overlay?.SetPushToTalk(VoiceOverlayState.Listening);
        return true;
    }

    public async Task EndVoiceHoldAsync()
    {
        if (_voice is null)
        {
            return;
        }

        _overlay?.SetPushToTalk(VoiceOverlayState.Transcribing);
        try
        {
            var text = await _voice.EndHoldAsync();
            if (!string.IsNullOrEmpty(text))
            {
                InjectText(text);
            }

            _overlay?.SetPushToTalk(null);
        }
        catch (Exception ex)
        {
            _overlay?.ShowPushToTalkThenClear(VoiceOverlayState.Failed, $"Dictation failed — {ex.Message}", TimeSpan.FromSeconds(5));
        }
    }

    private void _RemoveAttachment(ImageAttachmentViewModel attachment)
    {
        PendingAttachments.Remove(attachment);
        attachment.Dispose();
    }

    private void _OnCockpitPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CockpitViewModel.VoiceEnabled))
        {
            OnPropertyChanged(nameof(VoiceEnabled));
        }
    }

    // The first name of the Windows account's display name; null where there is none, and the greeting drops its comma.
    private static string? _WindowsFirstName()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var size = 256u;
        var name = new StringBuilder((int)size);
        if (!GetUserNameExW(NameDisplay, name, ref size) || name.Length == 0)
        {
            return null;
        }

        var display = name.ToString();
        var given = display.Contains(',') ? display[(display.IndexOf(',') + 1)..] : display;
        return given.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    private const int NameDisplay = 3;

    [DllImport("secur32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserNameExW(int nameFormat, StringBuilder nameBuffer, ref uint size);
}
