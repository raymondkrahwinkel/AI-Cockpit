using System.ComponentModel;
using System.Diagnostics;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Shell;
using Cockpit.Core.Terminal;
using Cockpit.Infrastructure.Sessions.Tty;

namespace Cockpit.Infrastructure.Shell;

// AC-1335: one PowerShell command as administrator, started through the same runas point as the administrator
// terminal. No pipe can cross the integrity boundary, so the command's whole output stream (`*>`) goes to a file
// in %TEMP% that the cockpit reads back once the process has ended. Nothing is cached and nothing is remembered.
internal sealed class ElevatedCommandRunner : IElevatedCommandRunner, ISingletonService
{
    private static readonly TimeSpan KillSettleTimeout = TimeSpan.FromSeconds(5);

    public bool IsSupported => ElevatedProcessStart.IsSupported;

    public ElevatedCommandPlan Plan(string command)
    {
        var outputFile = Path.Combine(Path.GetTempPath(), $"cockpit-elevated-{Guid.NewGuid():N}.txt");
        return new ElevatedCommandPlan(
            Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            Compose(command, outputFile),
            outputFile);
    }

    // `-NoProfile`/`-NonInteractive` are the cockpit's, not the agent's (AC-969 requirement 9): no profile script
    // runs elevated, and a command that would wait for a keyboard errors out instead of hanging behind a hidden
    // window. The script is one argv token, escaped for `CommandLineToArgvW`, so quotes in the command survive.
    internal static string Compose(string command, string outputFile) =>
        $"-NoProfile -NonInteractive -Command {ConPtyHostFactory.QuoteArgument($"& {{ {command} }} *> '{outputFile.Replace("'", "''")}'")}";

    public async Task<ElevatedCommandResult> RunAsync(ElevatedCommandPlan plan, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
        {
            return new ElevatedCommandResult(ElevatedCommandOutcome.Failed, null, string.Empty, "Running a command as administrator is a Windows-only action.");
        }

        var start = new ProcessStartInfo(plan.Executable)
        {
            Arguments = plan.Arguments,
            WorkingDirectory = workingDirectory,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        // Created here, at medium integrity, so the file is the operator's own before the elevated process
        // overwrites it — and an empty file afterwards means "ran and wrote nothing", not "never started".
        File.WriteAllText(plan.OutputFile, string.Empty);

        using var process = ElevatedProcessStart.Start(start, out var declined, out var startError);
        if (process is null)
        {
            File.Delete(plan.OutputFile);
            return declined
                ? new ElevatedCommandResult(ElevatedCommandOutcome.Declined, null, string.Empty)
                : new ElevatedCommandResult(ElevatedCommandOutcome.Failed, null, string.Empty, startError);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        string? error = null;
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            error = _TryKill(process);
        }

        var output = _ReadAndDeleteOutput(plan.OutputFile, ref error);
        var exitCode = process.HasExited ? process.ExitCode : (int?)null;
        return new ElevatedCommandResult(timedOut ? ElevatedCommandOutcome.TimedOut : ElevatedCommandOutcome.Completed, exitCode, output, error);
    }

    // The handle ShellExecuteEx returned carries PROCESS_TERMINATE against the high-IL child (measured 2026-09-21,
    // unlike a fresh OpenProcess — AC-925); should that ever not hold, the operator is told which process to close.
    // The bounded wait is what lets the output file be read: Kill returns before the file handle is released.
    private static string? _TryKill(Process process)
    {
        try
        {
            process.Kill();
            process.WaitForExit(KillSettleTimeout);
            return "Timed out; the elevated process was ended.";
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return $"Timed out and the elevated process (PID {process.Id}) could not be ended from the cockpit: {exception.Message} Close it yourself.";
        }
    }

    private static string _ReadAndDeleteOutput(string outputFile, ref string? error)
    {
        try
        {
            if (!File.Exists(outputFile))
            {
                return string.Empty;
            }

            var output = File.ReadAllText(outputFile);
            File.Delete(outputFile);
            return output;
        }
        catch (IOException exception)
        {
            // A process that outlived its timeout still holds the file open: report where it is, do not hide it.
            error = $"{error} Could not read or remove the output file '{outputFile}': {exception.Message}".Trim();
            return string.Empty;
        }
    }
}
