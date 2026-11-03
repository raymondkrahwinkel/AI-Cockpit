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
