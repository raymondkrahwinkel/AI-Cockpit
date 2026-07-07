using FluentAssertions;
using Cockpit.Infrastructure.Claude.Tty;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// Covers the one purely testable seam of <see cref="ConPtyHostFactory"/>: how the executable path
/// and launch arguments are turned into the single command-line string handed to
/// <c>CreateProcessW</c> (the pty spawn itself needs a real ConPTY on Windows, so it is out of
/// unit-test reach here).
/// </summary>
public class ConPtyHostFactoryTests
{
    [Fact]
    public void QuoteIfNeeded_LeavesAPathWithoutSpacesUnquoted()
    {
        var quoted = ConPtyHostFactory.QuoteIfNeeded(@"C:\Users\raymo\AppData\Roaming\Claude\claude-code\2.1.197\claude.exe");

        quoted.Should().Be(@"C:\Users\raymo\AppData\Roaming\Claude\claude-code\2.1.197\claude.exe");
    }

    [Fact]
    public void QuoteIfNeeded_WrapsAPathWithASpaceInIt()
    {
        var quoted = ConPtyHostFactory.QuoteIfNeeded(@"C:\Program Files\Claude\claude.exe");

        quoted.Should().Be(@"""C:\Program Files\Claude\claude.exe""");
    }

    [Fact]
    public void QuoteIfNeeded_LeavesABareTokenUnquoted()
    {
        var quoted = ConPtyHostFactory.QuoteIfNeeded("claude");

        quoted.Should().Be("claude");
    }

    [Fact]
    public void QuoteIfNeeded_DoesNotDoubleQuoteAnAlreadyQuotedToken()
    {
        var quoted = ConPtyHostFactory.QuoteIfNeeded(@"""C:\Program Files\Claude\claude.exe""");

        quoted.Should().Be(@"""C:\Program Files\Claude\claude.exe""");
    }

    [Fact]
    public void BuildCommandLine_WithNoArguments_IsJustTheExecutable()
    {
        var commandLine = ConPtyHostFactory.BuildCommandLine("claude", []);

        commandLine.Should().Be("claude");
    }

    [Fact]
    public void BuildCommandLine_AppendsEachArgumentSpaceSeparated()
    {
        var commandLine = ConPtyHostFactory.BuildCommandLine(
            "claude",
            ["--permission-mode", "acceptEdits", "--model", "opus", "--effort", "high"]);

        commandLine.Should().Be("claude --permission-mode acceptEdits --model opus --effort high");
    }

    [Fact]
    public void BuildCommandLine_QuotesAnExecutablePathWithASpace()
    {
        var commandLine = ConPtyHostFactory.BuildCommandLine(
            @"C:\Program Files\Claude\claude.exe",
            ["--model", "opus"]);

        commandLine.Should().Be(@"""C:\Program Files\Claude\claude.exe"" --model opus");
    }
}
