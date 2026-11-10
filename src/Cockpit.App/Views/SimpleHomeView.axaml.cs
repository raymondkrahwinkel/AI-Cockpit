using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

// AC-1528: the Simple stand's Home. Its box starts a conversation of its own; it is never the assistant's composer.
public partial class SimpleHomeView : UserControl
{
    public SimpleHomeView()
    {
        InitializeComponent();
        HomeInput.AddHandler(KeyDownEvent, _OnInputKeyDown, RoutingStrategies.Tunnel);
        HomeInput.AddHandler(KeyUpEvent, _OnInputKeyUp, RoutingStrategies.Tunnel);
        HomeMentionPopup.EmptyActionCommand = new AsyncRelayCommand(async () =>
        {
            if (Cockpit is { } cockpit)
            {
                cockpit.SimpleHome.MentionPicker.Dismiss();
                await _ChooseFolderAsync(cockpit.SimpleHome);
            }
        });
    }

    // Set by the key-down tunnel: a key this view consumed is not typing, so its key-up must not re-read the @ token.
    private bool _keyDownHandled;

    private CockpitViewModel? Cockpit => DataContext as CockpitViewModel;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible)
        {
            Dispatcher.UIThread.Post(() => HomeInput.Focus());
        }
    }

    // Enter sends and Shift+Enter breaks the line, as in every composer here; the dictation key holds to talk.
    private void _OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        _OnInputKeyDownCore(e);
        _keyDownHandled = e.Handled;
    }

    private void _OnInputKeyDownCore(KeyEventArgs e)
    {
        if (Cockpit is not { } cockpit)
        {
            return;
        }

        if (MentionComposerKeys.Handle(cockpit.SimpleHome.MentionPicker, HomeInput, e))
        {
            return;
        }

        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            if (cockpit.SimpleHome.SendCommand.CanExecute(null))
            {
                cockpit.SimpleHome.SendCommand.Execute(null);
            }
        }
        else if (cockpit.IsAudioAvailable
            && PushToTalkKeyGate.ShouldHandleLocally(e.Key, cockpit.VoicePushToTalkKeyName, cockpit.VoiceGlobalPushToTalk))
        {
            e.Handled = true;
            HomeComposerBar.PressMic();
        }
    }

    private async void _OnInputKeyUp(object? sender, KeyEventArgs e)
    {
        // A paste is not typing: Ctrl+V must not open the list for an @ that arrived with the clipboard.
        if (Cockpit is { } home && !_keyDownHandled && !e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            home.SimpleHome.MentionPicker.OnTextChanged(HomeInput.Text ?? string.Empty, HomeInput.CaretIndex);
        }

        if (Cockpit is { IsAudioAvailable: true } cockpit
            && PushToTalkKeyGate.ShouldHandleLocally(e.Key, cockpit.VoicePushToTalkKeyName, cockpit.VoiceGlobalPushToTalk))
        {
            e.Handled = true;
            await HomeComposerBar.ReleaseMicAsync();
        }
    }

    private void _OnProjectChipClicked(object? sender, RoutedEventArgs e)
    {
        if (Cockpit is not { } cockpit || sender is not Control chip)
        {
            return;
        }

        var home = cockpit.SimpleHome;
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        menu.Items.Add(new MenuItem { Header = "No project or folder", Command = home.ChooseProjectCommand });
        foreach (var project in cockpit.Projects.Projects)
        {
            menu.Items.Add(new MenuItem { Header = project.Name, Command = home.ChooseProjectCommand, CommandParameter = project });
        }

        var chooseFolder = new MenuItem { Header = "Choose folder…" };
        chooseFolder.Click += async (_, _) => await _ChooseFolderAsync(home);
        menu.Items.Add(new Separator());
        menu.Items.Add(chooseFolder);
        menu.ShowAt(chip);
    }

    private async Task _ChooseFolderAsync(SimpleHomeViewModel home)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a folder to work in" });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is { } folder)
        {
            home.ChooseFolder(folder);
        }
    }
}
