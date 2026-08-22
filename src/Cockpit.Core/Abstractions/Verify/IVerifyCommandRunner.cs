using Cockpit.Core.Verify;

namespace Cockpit.Core.Abstractions.Verify;

/// <summary>
/// Runs a verify runner's registered command as a child process (AC-86) and returns its <see cref="VerifyRunResult"/>.
/// A seam of its own so the tool orchestrating a verify run (find runner, consent, run, feed back) can be tested
/// without spawning a real process, exercising the "verify only ever runs a registered command" boundary in isolation from the OS.
/// </summary>
public interface IVerifyCommandRunner
{
    Task<VerifyRunResult> RunAsync(VerifyRunner runner, CancellationToken cancellationToken = default);
}
