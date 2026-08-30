using System.Text;
using System.Text.Json;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using Cockpit.MeasurementHarness.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.MeasurementHarness.Scenarios;

/// <summary>
/// AC-1090 criterion 1: what it costs to keep a pane's transcript on disk. Replays a real conversation row by
/// row through three write strategies and counts the bytes each one hands to the file system, against the same
/// workload and on the same machine — the amplification is that total divided by what ends up stored.
/// </summary>
/// <remarks>
/// Needs a real conversation: <c>--workload=&lt;assistant-transcript*.json&gt;</c>. It refuses rather than
/// inventing one, because the shape of the rows is the whole finding (AC-1090: the bytes are flat, the row
/// count is the cost) and a synthetic workload would decide that in advance.
/// </remarks>
public static class TranscriptWriteScenario
{
    public const string Name = "transcript-write";

    // The amplification of the idiom this change removed, from the ticket's own measurement: 1310 rows, 3,6 MB
    // stored, 2,49 GB written. An instrument that reads the known-bad idiom as cheap cannot be trusted to report
    // the new one as cheap either, so the control holds this scenario to that order.
    private const double ControlFloor = 100;

    /// <summary>
    /// The known-bad idiom has to show up as expensive. It is not a separate case bolted on: it is the same
    /// measurement the run reports, held to the number the ticket already established for it.
    /// </summary>
    public static PositiveControl Control() => PositiveControl.Named(
        "rewrite-per-row reads as heavily amplified",
        recorder => Task.FromResult(recorder.ValueOf("rewrite-per-row-amplification") is { } value && value > ControlFloor));

    public static async Task RunAsync(MeasurementRun run, string workloadPath)
    {
        var rows = _LoadWorkload(workloadPath);
        var root = Path.Combine(Path.GetTempPath(), "cockpit-transcript-write", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            await run.MeasureAsync($"{rows.Count} rows from {Path.GetFileName(workloadPath)}", async recorder =>
            {
                run.Write($"workload: {workloadPath}");
                run.Write($"rows: {rows.Count:N0}");
                run.Write(string.Empty);

                _Report(run, recorder, "rewrite-per-row", _RewritePerRow(rows, Path.Combine(root, "rewrite.json"), TimeSpan.Zero),
                    "AC-684's idiom: the whole transcript re-serialised and atomically replaced on every row");

                _Report(run, recorder, "rewrite-debounced", _RewritePerRow(rows, Path.Combine(root, "debounced.json"), TimeSpan.FromSeconds(5)),
                    "the same, with AC-1151's 5s debounce — coalesced on the rows' own timestamps, so this is what "
                    + "the shipped code would have written for this conversation, not a live re-run of it");

                _Report(run, recorder, "append-only", await _AppendOnlyAsync(rows, root).ConfigureAwait(true),
                    "AC-1090's SessionTranscriptLog, debounce off so nothing is coalesced away");
            }).ConfigureAwait(true);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void _Report(MeasurementRun run, Recorder recorder, string strategy, Written written, string what)
    {
        var amplification = (double)written.Bytes / written.FinalSize;
        recorder.Measure($"{strategy}-amplification", amplification, "x");
        recorder.Measure($"{strategy}-bytes", written.Bytes, "bytes");

        run.Write($"{strategy} — {what}");
        run.Write($"  writes: {written.Writes:N0}   stored: {written.FinalSize:N0} bytes   written: {written.Bytes:N0} bytes");
        run.Write($"  amplification: {amplification:F1}x");
        run.Write(string.Empty);
    }

    private sealed record Written(int Writes, long Bytes, long FinalSize);

    // AC-684's `AssistantTranscriptFile.SaveAsync`, reconstructed: the production class it belongs to is gone, so
    // this is the one place in the harness that is not the real thing. It is held to the ticket's own number for
    // exactly that reason — a reconstruction that does not reproduce 702x on a 1310-row conversation is wrong.
    private static Written _RewritePerRow(IReadOnlyList<TranscriptSnapshotEntry> rows, string path, TimeSpan debounce)
    {
        long bytes = 0;
        var writes = 0;
        var lastWrite = DateTimeOffset.MinValue;

        for (var count = 1; count <= rows.Count; count++)
        {
            // The debounced variant writes at most once per window of conversation time, and always for the last
            // row — the flush AC-1151 forces on archive and shutdown.
            var due = debounce == TimeSpan.Zero
                || count == rows.Count
                || rows[count - 1].Timestamp - lastWrite >= debounce;
            if (!due)
            {
                continue;
            }

            lastWrite = rows[count - 1].Timestamp;
            var content = JsonSerializer.Serialize(rows.Take(count).Select(_AsAssistantShape));
            File.WriteAllText(path, content);
            bytes += Encoding.UTF8.GetByteCount(content);
            writes++;
        }

        return new Written(writes, bytes, new FileInfo(path).Length);
    }

    private static async Task<Written> _AppendOnlyAsync(IReadOnlyList<TranscriptSnapshotEntry> rows, string root)
    {
        var logRoot = Path.Combine(root, "append");
        var store = new SessionTranscriptLog(logRoot, NullLogger<SessionTranscriptLog>.Instance, TimeSpan.Zero);
        foreach (var row in rows)
        {
            await store.AppendAsync("measured-pane", row).ConfigureAwait(true);
        }

        return new Written(
            store.WriteCountForTests,
            store.BytesWrittenForTests,
            new FileInfo(store.LogPath("measured-pane")).Length);
    }

    // The eight fields AC-684 stored, so the reconstructed idiom is measured on the bytes it actually wrote —
    // not on this change's own row id and optional members.
    private static object _AsAssistantShape(TranscriptSnapshotEntry row) => new
    {
        row.Kind,
        row.Text,
        row.ToolName,
        row.InputJson,
        row.ToolUseId,
        row.ResultText,
        row.IsResultError,
        row.Timestamp,
    };

    private static IReadOnlyList<TranscriptSnapshotEntry> _LoadWorkload(string path)
    {
        if (!File.Exists(path))
        {
            throw new ArgumentException($"--workload must point at a saved transcript; '{path}' does not exist.");
        }

        var saved = JsonSerializer.Deserialize<List<SavedRow>>(File.ReadAllText(path))
            ?? throw new ArgumentException($"'{path}' is not a saved transcript.");

        return
        [
            .. saved.Select((row, index) => new TranscriptSnapshotEntry(
                index.ToString("x8"),
                row.Kind ?? "AssistantText",
                row.Text ?? string.Empty,
                row.ToolName,
                row.InputJson,
                row.ToolUseId,
                row.ResultText,
                row.IsResultError,
                row.Timestamp)),
        ];
    }

    private sealed record SavedRow(
        string? Kind,
        string? Text,
        string? ToolName,
        string? InputJson,
        string? ToolUseId,
        string? ResultText,
        bool IsResultError,
        DateTimeOffset Timestamp);
}
