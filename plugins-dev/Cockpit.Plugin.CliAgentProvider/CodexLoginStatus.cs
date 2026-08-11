using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Cockpit.Plugin.CliAgentProvider;

// Whether a Codex profile is logged in, per `codex login status`'s exit code (AC-713). No `--json` output exists
// (confirmed via `codex login --help`) — exit 0 answers "logged in"; any other exit (empirically 1, with
// "Not logged in" on stderr for a fresh profile) answers "logged out". Cached for the same reason as
// `ClaudeLoginStatus`: `TtyProviderRegistration.IsLoggedIn`/`SessionProviderRegistration.IsLoggedIn` are
// synchronous on the UI thread, once per profile, and the CLI costs a subprocess.
internal static class CodexLoginStatus
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan _Timeout = TimeSpan.FromSeconds(30);

    // A CLI too old for `login status` never starts answering; without this every gate call re-spawns it.
    private static readonly TimeSpan _RetryAfterFailure = TimeSpan.FromMinutes(5);

    // Keyed by config directory (CODEX_HOME) — that is what decides whose login the CLI reports.
    private static readonly ConcurrentDictionary<string, _Entry> _Cache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record _Reading(bool LoggedIn, DateTimeOffset AsOf);

    private sealed class _Entry
    {
        private volatile _Reading? _reading;
        private long _retryNotBeforeTicks;
        public int Refreshing;

        public _Reading? Reading
        {
            get => _reading;
            set => _reading = value;
        }

        public DateTimeOffset RetryNotBefore
        {
            get => new(Interlocked.Read(ref _retryNotBeforeTicks), TimeSpan.Zero);
            set => Interlocked.Exchange(ref _retryNotBeforeTicks, value.UtcTicks);
        }
    }

    // The gate the host calls. Never blocks.
    public static bool IsLoggedIn(string configJson, Func<string, string?>? managedResolver = null) =>
        IsLoggedIn(configJson, DateTimeOffset.UtcNow, managedResolver, _AskCliAsync);

    // Test seam: the same decision against an injected clock and an injected "ask the CLI".
    internal static bool IsLoggedIn(
        string configJson,
        DateTimeOffset now,
        Func<string, string?>? managedResolver,
        Func<string, string?, CancellationToken, Task<bool?>> ask)
    {
        var config = _ParseConfig(configJson);
        var entry = _Cache.GetOrAdd(config.ConfigDir ?? string.Empty, _ => new _Entry());
        var reading = entry.Reading;

        if (reading is not null && now - reading.AsOf <= MaxAge)
        {
            return reading.LoggedIn;
        }

        // Aged out or never taken: refresh behind the caller and answer with what is known. Before the CLI has
        // ever answered, guess "logged in" — locking the operator out of an account they are signed in to is the
        // worse error, and the refresh started above corrects a wrong guess within its timeout.
        _Start(configJson, now, managedResolver, ask);
        return reading?.LoggedIn ?? true;
    }

    // Refreshes without waiting. Test seam mirrors `ClaudeLoginStatus.Warm`.
    public static void Warm(string configJson, Func<string, string?>? managedResolver = null) =>
        _Start(configJson, DateTimeOffset.UtcNow, managedResolver, _AskCliAsync);

    private static void _Start(
        string configJson,
        DateTimeOffset now,
        Func<string, string?>? managedResolver,
        Func<string, string?, CancellationToken, Task<bool?>> ask) =>
        _ = Task.Run(() => RefreshAsync(configJson, now, managedResolver, ask, CancellationToken.None));

    // `now` stamps the reading, so a test on a fixed clock does not compare two different ones.
    internal static async Task RefreshAsync(
        string configJson,
        DateTimeOffset now,
        Func<string, string?>? managedResolver,
        Func<string, string?, CancellationToken, Task<bool?>> ask,
        CancellationToken cancellationToken)
    {
        var config = _ParseConfig(configJson);
        var entry = _Cache.GetOrAdd(config.ConfigDir ?? string.Empty, _ => new _Entry());

        if (now < entry.RetryNotBefore || Interlocked.CompareExchange(ref entry.Refreshing, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var executablePath = CliExecutableLocator.Resolve(config.Command, managedResolver);
            var answer = await ask(executablePath, config.ConfigDir, cancellationToken).ConfigureAwait(false);
            if (answer is { } loggedIn)
            {
                entry.Reading = new _Reading(loggedIn, now);
                entry.RetryNotBefore = default;
                return;
            }

            entry.RetryNotBefore = now + _RetryAfterFailure;
        }
        catch (Exception)
        {
            entry.RetryNotBefore = now + _RetryAfterFailure;
        }
        finally
        {
            Interlocked.Exchange(ref entry.Refreshing, 0);
        }
    }

    private static async Task<bool?> _AskCliAsync(string executablePath, string? configDirOverride, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("login");
        startInfo.ArgumentList.Add("status");

        if (!string.IsNullOrWhiteSpace(configDirOverride))
        {
            startInfo.Environment["CODEX_HOME"] = configDirOverride;
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }

        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var drain = Task.WhenAll(stdout, process.StandardError.ReadToEndAsync(cancellationToken));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_Timeout);

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

            return null;
        }

        // The exit code is the only structured signal this CLI offers (no `--json` on `login status`).
        return process.ExitCode == 0;
    }

    private static CliAgentConfig _ParseConfig(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return new CliAgentConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<CliAgentConfig>(configJson, CliAgentConfig.JsonOptions) ?? new CliAgentConfig();
        }
        catch (JsonException)
        {
            return new CliAgentConfig();
        }
    }

    // Tests share a static cache; each one starts from a known state.
    internal static void ResetForTests() => _Cache.Clear();
}
