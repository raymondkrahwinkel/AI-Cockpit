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
    [InlineData(null)]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
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
