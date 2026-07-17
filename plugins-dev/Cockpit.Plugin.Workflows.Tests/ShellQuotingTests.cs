using Cockpit.Plugin.Workflows.Engine;
using FluentAssertions;

namespace Cockpit.Plugin.Workflows.Tests;

/// <summary>
/// Making an untrusted value inert when it is spliced into a command (AC-39). Only the substituted step data is
/// quoted, not the operator's template, so a value carrying shell metacharacters becomes one literal argument
/// instead of a second command.
/// </summary>
public class ShellQuotingTests
{
    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("a; rm -rf ~", "'a; rm -rf ~'")]
    [InlineData("$(whoami)", "'$(whoami)'")]
    [InlineData("back`tick`", "'back`tick`'")]
    [InlineData("a | b > c", "'a | b > c'")]
    public void QuotePosix_WrapsTheValueSoTheShellReadsItAsOneLiteralArgument(string value, string expected) =>
        ShellQuoting.QuotePosix(value).Should().Be(expected);

    [Fact]
    public void QuotePosix_ClosesAndReopensAroundAnEmbeddedSingleQuote_TheOneCharacterItCannotHold() =>
        ShellQuoting.QuotePosix("it's").Should().Be("'it'\\''s'");

    [Theory]
    [InlineData("a & calc", "a ^& calc")]
    [InlineData("a | b", "a ^| b")]
    [InlineData("a > out", "a ^> out")]
    [InlineData("(grouped)", "^(grouped^)")]
    public void QuoteCmd_CaretEscapesTheSeparatorsThatWouldChainOrRedirectACommand(string value, string expected) =>
        ShellQuoting.QuoteCmd(value).Should().Be(expected);
}
