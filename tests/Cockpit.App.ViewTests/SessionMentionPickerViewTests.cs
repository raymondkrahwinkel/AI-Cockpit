using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mentions;
using Cockpit.Core.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-740's @-mention picker wired into the real composer: the tunnel key-handler must give Up/Down/Tab/Enter/Esc
/// to an open picker before recall/stop/send/paste ever see them, the Popup's IsOpen has to actually track
/// <see cref="MentionPickerViewModel.IsOpen"/>, and a paste that happens to leave "@word"-looking text behind
/// must never be mistaken for the operator typing it.
/// </summary>
[Collection("avalonia")]
public class SessionMentionPickerViewTests
{
    private sealed record Pane(Window Window, SessionViewModel Session) : IDisposable
    {
        public void Dispose() => Window.Close();
    }

    private static Pane _Pane(SessionViewModel session)
    {
        var window = new Window { Width = 620, Height = 480, Content = new ContentControl { Content = session } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return new Pane(window, session);
    }

    private static SessionViewModel _Session(string? workingDirectory = "/repo", IMentionFileSource? fileSource = null)
    {
        var session = new SessionViewModel(Substitute.For<ISessionManager>(), mentionFileSource: fileSource);
        session.QueuedMessages.Clear();
        session.PendingAttachments.Clear();
        session.WorkingDirectory = workingDirectory;
        return session;
    }

    private static IMentionFileSource _FilesNamed(params string[] paths)
    {
        var source = Substitute.For<IMentionFileSource>();
        source.GetPathsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(paths));
        return source;
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
    public void TypingAnAtSign_OpensThePopup() => HeadlessAvalonia.Run(() =>
    {
        using var pane = _Pane(_Session());
        var box = _Composer(pane);

        _Type(box, pane, "@");

        Assert.True(pane.Session.MentionPicker.IsOpen);
        Assert.True(_MentionPopup(pane).IsOpen);
    });

    [Fact]
    public void WithoutAWorkingDirectory_TypingAnAtSign_NeverOpensThePopup() => HeadlessAvalonia.Run(() =>
    {
        using var pane = _Pane(_Session(workingDirectory: null));
        var box = _Composer(pane);

        _Type(box, pane, "@");

        Assert.False(pane.Session.MentionPicker.IsOpen);
        Assert.False(_MentionPopup(pane).IsOpen);
    });

    [Fact]
    public void Escape_ClosesThePopup() => HeadlessAvalonia.Run(() =>
    {
        using var pane = _Pane(_Session());
        var box = _Composer(pane);
        _Type(box, pane, "@foo");
        Assert.True(pane.Session.MentionPicker.IsOpen);

        _Press(box, pane, Key.Escape);

        Assert.False(pane.Session.MentionPicker.IsOpen);
    });

    [Fact]
    public void DownArrow_MovesThePickerSelection_InsteadOfTheCaret() => HeadlessAvalonia.Run(() =>
    {
        using var pane = _Pane(_Session(fileSource: _FilesNamed("a.cs", "b.cs")));
        var box = _Composer(pane);
        _Type(box, pane, "@");
        var first = pane.Session.MentionPicker.Selected;

        _Press(box, pane, Key.Down);

        Assert.NotNull(first);
        Assert.NotEqual(first, pane.Session.MentionPicker.Selected);
    });

    [Fact]
    public void Enter_WithASelection_InsertsTheMention_AndDoesNotSend() => HeadlessAvalonia.Run(() =>
    {
        using var pane = _Pane(_Session(fileSource: _FilesNamed("src/Foo.cs")));
        var box = _Composer(pane);
        _Type(box, pane, "@foo");

        _Press(box, pane, Key.Enter);

        Assert.False(pane.Session.MentionPicker.IsOpen);
        Assert.Equal("@src/Foo.cs ", box.Text);
        Assert.Equal(box.Text!.Length, box.CaretIndex);
    });

    [Fact]
    public void Tab_AcceptsTheSelectionTheSameAsEnter() => HeadlessAvalonia.Run(() =>
    {
        using var pane = _Pane(_Session(fileSource: _FilesNamed("src/Foo.cs")));
        var box = _Composer(pane);
        _Type(box, pane, "@foo");

        _Press(box, pane, Key.Tab);

        Assert.Equal("@src/Foo.cs ", box.Text);
    });

    [Fact]
    public void APasteThatLeavesMentionLookingText_NeverOpensThePicker() => HeadlessAvalonia.Run(() =>
    {
        using var pane = _Pane(_Session());
        var box = _Composer(pane);

        // Simulates what Ctrl+V produces: the KeyDown marks itself handled and kicks off the async clipboard
        // read (which _HandlePasteAsync owns); here we only need the post-paste text/caret _OnInputKeyUp would
        // see, without actually touching the clipboard.
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.V, KeyModifiers = KeyModifiers.Control });
        box.Text = "@pasted";
        box.CaretIndex = box.Text.Length;
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.V, KeyModifiers = KeyModifiers.Control });
        _Settle(pane);

        Assert.False(pane.Session.MentionPicker.IsOpen);
    });
}
