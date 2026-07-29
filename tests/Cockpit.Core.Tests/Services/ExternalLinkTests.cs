using Cockpit.App.Services;

namespace Cockpit.Core.Tests.Services;

/// <summary>
/// The guard in front of the one place this app hands an operator's own text to the shell. Both directions are
/// exercised through <see cref="ExternalLink.TryParseWebAddress"/> rather than through the opening call: a case that reached
/// the shell would start a browser on the machine running the tests, so an accept can only be asserted on the
/// decision. Asserting only the refusals would leave an inverted guard — every link silently dead — green.
/// </summary>
public class ExternalLinkTests
{
    [Theory]
    [InlineData("https://github.com/example/repo")]
    [InlineData("http://example.test")]
    [InlineData("https://example.test:8443/path?q=1#frag")]
    [InlineData("HTTPS://EXAMPLE.TEST")]
    public void TryParseWebAddress_AnHttpOrHttpsUrl_IsAccepted(string value) =>
        Assert.True(ExternalLink.TryParseWebAddress(value, out _), "a value the rows draw as a link has to be one this will actually open");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("github.com/example/repo")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("C:\\Windows\\System32\\calc.exe")]
    [InlineData("\\\\server\\share\\payload.exe")]
    [InlineData("cmd.exe /c calc")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("ms-msdt:/id")]
    [InlineData("search-ms:query=x")]
    [InlineData("vscode://file/etc/passwd")]
    [InlineData("mailto:someone@example.test")]
    public void TryParseWebAddress_AnythingElse_IsRefused(string? value) =>
        Assert.False(ExternalLink.TryParseWebAddress(value, out _), "these are the values a weaker guard would have launched a program with");

    [Theory]
    [InlineData(null)]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("cmd.exe /c calc")]
    [InlineData("javascript:alert(1)")]
    public void TryOpen_SomethingItRefuses_StartsNothing(string? value) =>
        Assert.False(ExternalLink.TryOpen(value), "a false return is the proof nothing was handed to the shell");

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ms-msdt:/id")]
    [InlineData("vscode://file/etc/passwd")]
    public void TryOpen_AParsedAddressWithTheWrongScheme_IsStillRefused(string value)
    {
        // The overload for a caller that parsed the address itself re-checks the scheme rather than trusting them: a
        // rule this class owns but only its callers apply is not a rule, and a caller that reaches the shell this way
        // writes no shell-out of its own for the source scan to notice.
        Assert.False(ExternalLink.TryOpen(new Uri(value)), "the guard belongs to this class, not to whoever calls it");
    }
}
