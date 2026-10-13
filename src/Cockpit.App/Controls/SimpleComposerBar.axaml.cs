using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Controls;

// AC-1528: the Simple composer's project chip, paperclip and mic — built once, for Home's box and a conversation's.
public partial class SimpleComposerBar : UserControl
{
    public static readonly StyledProperty<ISimpleComposerTarget?> TargetProperty =
        AvaloniaProperty.Register<SimpleComposerBar, ISimpleComposerTarget?>(nameof(Target));

    public static readonly StyledProperty<string?> ProjectLabelProperty =
        AvaloniaProperty.Register<SimpleComposerBar, string?>(nameof(ProjectLabel), "No project or folder");

    // Home only: under a conversation the chip says where it runs and cannot change it.
    public static readonly StyledProperty<bool> CanChooseProjectProperty =
        AvaloniaProperty.Register<SimpleComposerBar, bool>(nameof(CanChooseProject));

    private bool _holding;

    public SimpleComposerBar()
    {
        InitializeComponent();

        // Tunnel and handled-too: a Button claims the press for its own click, and the hold has to see it first.
        MicButton.AddHandler(PointerPressedEvent, _OnMicPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        MicButton.AddHandler(PointerReleasedEvent, _OnMicReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        MicButton.AddHandler(PointerCaptureLostEvent, _OnMicReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public ISimpleComposerTarget? Target
    {
        get => GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    public string? ProjectLabel
    {
        get => GetValue(ProjectLabelProperty);
        set => SetValue(ProjectLabelProperty, value);
    }

    public bool CanChooseProject
    {
        get => GetValue(CanChooseProjectProperty);
        set => SetValue(CanChooseProjectProperty, value);
    }

    public event EventHandler<RoutedEventArgs>? ProjectChipClicked;

    private void _OnProjectChipClick(object? sender, RoutedEventArgs e) => ProjectChipClicked?.Invoke(sender, e);

    private async void _OnAttachClick(object? sender, RoutedEventArgs e)
    {
        if (Target is not { } target || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Attach files", AllowMultiple = true });
        foreach (var path in files.Select(file => file.TryGetLocalPath()).OfType<string>())
        {
            await SimpleComposerAttachment.AttachAsync(target, path);
        }
    }

    internal void PressMic() => _holding = Target?.BeginVoiceHold() == true;

    internal async Task ReleaseMicAsync()
    {
        if (!_holding)
        {
            return;
        }

        _holding = false;
        await Target!.EndVoiceHoldAsync();
    }

    private void _OnMicPressed(object? sender, PointerPressedEventArgs e) => PressMic();

    private async void _OnMicReleased(object? sender, RoutedEventArgs e) => await ReleaseMicAsync();
}
