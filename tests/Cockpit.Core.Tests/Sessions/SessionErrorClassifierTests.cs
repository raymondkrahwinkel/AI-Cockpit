using Cockpit.Core.Sessions;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// AC-720's fallback: guesses a <see cref="SessionErrorKind"/> from a driver's message text when the driver has
/// not been taught to set <c>Kind</c> itself. Presentation only — the default for anything unrecognised is
/// <see cref="SessionErrorKind.Unknown"/>, never a guessed blocking/temporary severity.
/// </summary>
public class SessionErrorClassifierTests
{
    [Theory]
    [InlineData("401 Unauthorized: invalid api key", SessionErrorKind.AuthRequired)]
    [InlineData("You are not authenticated. Please log in.", SessionErrorKind.AuthRequired)]
    [InlineData("429 Too Many Requests: rate limit exceeded", SessionErrorKind.RateLimited)]
    [InlineData("Error: quota exceeded for this model", SessionErrorKind.RateLimited)]
    [InlineData("The request timed out after 30s", SessionErrorKind.ServiceUnavailable)]
    [InlineData("502 Bad Gateway", SessionErrorKind.ServiceUnavailable)]
    [InlineData("connection refused", SessionErrorKind.ServiceUnavailable)]
    public void RecognisedWording_ClassifiesAccordingly(string message, SessionErrorKind expected)
    {
        Assert.Equal(expected, SessionErrorClassifier.Classify(message));
    }

    [Theory]
    [InlineData("The model returned no response — no text and no tool calls.")]
    [InlineData("something odd happened")]
    [InlineData("")]
    public void UnrecognisedWording_StaysUnknown_NeverAGuessedSeverity(string message)
    {
        Assert.Equal(SessionErrorKind.Unknown, SessionErrorClassifier.Classify(message));
    }
}
