using Cockpit.Infrastructure.Shell;

namespace Cockpit.Infrastructure.Tests.Shell;

// AC-1335: the argument string is what the operator approves and what `powershell.exe` parses, so it is pinned
// literally: the cockpit's own `-NoProfile -NonInteractive`, one script block, the whole stream redirected to the
// named file, and quotes in the command escaped the way `CommandLineToArgvW` undoes them.
public sealed class ElevatedCommandRunnerTests
{
    [Theory]
    [InlineData("Get-Date", @"C:\t\out.txt", @"-NoProfile -NonInteractive -Command ""& { Get-Date } *> 'C:\t\out.txt'""")]
    [InlineData(@"Write-Output ""a b""", @"C:\t\out.txt", @"-NoProfile -NonInteractive -Command ""& { Write-Output \""a b\"" } *> 'C:\t\out.txt'""")]
    [InlineData(@"Get-Item ""C:\Program Files\""", @"C:\t\out.txt", @"-NoProfile -NonInteractive -Command ""& { Get-Item \""C:\Program Files\\\"" } *> 'C:\t\out.txt'""")]
    [InlineData("Get-Date", @"C:\Users\O'Brien\out.txt", @"-NoProfile -NonInteractive -Command ""& { Get-Date } *> 'C:\Users\O''Brien\out.txt'""")]
    [InlineData("Write-Output \"a`tb\"; $x = 1", @"C:\t\out.txt", @"-NoProfile -NonInteractive -Command ""& { Write-Output \""a`tb\""; $x = 1 } *> 'C:\t\out.txt'""")]
    public void Compose_WrapsTheCommandInOneNonInteractiveScriptBlock_RedirectedToTheFile(string command, string outputFile, string expected)
    {
        Assert.Equal(expected, ElevatedCommandRunner.Compose(command, outputFile));
    }
}
