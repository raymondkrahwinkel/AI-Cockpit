using Cockpit.App.ViewModels;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-740's @-mention picker state machine: open/close on the caret's token, load the file list once per open
/// (never per keystroke), rank in-memory as the query narrows, and stay shut for a token Esc dismissed.
/// </summary>
[Collection("avalonia")]
public class MentionPickerViewModelTests
{
    private static MentionPickerViewModel _WithFiles(IReadOnlyList<string> files, string? workingDirectory = "/repo") =>
        new(_ => Task.FromResult(files), () => workingDirectory);

    [Fact]
    public void OnTextChanged_TokenClosedByWhitespace_ClosesAnOpenPicker()
    {
        var viewModel = _WithFiles(["src/Foo.cs"]);
        viewModel.OnTextChanged("@foo", 4);
        Assert.True(viewModel.IsOpen);

        viewModel.OnTextChanged("@foo ", 5);

        Assert.False(viewModel.IsOpen);
        Assert.Empty(viewModel.Matches);
    }

    [Fact]
    public void OnTextChanged_NarrowingTheQueryWithinOneOpenSession_DoesNotReloadTheFileSource()
    {
        var calls = 0;
        var viewModel = new MentionPickerViewModel(_ => { calls++; return Task.FromResult<IReadOnlyList<string>>(["src/Foo.cs", "src/Bar.cs"]); }, () => "/repo");

        viewModel.OnTextChanged("@f", 2);
        viewModel.OnTextChanged("@fo", 3);
        viewModel.OnTextChanged("@foo", 4);

        Assert.Equal(1, calls);
        Assert.Single(viewModel.Matches);
        Assert.Equal("src/Foo.cs", viewModel.Matches[0].Path);
    }

    [Fact]
    public void Move_ClampsToTheEndsOfTheList()
    {
        var viewModel = _WithFiles(["a", "b", "c"]);
        viewModel.OnTextChanged("@", 1);

        viewModel.Move(-1);
        Assert.Equal("a", viewModel.Selected?.Path);

        viewModel.Move(10);
        Assert.Equal("c", viewModel.Selected?.Path);
    }

    [Fact]
    public void Accept_WithASelection_ReturnsItAndCloses()
    {
        var viewModel = _WithFiles(["src/Foo.cs"]);
        viewModel.OnTextChanged("@foo", 4);

        var acceptance = viewModel.Accept();

        Assert.NotNull(acceptance);
        Assert.Equal(0, acceptance!.TokenStart);
        Assert.Equal("src/Foo.cs", acceptance.Path);
        Assert.False(viewModel.IsOpen);
    }

    [Fact]
    public void Dismiss_ThenTypingOnInTheSameToken_DoesNotReopen()
    {
        var viewModel = _WithFiles(["src/Foo.cs"]);
        viewModel.OnTextChanged("@fo", 3);

        viewModel.Dismiss();
        Assert.False(viewModel.IsOpen);

        viewModel.OnTextChanged("@foo", 4);

        Assert.False(viewModel.IsOpen);
    }
}
