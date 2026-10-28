namespace Cockpit.Plugin.Depot.Tests;

// `DepotUrlNormalizer` (AC-499): every endpoint URL an operator actually pastes strips to exactly its base, so
// `Normalize(endpointUrl) + "/mcp" == endpointUrl` holds row by row in the table below, including a deployment whose
// base path itself ends in `/mcp` (`https://host/mcp/mcp`), which a naive "strip every trailing /mcp" gets wrong.
public class DepotUrlNormalizerTests
{
    // One row per spelling that reaches the strip branch. `https://host/mcp/mcp` is the row that fails under a
    // loop stripping every trailing /mcp instead of exactly one: that deployment's own base path is /mcp.
    [Theory]
    [InlineData("https://depot.example.com/mcp", "https://depot.example.com")]
    [InlineData("https://depot.example.com/mcp/", "https://depot.example.com")]
    [InlineData("https://depot.example.com/MCP", "https://depot.example.com")]
    [InlineData("https://host/depot/mcp", "https://host/depot")]
    [InlineData("https://host/mcp/mcp", "https://host/mcp")]
    [InlineData("depot.example.com/mcp", "depot.example.com")]
    [InlineData("https://depot.example.com:8443/mcp", "https://depot.example.com:8443")]
    public void Normalize_StripsExactlyOneTrailingMcpSegment(string url, string expected)
    {
        Assert.Equal(expected, DepotUrlNormalizer.Normalize(url));
    }

    // Decision (documented on the class): a literal trailing-substring transform, not a URI parse — a query string
    // or fragment after /mcp stops the literal suffix match, so it is left untouched rather than guessed at. Depot's
    // own documented URL never carries either.
    [Fact]
    public void Normalize_WithNoTrailingMcpSegment_IsOnlyTrimmed()
    {
        Assert.Equal("https://depot.example.com", DepotUrlNormalizer.Normalize("https://depot.example.com"));
        Assert.Equal("not a url at all", DepotUrlNormalizer.Normalize("  not a url at all  "));
        Assert.Equal("https://depot.example.com/mcp?token=abc", DepotUrlNormalizer.Normalize("https://depot.example.com/mcp?token=abc"));
        Assert.Equal("https://depot.example.com/mcp#section", DepotUrlNormalizer.Normalize("https://depot.example.com/mcp#section"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void Normalize_NullOrBlank_ReturnsEmpty(string? url)
    {
        Assert.Equal(string.Empty, DepotUrlNormalizer.Normalize(url));
    }

    [Fact]
    public void Origin_KeepsSchemeHostAndPort_AndIsNullWithoutAUrl()
    {
        (string Url, string? Expected)[] rows =
        [
            ("https://depot.example.com", "https://depot.example.com"),
            ("https://host/depot", "https://host"),
            ("https://depot.example.com:8443", "https://depot.example.com:8443"),
            ("depot.example.com", null), // no scheme is not a URL to take an origin from
            ("not a url at all", null),
        ];

        foreach (var (url, expected) in rows)
        {
            Assert.Equal(expected, DepotUrlNormalizer.Origin(url));
        }
    }
}
