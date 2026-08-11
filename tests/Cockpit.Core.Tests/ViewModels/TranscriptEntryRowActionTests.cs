using Cockpit.App.ViewModels;
using CommunityToolkit.Mvvm.Input;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// AC-715: the generic row action — one optional labelled button any transcript row can carry, so a row that
/// needs an affordance (AC-713's "Login" on an auth-error row) reuses it instead of growing a card of its own.
/// </summary>
public class TranscriptEntryRowActionTests
{
    [Fact]
    public void ARowWithoutAnActionShowsNoButton()
    {
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.Error, "Not signed in.");

        Assert.False(entry.HasAction);
    }

    [Fact]
    public void BothALabelAndACommandAreNeeded_SoAHalfSetActionNeverRendersADeadButton()
    {
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.Error, "Not signed in.")
        {
            ActionLabel = "Login",
        };
        Assert.False(entry.HasAction);

        entry.ActionLabel = null;
        entry.ActionCommand = new RelayCommand(() => { });
        Assert.False(entry.HasAction);
    }

    [Fact]
    public void ALabelledCommandRendersAndRuns()
    {
        var ran = 0;
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.Error, "Not signed in.")
        {
            ActionLabel = "Login",
            ActionCommand = new RelayCommand(() => ran++),
        };

        Assert.True(entry.HasAction);
        entry.ActionCommand.Execute(null);

        Assert.Equal(1, ran);
    }
}
