using FluentAssertions;
using Cockpit.App.Services;

namespace Cockpit.Core.Tests.Services;

/// <summary>
/// The guard in front of the one place this app hands an operator's own text to the shell. Only the refusals are
/// exercised, deliberately: a case that passed the guard would start a browser on the machine running the tests, so
/// the opening half is verified by rendering the control and by the four views that have always opened links this way.
/// </summary>
public class ExternalLinkTests
{
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
    public void TryOpen_AnythingThatIsNotAnHttpUrl_StartsNothing(string? value) =>
        ExternalLink.TryOpen(value)
            .Should().BeFalse("a false return is the proof nothing was handed to the shell — these are the values a " +
                              "weaker guard would have launched a program with");
}
