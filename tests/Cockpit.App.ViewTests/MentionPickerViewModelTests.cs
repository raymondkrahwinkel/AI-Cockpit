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
    public void OnTextChanged_AtTrigger_OpensAndLoadsMatches()
    {
        var viewModel = _WithFiles(["src/Foo.cs", "src/Bar.cs"]);

        viewModel.OnTextChanged("@", 1);

        Assert.True(viewModel.IsOpen);
        Assert.False(viewModel.IsLoading);
        Assert.Equal(2, viewModel.Matches.Count);
    }

    [Fact]
    public void OnTextChanged_NoWorkingDirectory_NeverOpens()
    {
        var viewModel = _WithFiles(["src/Foo.cs"], workingDirectory: null);

        viewModel.OnTextChanged("@", 1);

        Assert.False(viewModel.IsOpen);
    }

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
    public void OnTextChanged_ReopeningAfterAClose_ReloadsTheFileSource()
    {
        var calls = 0;
        var viewModel = new MentionPickerViewModel(_ => { calls++; return Task.FromResult<IReadOnlyList<string>>(["src/Foo.cs"]); }, () => "/repo");

        viewModel.OnTextChanged("@foo", 4);
        viewModel.OnTextChanged("@foo ", 5); // closes
        viewModel.OnTextChanged("bar @baz", 8); // fresh '@', reopens

        Assert.Equal(2, calls);
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
    public void Accept_WithNothingSelected_ReturnsNull()
    {
        var viewModel = _WithFiles([]);
        viewModel.OnTextChanged("@zzz", 4);

        Assert.Null(viewModel.Accept());
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

    [Fact]
    public void Dismiss_ThenANewAtToken_ReopensNormally()
    {
        var viewModel = _WithFiles(["src/Foo.cs"]);
        viewModel.OnTextChanged("@fo", 3);
        viewModel.Dismiss();

        viewModel.OnTextChanged("@fo bar @f", 10);

        Assert.True(viewModel.IsOpen);
    }

    [Fact]
    public void OnTextChanged_WhileALoadIsStillPending_ShowsLoadingThenPopulatesOnCompletion()
    {
        var source = new TaskCompletionSource<IReadOnlyList<string>>();
        var viewModel = new MentionPickerViewModel(_ => source.Task, () => "/repo");

        viewModel.OnTextChanged("@", 1);

        Assert.True(viewModel.IsOpen);
        Assert.True(viewModel.IsLoading);
        Assert.Empty(viewModel.Matches);

        source.SetResult(["src/Foo.cs"]);

        Assert.False(viewModel.IsLoading);
        Assert.Single(viewModel.Matches);
    }

    [Fact]
    public void Close_ClearsDismissSuppression()
    {
        var viewModel = _WithFiles(["src/Foo.cs"]);
        viewModel.OnTextChanged("@fo", 3);
        viewModel.Dismiss();

        viewModel.Close();
        viewModel.OnTextChanged("@fo", 3);

        Assert.True(viewModel.IsOpen);
    }
}
