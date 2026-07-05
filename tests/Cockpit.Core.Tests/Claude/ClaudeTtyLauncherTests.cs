using FluentAssertions;
using Cockpit.Infrastructure.Claude.Tty;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// Covers the one purely testable seam of <see cref="ClaudeTtyLauncher"/>: how the executable path
/// is turned into the command line handed to <c>CreateProcessW</c> (the pty spawn itself needs real
/// ConPTY + a logged-in CLI, so it is out of unit-test reach).
/// </summary>
public class ClaudeTtyLauncherTests
{
    [Fact]
    public void QuoteExecutable_WrapsPathsContainingSpaces()
    {
        var quoted = ClaudeTtyLauncher.QuoteExecutable(@"C:\Users\raymo\AppData\Roaming\Claude\claude-code\2.1.197\claude.exe");

        quoted.Should().Be(@"C:\Users\raymo\AppData\Roaming\Claude\claude-code\2.1.197\claude.exe");
    }

    [Fact]
    public void QuoteExecutable_WrapsPathWithASpaceInIt()
    {
        var quoted = ClaudeTtyLauncher.QuoteExecutable(@"C:\Program Files\Claude\claude.exe");

        quoted.Should().Be(@"""C:\Program Files\Claude\claude.exe""");
    }

    [Fact]
    public void QuoteExecutable_LeavesABarePathUnquoted()
    {
        var quoted = ClaudeTtyLauncher.QuoteExecutable("claude");

        quoted.Should().Be("claude");
    }

    [Fact]
    public void QuoteExecutable_DoesNotDoubleQuoteAnAlreadyQuotedPath()
    {
        var quoted = ClaudeTtyLauncher.QuoteExecutable(@"""C:\Program Files\Claude\claude.exe""");

        quoted.Should().Be(@"""C:\Program Files\Claude\claude.exe""");
    }
}
