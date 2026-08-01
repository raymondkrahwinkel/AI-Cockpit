using System.Collections.Concurrent;
using System.Diagnostics;

namespace Cockpit.Plugin.ClaudeProvider;

/// <summary>
/// Asks the CLI to refresh the account's cached allowances, so <see cref="ClaudeUsageCache"/> has something recent
/// to read on the SDK route (AC-549).
/// <para>
/// It runs <c>claude -p "/usage"</c>, which the CLI answers out of its own state: measured on 2.1.220 at
/// <c>total_cost_usd</c> 0, <c>duration_api_ms</c> 0, <c>num_turns</c> 0 and zero tokens in every bucket. The
/// answer itself is prose and deliberately ignored — the side effect is the point, because the same call rewrites
/// <c>cachedUsageUtilization</c> in <c>.claude.json</c> as structured JSON, and parsing numbers beats parsing an
/// English sentence that a future release may word differently.
/// </para>
/// </summary>
internal static class ClaudeUsageRefresh
{
    /// <summary>
    /// How often an account may be re-asked. The allowances move slowly and the figures are account-wide, so this
    /// is deliberately longer than a session would poll on its own — see <see cref="ClaudeUsageCache.MaxAge"/>,
    /// which this must stay comfortably under or every reading expires before the next refresh.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Per account, not per session: the five-hour and weekly windows belong to the login, so ten open sessions on
    /// one profile must cost one subprocess between them rather than ten. Keyed by the config directory, which is
    /// what decides whose <c>.claude.json</c> the CLI reads.
    /// </summary>
    private static readonly ConcurrentDictionary<string, _Account> _Accounts = new(StringComparer.OrdinalIgnoreCase);

    private sealed class _Account
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public DateTimeOffset LastRefresh = DateTimeOffset.MinValue;
    }

    /// <summary>
    /// Refreshes this account's snapshot if the last one is old enough, and does nothing at all otherwise. Never
    /// throws: a usage figure is a nicety, and a CLI that is missing, busy or slow must not disturb the session
    /// that happened to ask.
    /// </summary>
    public static async Task RefreshAsync(
        string cliPath,
        string? configDirectoryOverride,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cliPath) || !File.Exists(cliPath))
        {
            return;
        }

        var account = _Accounts.GetOrAdd(configDirectoryOverride ?? string.Empty, _ => new _Account());
        if (now - account.LastRefresh < Interval || !await account.Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            // Re-checked inside the gate: two sessions can both pass the cheap check above before either takes it.
            if (now - account.LastRefresh < Interval)
            {
                return;
            }

            if (await _AskAsync(cliPath, configDirectoryOverride, cancellationToken).ConfigureAwait(false))
            {
                account.LastRefresh = now;
            }
        }
        catch (Exception)
        {
            // Left un-stamped on purpose, so a failure is retried on the next turn rather than waiting out the
            // interval as if it had succeeded.
        }
        finally
        {
            account.Gate.Release();
        }
    }

    private static async Task<bool> _AskAsync(string cliPath, string? configDirectoryOverride, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(cliPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add("/usage");

        if (!string.IsNullOrWhiteSpace(configDirectoryOverride))
        {
            startInfo.Environment[ClaudeConfigPaths.EnvironmentVariable] = configDirectoryOverride;
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return false;
        }

        // stdin closed at once: without it the CLI waits three seconds for piped input it is never going to get
        // (measured — it says so on stderr), which is three seconds of a subprocess doing nothing.
        process.StandardInput.Close();

        // Both pipes drained while waiting. A child that fills one and blocks is the failure mode that turned a
        // Voice-suite test flaky in this repo; the output is discarded, but it still has to be read.
        var drain = Task.WhenAll(
            process.StandardOutput.ReadToEndAsync(cancellationToken),
            process.StandardError.ReadToEndAsync(cancellationToken));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await drain.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Already gone.
            }

            return false;
        }

        return process.ExitCode == 0;
    }
}
