namespace Cockpit.Core.Abstractions.Shell;

/// <summary>
/// Runs one command as administrator through the Windows UAC prompt and hands its output back (AC-1335). A seam of
/// its own so the MCP tool can be tested without a live prompt.
/// </summary>
public interface IElevatedCommandRunner
{
    /// <summary>
    /// Whether this platform can elevate at all — Windows only.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// The exact process the tool would start for <paramref name="command"/>: executable, argument string and the
    /// output file the command is redirected to. Composed here so the operator approves the literal command line.
    /// </summary>
    ElevatedCommandPlan Plan(string command);

    /// <summary>
    /// Starts <paramref name="plan"/> elevated in <paramref name="workingDirectory"/> and waits for it, at most
    /// <paramref name="timeout"/>. Never throws for a declined prompt or a failed start — those are outcomes.
    /// </summary>
    Task<ElevatedCommandResult> RunAsync(ElevatedCommandPlan plan, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken = default);
}
