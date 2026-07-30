using System.Text;
using Cockpit.Infrastructure.Sessions.Tty;

namespace Cockpit.Infrastructure.Tests.Sessions;

/// <summary>
/// AC-129: a program that writes a plain <c>\n</c> — `ls`, `git status`, any command that does not emit its own
/// carriage return — must reach the terminal as CRLF, or the cursor drops a row while holding its column and the
/// output comes out as a staircase. The pty Porta.Pty hands back has that post-processing switched off, so this
/// drives a real pty through the real spawn path and reads what actually comes out of it.
/// </summary>
public class PortaPtyLineDisciplineTests
{
    // Generous on purpose: this waits on a forked child under a full test run, where scheduling is the slowest
    // part. The assertion is about the bytes, never about how quickly they arrive, so a tight budget here would
    // only buy flakiness — which it did at 2s per read.
    private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task SpawnedPty_TranslatesNewlinesForTheTerminal()
    {
        // OperatingSystem.IsLinux() (not RuntimeInformation) is the guard the platform-compatibility analyzer
        // understands. The spawn path and these termios values are Linux-only; elsewhere there is nothing to assert.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var pty = PortaPtyProcess.Start(
            "/bin/sh",
            ["-c", "printf 'AA\\nBB\\n'"],
            Path.GetTempPath(),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["PATH"] = "/usr/bin:/bin" },
            columns: 80,
            rows: 24);

        var output = await _ReadUntilBothLinesArriveAsync(pty);

        Assert.Contains("AA\r\nBB\r\n", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The modes are applied by a shell that then <c>exec</c>s the real program, so this drives the path an
    /// interactive session actually takes: type a line in, read the result back. It pins the input side too — the
    /// echo and line editing that <c>ICANON</c>/<c>ECHO</c> carry — which a one-shot command never exercises.
    /// </summary>
    [Fact]
    public async Task InteractiveShell_EchoesTypedInputAndAnswersWithCrLf()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var pty = PortaPtyProcess.Start(
            "/bin/sh",
            ["-i"],
            Path.GetTempPath(),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PATH"] = "/usr/bin:/bin",
                ["PS1"] = "$ ",
            },
            columns: 80,
            rows: 24);

        await pty.InputStream.WriteAsync("echo marker\n"u8.ToArray());
        await pty.InputStream.FlushAsync();

        var output = await _ReadUntilAsync(pty, "marker\r\n");

        Assert.Contains("marker\r\n", output, StringComparison.Ordinal);
    }

    private static Task<string> _ReadUntilBothLinesArriveAsync(PortaPtyProcess pty) =>
        _ReadUntilAsync(pty, "BB");

    private static async Task<string> _ReadUntilAsync(PortaPtyProcess pty, string sentinel)
    {
        var collected = new StringBuilder();
        var buffer = new byte[1024];
        using var deadline = new CancellationTokenSource(ReadBudget);

        while (!deadline.IsCancellationRequested)
        {
            int read;
            try
            {
                // Raced against the deadline rather than cancelled through the token: a read on a pty's master
                // does not honour cancellation — it sits in the syscall until bytes arrive. Passing only the token
                // makes a regression hang the whole suite instead of failing it, which is strictly worse than a
                // red test. Whatever arrived so far is what the assertion then judges.
                var reading = pty.OutputStream.ReadAsync(buffer, CancellationToken.None).AsTask();
                var finished = await Task.WhenAny(reading, Task.Delay(ReadBudget, deadline.Token));
                if (finished != reading)
                {
                    break;
                }

                read = await reading;
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException)
            {
                // The master reports EIO once the child is gone — the normal end of a pty on Linux.
                break;
            }

            if (read <= 0)
            {
                break;
            }

            collected.Append(Encoding.UTF8.GetString(buffer, 0, read));
            if (collected.ToString().Contains(sentinel, StringComparison.Ordinal))
            {
                break;
            }
        }

        return collected.ToString();
    }
}
