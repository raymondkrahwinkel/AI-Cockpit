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
    /// <summary>
    /// One argument, quoted the way <c>CommandLineToArgvW</c> reads it back. The last two rows matter most.
    /// <c>--settings &lt;json&gt;</c> is full of quotes and spaces: the old "wrap it when it has a space" quoting
    /// left them unescaped, so the parser split the JSON at its first space and handed <c>claude.exe</c> broken
    /// argv, which exited on the spot and closed every new TTY panel. And a backslash run is literal on its own
    /// but must be doubled before a quote, or the parser reads it as escaping that quote.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Users\raymo\AppData\Roaming\Claude\claude-code\2.1.197\claude.exe", @"C:\Users\raymo\AppData\Roaming\Claude\claude-code\2.1.197\claude.exe")]
    [InlineData("claude", "claude")]
    [InlineData(@"C:\Program Files\Claude\claude.exe", @"""C:\Program Files\Claude\claude.exe""")]
    [InlineData(@"{""statusLine"":{""type"":""command""}}", @"""{\""statusLine\"":{\""type\"":\""command\""}}""")]
    [InlineData(@"\""", @"""\\\""""")]
    public void QuoteArgument_QuotesOnlyWhatNeedsIt_AndEscapesWhatWouldBeReadAsSyntax(string argument, string expected)
    {
        Assert.Equal(expected, ConPtyHostFactory.QuoteArgument(argument));
    }

    [Theory]
    [InlineData("claude", new string[0], "claude")]
    [InlineData("claude", new[] { "--permission-mode", "acceptEdits", "--model", "opus", "--effort", "high" }, "claude --permission-mode acceptEdits --model opus --effort high")]
    [InlineData(@"C:\Program Files\Claude\claude.exe", new[] { "--model", "opus" }, @"""C:\Program Files\Claude\claude.exe"" --model opus")]
    [InlineData("claude", new[] { "--settings", @"{""statusLine"":{""type"":""command""}}" }, @"claude --settings ""{\""statusLine\"":{\""type\"":\""command\""}}""")]
    public void BuildCommandLine_JoinsTheQuotedExecutableAndArgumentsWithSpaces(string executable, string[] arguments, string expected)
    {
        Assert.Equal(expected, ConPtyHostFactory.BuildCommandLine(executable, arguments));
    }
}
