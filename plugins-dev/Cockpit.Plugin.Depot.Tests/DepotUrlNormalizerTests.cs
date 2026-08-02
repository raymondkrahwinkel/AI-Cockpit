namespace Cockpit.Plugin.Depot.Tests;

// `DepotUrlNormalizer` (AC-499): pins the round-trip guarantee the class doc comment promises —
// `Normalize(endpointUrl) + "/mcp" == endpointUrl` for every endpoint URL an operator actually pastes,
// including a deployment whose own base path ends in `/mcp` (`https://host/mcp/mcp`), which a naive
// "strip every trailing /mcp" implementation gets wrong.
public class DepotUrlNormalizerTests
{
    [Fact]
    public void Normalize_RootWithoutPath_IsUnchanged()
    {
        Assert.Equal("https://depot.example.com", DepotUrlNormalizer.Normalize("https://depot.example.com"));
    }

    [Fact]
    public void Normalize_WithTrailingMcp_StripsIt()
    {
        Assert.Equal("https://depot.example.com", DepotUrlNormalizer.Normalize("https://depot.example.com/mcp"));
    }

    [Fact]
    public void Normalize_WithTrailingMcpAndSlash_StripsBoth()
    {
        Assert.Equal("https://depot.example.com", DepotUrlNormalizer.Normalize("https://depot.example.com/mcp/"));
    }

    [Fact]
    public void Normalize_McpSuffixUppercase_StripsCaseInsensitively()
    {
        Assert.Equal("https://depot.example.com", DepotUrlNormalizer.Normalize("https://depot.example.com/MCP"));
    }

    [Fact]
    public void Normalize_SubpathDeployment_StripsOnlyTheMcpSegment()
    {
        Assert.Equal("https://host/depot", DepotUrlNormalizer.Normalize("https://host/depot/mcp"));
    }

    [Fact]
    public void Normalize_DoubledMcpSuffix_StripsOnlyOneSegment()
    {
        // The case a "strip every trailing /mcp" loop gets wrong: this deployment's own base path is /mcp (its
        // real endpoint is https://host/mcp/mcp), so only one segment comes off, not both down to the origin.
        Assert.Equal("https://host/mcp", DepotUrlNormalizer.Normalize("https://host/mcp/mcp"));
    }

    [Fact]
    public void Normalize_EmptyString_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, DepotUrlNormalizer.Normalize(string.Empty));
    }

    [Fact]
    public void Normalize_Whitespace_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, DepotUrlNormalizer.Normalize("   "));
    }

    [Fact]
    public void Normalize_Null_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, DepotUrlNormalizer.Normalize(null));
    }

    [Fact]
    public void Normalize_UrlWithoutScheme_StillStripsTheMcpSuffix()
    {
        Assert.Equal("depot.example.com", DepotUrlNormalizer.Normalize("depot.example.com/mcp"));
    }

    [Fact]
    public void Normalize_NotAUrlAtAll_IsOnlyTrimmed()
    {
        Assert.Equal("not a url at all", DepotUrlNormalizer.Normalize("  not a url at all  "));
    }

    [Fact]
    public void Normalize_UrlWithPort_StripsTheMcpSuffixAfterThePort()
    {
        Assert.Equal("https://depot.example.com:8443", DepotUrlNormalizer.Normalize("https://depot.example.com:8443/mcp"));
    }

    // Decision (documented on the class): a literal trailing-substring transform, not a URI parse — a query string
    // or fragment after /mcp stops the literal suffix match, so it is left untouched rather than guessed at. Depot's
    // own documented URL never carries either.
    [Fact]
    public void Normalize_UrlWithQueryStringAfterMcp_IsLeftUntouched()
    {
        Assert.Equal("https://depot.example.com/mcp?token=abc", DepotUrlNormalizer.Normalize("https://depot.example.com/mcp?token=abc"));
    }

    [Fact]
    public void Normalize_UrlWithFragmentAfterMcp_IsLeftUntouched()
    {
        Assert.Equal("https://depot.example.com/mcp#section", DepotUrlNormalizer.Normalize("https://depot.example.com/mcp#section"));
    }

    // The property DepotUrlNormalizer's own doc comment promises: for every endpoint URL an operator actually
    // pastes (Depot's docs always show the full endpoint, including /mcp), stripping it down and re-appending
    // "/mcp" — exactly what DepotPlugin._ContributionFor does with the stored, already-normalized base — must land
    // back on the same endpoint. "https://host/mcp/mcp" is the case that breaks under a loop that strips every
    // trailing /mcp instead of exactly one.
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

    // The round-trip promise is scoped to a real endpoint URL — Depot's own documented URL never carries a query
    // string or fragment after /mcp, and Normalize deliberately leaves both untouched (see the class doc comment),
    // so re-appending "/mcp" to the normalized value does not reconstruct the original input here. Pinning the
    // actual behavior instead: nothing is stripped, so the value is returned unchanged aside from trimming.
    [Theory]
    [InlineData("https://depot.example.com/mcp?token=abc")]
    [InlineData("https://depot.example.com/mcp#section")]
    public void Normalize_EndpointWithQueryOrFragment_DoesNotRoundTrip(string endpointUrl)
    {
        Assert.Equal(endpointUrl, DepotUrlNormalizer.Normalize(endpointUrl));
    }

    [Fact]
    public void Origin_RootUrl_IsTheUrlItself()
    {
        Assert.Equal("https://depot.example.com", DepotUrlNormalizer.Origin("https://depot.example.com"));
    }

    [Fact]
    public void Origin_UrlWithSubpath_DropsThePath()
    {
        Assert.Equal("https://host", DepotUrlNormalizer.Origin("https://host/depot"));
    }

    [Fact]
    public void Origin_UrlWithPort_KeepsThePort()
    {
        Assert.Equal("https://depot.example.com:8443", DepotUrlNormalizer.Origin("https://depot.example.com:8443"));
    }

    [Fact]
    public void Origin_UrlWithoutScheme_ReturnsNull()
    {
        Assert.Null(DepotUrlNormalizer.Origin("depot.example.com"));
    }

    [Fact]
    public void Origin_NotAUrlAtAll_ReturnsNull()
    {
        Assert.Null(DepotUrlNormalizer.Origin("not a url at all"));
    }
}
