using System.ClientModel;
using System.ClientModel.Primitives;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Infrastructure.Tests.Sessions;

/// <summary>
/// AC-720: <see cref="OpenAiCompatSessionDriver._ClassifyError"/>/<c>_RetryAfterFrom</c> — the HTTP status
/// (and, for a 429, the Retry-After header) is the one structured signal an OpenAI-compatible server gives.
/// </summary>
public class OpenAiCompatSessionDriverErrorClassificationTests
{
    [Theory]
    [InlineData(401, SessionErrorKind.AuthRequired)]
    [InlineData(403, SessionErrorKind.AuthRequired)]
    [InlineData(429, SessionErrorKind.RateLimited)]
    [InlineData(500, SessionErrorKind.ServiceUnavailable)]
    [InlineData(503, SessionErrorKind.ServiceUnavailable)]
    [InlineData(400, SessionErrorKind.Unknown)]
    [InlineData(404, SessionErrorKind.Unknown)]
    public void ClassifyError_MapsHttpStatusToKind(int status, SessionErrorKind expected)
    {
        var ex = new ClientResultException(new _FakeResponse(status));

        Assert.Equal(expected, OpenAiCompatSessionDriver._ClassifyError(ex));
    }

    [Fact]
    public void ClassifyError_ANonHttpException_StaysUnknown()
    {
        Assert.Equal(SessionErrorKind.Unknown, OpenAiCompatSessionDriver._ClassifyError(new HttpRequestException("server unreachable")));
    }

    [Fact]
    public void RetryAfterFrom_A429WithADeltaSecondsHeader_ReturnsThatManySecondsFromNow()
    {
        var ex = new ClientResultException(new _FakeResponse(429, retryAfterHeaderValue: "30"));

        var retryAfter = OpenAiCompatSessionDriver._RetryAfterFrom(ex);

        Assert.NotNull(retryAfter);
        Assert.True(retryAfter > DateTimeOffset.UtcNow.AddSeconds(25) && retryAfter <= DateTimeOffset.UtcNow.AddSeconds(30));
    }

    [Fact]
    public void RetryAfterFrom_A429WithAnHttpDateHeader_ParsesTheDateInstead()
    {
        // RFC 9110 §10.2.3: Retry-After may be an HTTP-date instead of delta-seconds.
        var ex = new ClientResultException(new _FakeResponse(429, retryAfterHeaderValue: "Wed, 21 Oct 2026 07:28:00 GMT"));

        Assert.Equal(new DateTimeOffset(2026, 10, 21, 7, 28, 0, TimeSpan.Zero), OpenAiCompatSessionDriver._RetryAfterFrom(ex));
    }

    [Fact]
    public void RetryAfterFrom_NoRetryAfterHeader_ReturnsNull()
    {
        var ex = new ClientResultException(new _FakeResponse(429));

        Assert.Null(OpenAiCompatSessionDriver._RetryAfterFrom(ex));
    }

    // Minimal PipelineResponse: just enough of the abstract surface to build a ClientResultException with a
    // chosen status and (optionally) a Retry-After header — this SDK ships no in-memory response for tests.
    private sealed class _FakeResponse(int status, string? retryAfterHeaderValue = null) : PipelineResponse
    {
        public override int Status => status;
        public override string ReasonPhrase => string.Empty;
        public override Stream? ContentStream { get; set; }
        public override BinaryData Content => BinaryData.FromString(string.Empty);
        protected override PipelineResponseHeaders HeadersCore { get; } = new _FakeHeaders(retryAfterHeaderValue);

        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => Content;

        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Content);

        public override void Dispose()
        {
        }
    }

    private sealed class _FakeHeaders(string? retryAfterHeaderValue) : PipelineResponseHeaders
    {
        public override bool TryGetValue(string name, out string? value)
        {
            value = string.Equals(name, "Retry-After", StringComparison.OrdinalIgnoreCase) ? retryAfterHeaderValue : null;
            return value is not null;
        }

        public override bool TryGetValues(string name, out IEnumerable<string>? values)
        {
            values = TryGetValue(name, out var value) ? [value!] : null;
            return values is not null;
        }

        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            if (retryAfterHeaderValue is not null)
            {
                yield return new KeyValuePair<string, string>("Retry-After", retryAfterHeaderValue);
            }
        }
    }
}
