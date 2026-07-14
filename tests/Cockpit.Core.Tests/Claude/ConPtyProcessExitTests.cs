using System.Collections;
using FluentAssertions;
using Cockpit.Infrastructure.Sessions.Tty;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// Covers the Windows-only exit detection on <see cref="ConPtyProcess"/>: unlike a Unix pty master, a
/// ConPTY holds the output pipe's write end open after the child exits, so without watching the child
/// process handle the reader would block forever and the TTY panel would never learn to close (the
/// "status stuck on Done, session never closes" bug). This spawns a real short-lived process in a real
/// ConPTY and asserts the output stream reaches EOF once it exits — the regression is that this read
/// never returns and the test times out.
/// </summary>
public class ConPtyProcessExitTests
{
    [Fact]
    public async Task OutputStream_ReachesEof_AfterTheChildProcessExits()
    {
        if (!OperatingSystem.IsWindows())
        {
            // ConPTY is Windows-only (kernel32 CreatePseudoConsole); the Unix host is PortaPtyProcess.
            return;
        }

        // cmd.exe /c exit terminates immediately after the ConPTY spawns it.
        var comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        var commandLine = ConPtyHostFactory.BuildCommandLine(comSpec, ["/c", "exit"]);

        using var pty = ConPtyProcess.Start(
            commandLine,
            Environment.CurrentDirectory,
            CurrentEnvironment(),
            columns: 80,
            rows: 24);

        // Drain the output until EOF (read == 0). Without exit detection this loop never ends because the
        // ConPTY keeps the pipe's write end open past the child's exit, so the read blocks and the linked
        // timeout cancels it — surfacing the bug as a failed test instead of a hung panel.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var buffer = new byte[4096];
        var reachedEof = false;
        try
        {
            while (true)
            {
                var read = await pty.OutputStream.ReadAsync(buffer.AsMemory(), timeout.Token);
                if (read <= 0)
                {
                    reachedEof = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Left as reachedEof == false: the read never completed, which is exactly the regression.
        }

        reachedEof.Should().BeTrue(
            "closing the pseudo console on the child's exit must EOF the output reader so the TTY panel closes");
    }

    // The child needs the real process environment (PATH, SystemRoot, ...) to run; ConPTY inherits nothing
    // implicitly, so hand it exactly what this process has, de-duplicated case-insensitively as the block
    // builder expects.
    private static Dictionary<string, string> CurrentEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                environment[key] = value;
            }
        }

        return environment;
    }
}
