using Cockpit.App.ViewModels;
using Cockpit.Core.Sessions;
using Material.Icons;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// AC-720: an error row's <c>ErrorKind</c> drives which of the three severities (blocking/temporary/
/// informational) it renders as, plus its icon and optional "try again after" caption — presentation only.
/// </summary>
public class TranscriptEntryErrorSeverityTests
{
    [Fact]
    public void DefaultKindIsUnknown_AndRendersInformational()
    {
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.Error, "boom");

        Assert.Equal(SessionErrorKind.Unknown, entry.ErrorKind);
        Assert.True(entry.IsInformationalError);
        Assert.False(entry.IsBlockingError);
        Assert.False(entry.IsTemporaryError);
        Assert.Equal(MaterialIconKind.InformationCircleOutline, entry.ErrorIconKind);
    }

    [Fact]
    public void AuthRequired_IsTheOnlyBlockingKind()
    {
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.Error, "boom") { ErrorKind = SessionErrorKind.AuthRequired };

        Assert.True(entry.IsBlockingError);
        Assert.False(entry.IsTemporaryError);
        Assert.False(entry.IsInformationalError);
        Assert.Equal(MaterialIconKind.LockAlertOutline, entry.ErrorIconKind);
    }

    [Theory]
    [InlineData(SessionErrorKind.RateLimited)]
    [InlineData(SessionErrorKind.ServiceUnavailable)]
    public void RateLimitedAndServiceUnavailable_AreBothTemporary(SessionErrorKind kind)
    {
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.Error, "boom") { ErrorKind = kind };

        Assert.True(entry.IsTemporaryError);
        Assert.False(entry.IsBlockingError);
        Assert.False(entry.IsInformationalError);
    }

    [Fact]
    public void NonErrorRows_NeverReportAnySeverity_EvenIfErrorKindWasSetAnyway()
    {
        // ErrorKind is only meaningful on an Error row; a Question row must not accidentally paint itself.
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.Question, "pick one") { ErrorKind = SessionErrorKind.AuthRequired };

        Assert.False(entry.IsBlockingError);
        Assert.False(entry.IsTemporaryError);
        Assert.False(entry.IsInformationalError);
    }

    [Fact]
    public void NoRetryAfter_HasNoCaption()
    {
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.Error, "boom");

        Assert.False(entry.HasRetryAfter);
        Assert.Equal(string.Empty, entry.RetryAfterText);
    }

    [Fact]
    public void ARetryAfterTime_RendersAsALocalHourMinuteCaption()
    {
        var retryAfter = new DateTimeOffset(2026, 8, 11, 14, 30, 0, TimeSpan.Zero);
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.Error, "boom") { RetryAfter = retryAfter };

        Assert.True(entry.HasRetryAfter);
        Assert.Equal($"Try again after {retryAfter.ToLocalTime():HH:mm}", entry.RetryAfterText);
    }
}
