using FluentAssertions;
using Cockpit.Infrastructure.Claude.Tty;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// Covers the one purely testable seam of <see cref="ConPtyHostFactory"/>: how the executable path
/// is turned into the command line handed to <c>CreateProcessW</c> (the pty spawn itself needs a
/// real ConPTY on Windows, so it is out of unit-test reach here).
/// </summary>
public class ConPtyHostFactoryTests
{
    [Fact]
    public void QuoteExecutable_WrapsPathsContainingSpaces()
    {
        var quoted = ConPtyHostFactory.QuoteExecutable(@"C:\Users\raymo\AppData\Roaming\Claude\claude-code\2.1.197\claude.exe");

        quoted.Should().Be(@"C:\Users\raymo\AppData\Roaming\Claude\claude-code\2.1.197\claude.exe");
    }

    [Fact]
    public void QuoteExecutable_WrapsPathWithASpaceInIt()
    {
        var quoted = ConPtyHostFactory.QuoteExecutable(@"C:\Program Files\Claude\claude.exe");

        quoted.Should().Be(@"""C:\Program Files\Claude\claude.exe""");
    }

    [Fact]
    public void QuoteExecutable_LeavesABarePathUnquoted()
    {
        var quoted = ConPtyHostFactory.QuoteExecutable("claude");

        quoted.Should().Be("claude");
    }

    [Fact]
    public void QuoteExecutable_DoesNotDoubleQuoteAnAlreadyQuotedPath()
    {
        var quoted = ConPtyHostFactory.QuoteExecutable(@"""C:\Program Files\Claude\claude.exe""");

        quoted.Should().Be(@"""C:\Program Files\Claude\claude.exe""");
    }
}
