using System.Collections;
using FluentAssertions;
using Cockpit.Infrastructure.Sessions.Tty;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// Regression: a child spawned inside a ConPTY must see its std streams as an interactive terminal
/// (isTTY=true), even when the cockpit's own standard handles are pipes — which is what <c>dotnet run</c>,
/// <c>dotnet test</c>, or any parent that captures the app's stdout hand down. Without the fix, the child
/// inherited those pipe handles for stdin/stdout/stderr, so <c>claude</c> saw <c>isatty(stdin)=false</c>,
/// fell back to <c>--print</c> mode and exited with "Input must be provided ... when using --print",
/// closing every new TTY panel the instant it opened. It reproduced only when launched with piped std
/// handles (a terminal launch hands down console handles, so it worked there). The probe uses Node —
/// claude is itself a Node process, so <c>process.stdin.isTTY</c> is the exact signal it reads — and writes
/// the verdict to a file so a fast-exiting child's output cannot be lost to the ConPTY teardown race.
/// </summary>
public class TtyStdinIsConsoleProbeTests
{
    [Fact]
    public async Task ConPtyChild_SeesAllStdStreamsAsATty_EvenWhenTheCockpitsOwnHandlesArePipes()
    {
        if (!OperatingSystem.IsWindows())
        {
            // ConPTY is Windows-only; the Unix host (PortaPtyProcess) always hands the child a real pty.
            return;
        }

        if (ResolveOnPath("node.exe") is null)
        {
            // The probe needs Node to ask process.stdin.isTTY; skip where it is not installed.
            return;
        }

        var resultPath = Path.Combine(Path.GetTempPath(), $"cockpit_conpty_probe_{Guid.NewGuid():N}.txt");
        var commandLine = ConPtyHostFactory.BuildCommandLine(
            "node",
            ["-e", "require('fs').writeFileSync(process.argv[1],'IN='+(process.stdin.isTTY===true)+' OUT='+(process.stdout.isTTY===true)+' ERR='+(process.stderr.isTTY===true))", resultPath]);

        using var pty = ConPtyProcess.Start(
            commandLine,
            Environment.CurrentDirectory,
            CurrentEnvironment(),
            columns: 80,
            rows: 24);

        // Drain to EOF so the child has exited before we read its verdict file.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var buffer = new byte[4096];
        try
        {
            while (await pty.OutputStream.ReadAsync(buffer.AsMemory(), timeout.Token) > 0)
            {
            }
        }
        catch (OperationCanceledException)
        {
        }

        for (var i = 0; i < 50 && !File.Exists(resultPath); i++)
        {
            await Task.Delay(20);
        }

        var verdict = File.Exists(resultPath) ? await File.ReadAllTextAsync(resultPath) : "<no result file>";
        try
        {
            verdict.Should().Contain("IN=true", "stdin must be a tty; node wrote: <<<{0}>>>", verdict);
            verdict.Should().Contain("OUT=true", "stdout must be a tty; node wrote: <<<{0}>>>", verdict);
            verdict.Should().Contain("ERR=true", "stderr must be a tty; node wrote: <<<{0}>>>", verdict);
        }
        finally
        {
            if (File.Exists(resultPath))
            {
                File.Delete(resultPath);
            }
        }
    }

    private static string? ResolveOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (path is null)
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), executable);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

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
