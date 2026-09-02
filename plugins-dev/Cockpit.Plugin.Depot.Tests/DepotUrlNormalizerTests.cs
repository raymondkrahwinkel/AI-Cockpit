namespace Cockpit.Plugin.Depot.Tests;

// `DepotUrlNormalizer` (AC-499): pins the round-trip guarantee `Normalize(endpointUrl) + "/mcp" == endpointUrl`
// for every endpoint URL an operator actually pastes, including a deployment whose base path itself ends in
// `/mcp` (`https://host/mcp/mcp`), which a naive "strip every trailing /mcp" implementation gets wrong.
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
    [Theory]
    [InlineData("https://depot.example.com", "https://depot.example.com")]
    [InlineData("  not a url at all  ", "not a url at all")]
    [InlineData("https://depot.example.com/mcp?token=abc", "https://depot.example.com/mcp?token=abc")]
    [InlineData("https://depot.example.com/mcp#section", "https://depot.example.com/mcp#section")]
    public void Normalize_WithNoTrailingMcpSegment_IsOnlyTrimmed(string url, string expected)
    {
        Assert.Equal(expected, DepotUrlNormalizer.Normalize(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_NullOrBlank_ReturnsEmpty(string? url)
    {
        Assert.Equal(string.Empty, DepotUrlNormalizer.Normalize(url));
    }

    // For every endpoint URL an operator actually pastes (always the full endpoint, including /mcp), stripping
    // it down and re-appending "/mcp" must land back on the same endpoint. This states the guarantee itself
    // rather than a table of outputs, so it stays alongside the tables above rather than being folded into them.
    [Theory]
    [InlineData("https://depot.example.com/mcp")] // root deployment
    [InlineData("https://depot.example.com/mcp/")] // trailing slash after the suffix
    [InlineData("https://host/depot/mcp")] // subpath deployment
    [InlineData("https://host/mcp/mcp")] // base path itself is /mcp
    [InlineData("https://depot.example.com:8443/mcp")] // with port
    public void Normalize_RoundTripsBackToTheOriginalEndpoint(string endpointUrl)
    {
        var trimmedEndpoint = endpointUrl.TrimEnd('/');

        Assert.Equal(trimmedEndpoint, DepotUrlNormalizer.Normalize(endpointUrl) + "/mcp");
    }

    [Theory]
    [InlineData("https://depot.example.com", "https://depot.example.com")]
    [InlineData("https://host/depot", "https://host")]
    [InlineData("https://depot.example.com:8443", "https://depot.example.com:8443")]
    [InlineData("depot.example.com", null)] // no scheme is not a URL to take an origin from
    [InlineData("not a url at all", null)]
    public void Origin_KeepsSchemeHostAndPort_AndIsNullWithoutAUrl(string url, string? expected)
    {
        Assert.Equal(expected, DepotUrlNormalizer.Origin(url));
    }
}
