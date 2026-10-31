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
    public void Normalize_WithNoTrailingMcpSegment_IsOnlyTrimmed_AndNullOrBlankIsEmpty()
    {
        Assert.Equal("https://depot.example.com", DepotUrlNormalizer.Normalize("https://depot.example.com"));
        Assert.Equal("not a url at all", DepotUrlNormalizer.Normalize("  not a url at all  "));
        Assert.Equal("https://depot.example.com/mcp?token=abc", DepotUrlNormalizer.Normalize("https://depot.example.com/mcp?token=abc"));
        Assert.Equal("https://depot.example.com/mcp#section", DepotUrlNormalizer.Normalize("https://depot.example.com/mcp#section"));
        Assert.Equal(string.Empty, DepotUrlNormalizer.Normalize(null));
        Assert.Equal(string.Empty, DepotUrlNormalizer.Normalize("   "));
    }

    [Fact]
    public void Origin_KeepsSchemeHostAndPort_AndIsNullWithoutAUrl()
    {
        Assert.Equal("https://depot.example.com", DepotUrlNormalizer.Origin("https://depot.example.com"));
        Assert.Equal("https://host", DepotUrlNormalizer.Origin("https://host/depot"));
        Assert.Equal("https://depot.example.com:8443", DepotUrlNormalizer.Origin("https://depot.example.com:8443"));
        Assert.Null(DepotUrlNormalizer.Origin("depot.example.com")); // no scheme is not a URL to take an origin from
        Assert.Null(DepotUrlNormalizer.Origin("not a url at all"));
    }
}
