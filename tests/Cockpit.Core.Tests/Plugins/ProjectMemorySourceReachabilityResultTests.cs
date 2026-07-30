using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// <see cref="ProjectMemorySourceReachabilityResult.Confirmed"/> promises, in its own doc comment, "a short
/// confirmation string" — but the value comes from a plugin relaying a tool's raw text response (AC-503 adversarial
/// review, Opus confirming round), which is server-controlled, not operator-controlled. Nothing enforced the word
/// "short" until this test: a multi-kilobyte tool response reached <see cref="ProjectDialogResourceRowTests"/>'s own
/// row and, from there, an unbounded <c>TextBlock</c> in the project editor.
/// </summary>
public class ProjectMemorySourceReachabilityResultTests
{
    [Fact]
    public void Confirmed_ADetailFarLongerThanAConfirmationSentence_IsClampedToAShortString()
    {
        var huge = new string('x', 20_000);

        var result = ProjectMemorySourceReachabilityResult.Confirmed(huge);

        Assert.NotNull(result.Detail);
        Assert.True(
            result.Detail!.Length <= ProjectMemorySourceReachabilityResult.MaxDetailLength,
            $"Detail was {result.Detail.Length} characters, expected at most {ProjectMemorySourceReachabilityResult.MaxDetailLength}.");
    }

    [Fact]
    public void Confirmed_ADetailWithNewlines_IsCollapsedToASingleLine()
    {
        var result = ProjectMemorySourceReachabilityResult.Confirmed("24 documents\nlast changed 2 hours ago\n\nsome trailing report body");

        Assert.NotNull(result.Detail);
        Assert.DoesNotContain('\n', result.Detail);
        Assert.DoesNotContain('\r', result.Detail);
    }

    [Fact]
    public void Confirmed_ADetailWithinTheLimit_IsReturnedUnchanged()
    {
        var result = ProjectMemorySourceReachabilityResult.Confirmed("24 documents, last changed 2 hours ago");

        Assert.Equal("24 documents, last changed 2 hours ago", result.Detail);
    }

    [Fact]
    public void Confirmed_NullDetail_StaysNull()
    {
        var result = ProjectMemorySourceReachabilityResult.Confirmed(null);

        Assert.Null(result.Detail);
    }

    // --- AC-499: CheckFailed — the state a plugin reports when its check ran but the call itself failed, distinct
    // from NotSignedIn. Its Detail carries the plugin's own account of what went wrong, so it is clamped exactly
    // the same way Confirmed's own is: a plugin's error text is no more trusted to be short than a tool's raw
    // response is.

    [Fact]
    public void CheckFailed_SetsStateToCheckFailed()
    {
        var result = ProjectMemorySourceReachabilityResult.CheckFailed("connection reset");

        Assert.Equal(ProjectMemorySourceReachability.CheckFailed, result.State);
    }

    [Fact]
    public void CheckFailed_ADetailFarLongerThanAnErrorSentence_IsClampedToAShortString()
    {
        var huge = new string('x', 20_000);

        var result = ProjectMemorySourceReachabilityResult.CheckFailed(huge);

        Assert.NotNull(result.Detail);
        Assert.True(
            result.Detail!.Length <= ProjectMemorySourceReachabilityResult.MaxDetailLength,
            $"Detail was {result.Detail.Length} characters, expected at most {ProjectMemorySourceReachabilityResult.MaxDetailLength}.");
    }

    [Fact]
    public void CheckFailed_NullDetail_StaysNull()
    {
        var result = ProjectMemorySourceReachabilityResult.CheckFailed(null);

        Assert.Null(result.Detail);
    }
}
