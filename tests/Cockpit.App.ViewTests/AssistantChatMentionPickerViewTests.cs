using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mentions;
using Cockpit.Core.Abstractions.Voice;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-740 addendum: the same @-mention picker in a hand-copied second composer, whose wiring is re-proven
/// here rather than assumed. Working directory: the session's own, then the Assistant Profile's default —
/// never the Cockpit process's own cwd.
/// </summary>
[Collection("avalonia")]
public class AssistantChatMentionPickerViewTests
{
    private sealed record Pane(Window Window, AssistantChatViewModel ViewModel) : IDisposable
    {
        public void Dispose() => Window.Close();
    }

    private static IAssistantSessionHost _FakeHost(SessionViewModel? session = null, string? defaultWorkingDirectory = null)
    {
        var host = Substitute.For<IAssistantSessionHost>();
        host.Session.Returns(session);
        host.DefaultWorkingDirectory.Returns(defaultWorkingDirectory);
        return host;
    }

    private static IMentionFileSource _FilesNamed(params string[] paths)
    {
        var source = Substitute.For<IMentionFileSource>();
        source.GetPathsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(paths));
        return source;
    }

    private static Pane _Pane(IAssistantSessionHost host, IMentionFileSource? fileSource = null)
    {
        var viewModel = new AssistantChatViewModel(
            host, Substitute.For<IAssistantSettingsStore>(), Substitute.For<IVoicePlaybackQueue>(), mentionFileSource: fileSource);
        var window = new AssistantChatWindow { Width = 420, Height = 560, DataContext = viewModel };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return new Pane(window, viewModel);
    }

    private static TextBox _Composer(Pane pane) =>
        pane.Window.GetVisualDescendants().OfType<TextBox>().First(box => box.Name == "InputBox");

    private static Popup _MentionPopup(Pane pane) =>
        pane.Window.GetVisualDescendants().OfType<Popup>().First();

    private static void _Settle(Pane pane)
    {
        Dispatcher.UIThread.RunJobs();
        pane.Window.UpdateLayout();
    }

    private static void _Type(TextBox box, Pane pane, string text)
    {
        foreach (var c in text)
        {
            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.None });
            var current = box.Text ?? string.Empty;
            var caret = box.CaretIndex;
            box.Text = current[..caret] + c + current[caret..];
            box.CaretIndex = caret + 1;
            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.None });
            _Settle(pane);
        }
    }

    private static void _Press(TextBox box, Pane pane, Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, KeyModifiers = modifiers });
        _Settle(pane);
    }

    [Fact]
    public void TypingAnAtSign_WithASessionWorkingDirectory_OpensThePopup() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel { WorkingDirectory = "/repo" };
        using var pane = _Pane(_FakeHost(session));
        var box = _Composer(pane);

        _Type(box, pane, "@");

        Assert.True(pane.ViewModel.MentionPicker.IsOpen);
        Assert.True(_MentionPopup(pane).IsOpen);
    });

    [Fact]
    public void TypingAnAtSign_BeforeASessionExists_FallsBackToTheProfileDefaultAndOpens() => HeadlessAvalonia.Run(() =>
    {
        using var pane = _Pane(_FakeHost(session: null, defaultWorkingDirectory: "/profile-default"));
        var box = _Composer(pane);

        _Type(box, pane, "@");

        Assert.True(pane.ViewModel.MentionPicker.IsOpen);
    });

    [Fact]
    public void TypingAnAtSign_WithNeitherASessionNorAProfileDefault_NeverOpensThePopup() => HeadlessAvalonia.Run(() =>
    {
        using var pane = _Pane(_FakeHost());
        var box = _Composer(pane);

        _Type(box, pane, "@");

        Assert.False(pane.ViewModel.MentionPicker.IsOpen);
        Assert.False(_MentionPopup(pane).IsOpen);
    });

    [Fact]
    public void Enter_WithASelection_InsertsTheMention_AndDoesNotSend() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel { WorkingDirectory = "/repo" };
        using var pane = _Pane(_FakeHost(session), _FilesNamed("src/Foo.cs"));
        var box = _Composer(pane);
        _Type(box, pane, "@foo");

        _Press(box, pane, Key.Enter);

        Assert.False(pane.ViewModel.MentionPicker.IsOpen);
        Assert.Equal("@src/Foo.cs ", box.Text);
        Assert.Equal("@src/Foo.cs ", pane.ViewModel.InputText);
    });

    [Fact]
    public void APasteThatLeavesMentionLookingText_NeverOpensThePicker() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel { WorkingDirectory = "/repo" };
        using var pane = _Pane(_FakeHost(session));
        var box = _Composer(pane);

        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.V, KeyModifiers = KeyModifiers.Control });
        box.Text = "@pasted";
        box.CaretIndex = box.Text.Length;
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.V, KeyModifiers = KeyModifiers.Control });
        _Settle(pane);

        Assert.False(pane.ViewModel.MentionPicker.IsOpen);
    });
}
