using FluentAssertions;
using Cockpit.Infrastructure.Sessions.Tty;

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
    public void QuoteArgument_LeavesAPathWithoutSpacesUnquoted()
    {
        var quoted = ConPtyHostFactory.QuoteArgument(@"C:\Users\raymo\AppData\Roaming\Claude\claude-code\2.1.197\claude.exe");

        quoted.Should().Be(@"C:\Users\raymo\AppData\Roaming\Claude\claude-code\2.1.197\claude.exe");
    }

    [Fact]
    public void QuoteArgument_WrapsAPathWithASpaceInIt()
    {
        var quoted = ConPtyHostFactory.QuoteArgument(@"C:\Program Files\Claude\claude.exe");

        quoted.Should().Be(@"""C:\Program Files\Claude\claude.exe""");
    }

    [Fact]
    public void QuoteArgument_LeavesABareTokenUnquoted()
    {
        var quoted = ConPtyHostFactory.QuoteArgument("claude");

        quoted.Should().Be("claude");
    }

    /// <summary>
    /// The regression that matters: <c>--settings &lt;json&gt;</c> (the statusline relay) is an argument
    /// full of double quotes and spaces. The old "wrap it in quotes when it has a space" quoting left the
    /// embedded quotes unescaped, so <c>CommandLineToArgvW</c> split the JSON at its first space and handed
    /// <c>claude.exe</c> broken argv — which exited on the spot, closing every new TTY panel instantly.
    /// Correct escaping doubles nothing here (no backslash runs) but escapes each embedded quote as <c>\"</c>.
    /// </summary>
    [Fact]
    public void QuoteArgument_EscapesEmbeddedDoubleQuotes()
    {
        var quoted = ConPtyHostFactory.QuoteArgument(@"{""statusLine"":{""type"":""command""}}");

        quoted.Should().Be(@"""{\""statusLine\"":{\""type\"":\""command\""}}""");
    }

    /// <summary>
    /// The subtle half of the algorithm: a run of backslashes is literal on its own, but when it precedes a
    /// quote (or the closing quote) each backslash must be doubled so the parser does not read them as
    /// escaping that quote. A lone <c>\"</c> token therefore becomes <c>"\\\""</c>.
    /// </summary>
    [Fact]
    public void QuoteArgument_DoublesBackslashesThatPrecedeAQuote()
    {
        var quoted = ConPtyHostFactory.QuoteArgument(@"\""");

        quoted.Should().Be(@"""\\\""""");
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

    [Fact]
    public void BuildCommandLine_EscapesASettingsJsonArgument()
    {
        var commandLine = ConPtyHostFactory.BuildCommandLine(
            "claude",
            ["--settings", @"{""statusLine"":{""type"":""command""}}"]);

        commandLine.Should().Be(@"claude --settings ""{\""statusLine\"":{\""type\"":\""command\""}}""");
    }
}
