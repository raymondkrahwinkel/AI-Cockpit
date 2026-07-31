using System.Diagnostics;
using Cockpit.Infrastructure.Voice;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// <see cref="ProcessStderrTail"/> (AC-534): the bounded buffer in isolation, then a real child process to prove
/// the actual failure mode the ticket names — an unread, redirected stderr stream blocks the writing child once
/// the OS pipe buffer (~64 KB on Linux) fills, which is why draining must be continuous, not just present.
/// </summary>
public class ProcessStderrTailTests
{
    [Fact]
    public void Snapshot_NothingReceived_ReturnsEmpty()
    {
        var tail = new ProcessStderrTail();

        Assert.Equal(string.Empty, tail.Snapshot());
    }

    [Fact]
    public void OnLine_NullLine_IsIgnored()
    {
        // Process.ErrorDataReceived raises Data == null once for the stream's EOF — must not become a phantom
        // blank line in the tail.
        var tail = new ProcessStderrTail();
        tail.OnLine("real line");

        tail.OnLine(null);

        Assert.Equal("real line", tail.Snapshot());
    }

    [Fact]
    public void OnLine_FewLines_SnapshotJoinsThemInOrder()
    {
        var tail = new ProcessStderrTail();
        tail.OnLine("first");
        tail.OnLine("second");
        tail.OnLine("third");

        Assert.Equal($"first{Environment.NewLine}second{Environment.NewLine}third", tail.Snapshot());
    }

    [Fact]
    public void OnLine_MoreThanMaxLines_DropsTheOldestFirst()
    {
        // 5 extra lines pushed in beyond the cap: indices 0..4 must be evicted, 5..(MaxLines+4) must survive.
        var tail = new ProcessStderrTail();
        for (var i = 0; i < ProcessStderrTail.MaxLines + 5; i++)
        {
            tail.OnLine($"line-{i}");
        }

        var snapshot = tail.Snapshot();

        Assert.DoesNotContain("line-4", snapshot, StringComparison.Ordinal); // the last evicted line
        Assert.Contains("line-5", snapshot, StringComparison.Ordinal); // the first surviving line
        Assert.Contains($"line-{ProcessStderrTail.MaxLines + 4}", snapshot, StringComparison.Ordinal); // the most recent
        Assert.Equal(ProcessStderrTail.MaxLines, snapshot.Split(Environment.NewLine).Length);
    }

    [Fact]
    public void OnLine_ManyBytes_StaysAtOrUnderTheByteCap()
    {
        var tail = new ProcessStderrTail();
        for (var i = 0; i < 50; i++)
        {
            tail.OnLine(new string('x', 500));
        }

        Assert.True(tail.Snapshot().Length <= ProcessStderrTail.MaxChars,
            $"Snapshot grew to {tail.Snapshot().Length} chars, over the {ProcessStderrTail.MaxChars}-character cap.");
    }

    /// <summary>
    /// The case this type exists for: a native runtime that dumps its whole state on one line. Cutting that line
    /// to fit keeps the answer; evicting it to get under the cap would leave an empty tail at the one moment the
    /// tail was supposed to explain something.
    /// </summary>
    [Fact]
    public void OnLine_WithASingleLineLongerThanTheWholeBudget_KeepsItTruncatedRatherThanDroppingEverything()
    {
        var tail = new ProcessStderrTail();
        tail.OnLine("CUDA error: out of memory");
        tail.OnLine("ggml_abort: " + new string('X', ProcessStderrTail.MaxChars * 3));

        var snapshot = tail.Snapshot();

        Assert.NotEqual(string.Empty, snapshot);
        Assert.Contains("ggml_abort: ", snapshot, StringComparison.Ordinal);
        Assert.EndsWith(ProcessStderrTail.TruncationMarker, snapshot, StringComparison.Ordinal);
        Assert.True(snapshot.Length <= ProcessStderrTail.MaxChars,
            $"Snapshot grew to {snapshot.Length} chars, over the {ProcessStderrTail.MaxChars}-character cap.");
    }

    /// <summary>
    /// The measured proof behind AC-534: a child that writes far more than a pipe buffer's worth of stderr must
    /// not block the parent, and what it drains must actually be the tail (the most recent lines), not whatever
    /// happened to arrive first before some earlier line got evicted.
    /// </summary>
    [Fact]
    public void RealChildWritingHundredsOfKbToStderr_DoesNotBlock_AndSnapshotHoldsTheMostRecentLines()
    {
        if (OperatingSystem.IsWindows())
        {
            // The POSIX shell loop below needs /bin/sh; the mechanism (an unread redirected stderr pipe
            // blocking the writer) is proven on the Linux CI runner regardless.
            return;
        }

        // Each line is delimited (L000001L) rather than zero-padded on its own, so a later assertion can look for
        // one exact line number without a shorter one accidentally matching as a substring of a longer one.
        // ~110 KB at ~90 bytes/line: past the ~64 KB Linux pipe buffer, which is all this has to prove, and no
        // further. An earlier 6000-line version wrote ~540 KB and put enough scheduling pressure on the run to
        // knock over a timing-sensitive test in another namespace (measured: 2 failures in 10 full-suite runs,
        // none once this test was excluded). A test that loads the machine is a test that fails other people's.
        const int lineCount = 1200;
        const string filler = "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";
        var script = $"i=1; while [ $i -le {lineCount} ]; do printf 'L%06dL-{filler}\\n' $i 1>&2; i=$((i+1)); done";

        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);

        var tail = new ProcessStderrTail();
        using var process = new Process { StartInfo = startInfo };
        process.ErrorDataReceived += (_, args) => tail.OnLine(args.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // A generous budget for CI noise; the point under test is "finishes at all", not a tight latency bound.
        var exited = process.WaitForExit(TimeSpan.FromSeconds(10));
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
        }

        Assert.True(exited, "The child did not exit — its stderr was not actually being drained.");

        // The timed overload returns as soon as the process is gone, while the asynchronous reader may still be
        // handing over its last lines — the parameterless call is the documented way to wait for those handlers to
        // drain. Without it this assertion read a snapshot that stopped a few hundred lines short, and failed on
        // roughly half the runs.
        process.WaitForExit();

        var snapshot = tail.Snapshot();
        Assert.DoesNotContain("L000001L", snapshot, StringComparison.Ordinal); // the first line was evicted
        Assert.Contains($"L{lineCount:D6}L", snapshot, StringComparison.Ordinal); // the last line survived
    }
}
