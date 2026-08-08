using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Cockpit.Plugin.ClaudeProvider;

// Whether a Claude profile is logged in, per `claude auth status --json` (AC-629). Replaces
// `File.Exists(".credentials.json")`, which was wrong both ways: absent on a logged-in macOS (Keychain), present
// next to an expired token. Only `loggedIn` is read, never a credential's contents (Iron Law #8).
//
// ⚠️ Cached because `IProfileLoginChecker.IsLoggedIn` is synchronous on the UI thread, once per profile — and the
// CLI costs ~575ms warm, 9.3s cold. The gate answers from here; the subprocess refreshes behind it.
internal static class ClaudeLoginStatus
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan _Timeout = TimeSpan.FromSeconds(30);

    // A CLI too old for `auth status` never starts answering; without this every gate call re-spawns it.
    private static readonly TimeSpan _RetryAfterFailure = TimeSpan.FromMinutes(5);

    // Keyed by config directory — that is what decides whose login the CLI reports.
    private static readonly ConcurrentDictionary<string, _Entry> _Cache = new(StringComparer.OrdinalIgnoreCase);

    // One reference rather than a separate bool and DateTimeOffset: a 16-byte struct is not written atomically,
    // and the refresh writes while the gate reads.
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

        // When the next attempt may run after one that could not answer.
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
        var config = ClaudeProviderConfig.Parse(configJson);
        var entry = _Cache.GetOrAdd(_KeyFor(config), _ => new _Entry());
        var reading = entry.Reading;

        if (reading is not null && now - reading.AsOf <= MaxAge)
        {
            return reading.LoggedIn;
        }

        // Aged out or never taken: refresh behind the caller and answer with what is known.
        _Start(configJson, now, managedResolver, ask);
        return reading?.LoggedIn ?? _ColdAnswer(config);
    }

    // Before the CLI has ever answered. `.credentials.json` is a reliable negative everywhere except macOS,
    // where the Keychain holds the credentials and the file never exists — so there, guess "logged in": locking
    // the operator out of an account they are signed in to is the worse error. The Manage-profiles list binds
    // this once and never re-reads, so a wrong guess stays visible until the dialog reopens.
    private static bool _ColdAnswer(ClaudeProviderConfig config)
    {
        if (OperatingSystem.IsMacOS())
        {
            return true;
        }

        var stateDirectory = ClaudeConfigPaths.ResolveStateDirectory(
            config.ConfigDir,
            Environment.GetEnvironmentVariable(ClaudeConfigPaths.EnvironmentVariable),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        // Existence only — never the contents (Iron Law #8).
        return File.Exists(Path.Combine(stateDirectory, ".credentials.json"));
    }

    // Task.Run, not a bare `_ =`: an async method runs synchronously up to its first real await, and `Resolve`
    // (PATH probe) plus `Process.Start` sit before it — on the UI thread, the freeze this cache exists to avoid.
    private static void _Start(
        string configJson,
        DateTimeOffset now,
        Func<string, string?>? managedResolver,
        Func<string, string?, CancellationToken, Task<bool?>> ask) =>
        _ = Task.Run(() => RefreshAsync(configJson, now, managedResolver, ask, CancellationToken.None));

    // Refreshes without waiting — called at plugin start per detected profile. One CLI per profile at a time.
    public static void Warm(string configJson, Func<string, string?>? managedResolver = null) =>
        _Start(configJson, DateTimeOffset.UtcNow, managedResolver, _AskCliAsync);

    // `now` stamps the reading, so a test on a fixed clock does not compare two different ones.
    internal static async Task RefreshAsync(
        string configJson,
        DateTimeOffset now,
        Func<string, string?>? managedResolver,
        Func<string, string?, CancellationToken, Task<bool?>> ask,
        CancellationToken cancellationToken)
    {
        var config = ClaudeProviderConfig.Parse(configJson);
        var entry = _Cache.GetOrAdd(_KeyFor(config), _ => new _Entry());

        // Backing off after a failure, or one already in flight — either way, not a second subprocess.
        if (now < entry.RetryNotBefore || Interlocked.CompareExchange(ref entry.Refreshing, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var executablePath = ClaudeExecutableLocator.Resolve(
                string.IsNullOrWhiteSpace(config.ExecutablePath) ? "claude" : config.ExecutablePath,
                managedResolver);

            var spawnOverride = ClaudeConfigPaths.ResolveSpawnOverride(
                config.ConfigDir,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

            var answer = await ask(executablePath, spawnOverride, cancellationToken).ConfigureAwait(false);
            if (answer is { } loggedIn)
            {
                entry.Reading = new _Reading(loggedIn, now);
                entry.RetryNotBefore = default;
                return;
            }

            // A failure is not a login status: leave what was known, and back off.
            entry.RetryNotBefore = now + _RetryAfterFailure;
        }
        catch (Exception)
        {
            // A missing `claude` throws out of Process.Start rather than answering null — same backoff.
            entry.RetryNotBefore = now + _RetryAfterFailure;
        }
        finally
        {
            Interlocked.Exchange(ref entry.Refreshing, 0);
        }
    }

    // Null when the CLI could not be asked, or said something this build does not understand — never a guess.
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

        startInfo.ArgumentList.Add("auth");
        startInfo.ArgumentList.Add("status");
        startInfo.ArgumentList.Add("--json");

        if (!string.IsNullOrWhiteSpace(configDirOverride))
        {
            startInfo.Environment[ClaudeConfigPaths.EnvironmentVariable] = configDirOverride;
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }

        // stdin closed so the CLI never waits on input; both pipes drained or a full one deadlocks the child.
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

        return ReadLoggedIn(await stdout.ConfigureAwait(false));
    }

    // The exit code is deliberately unused: 1 means logged out *and* means the CLI never ran. Only the payload
    // tells those apart.
    internal static bool? ReadLoggedIn(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("loggedIn", out var loggedIn)
                && loggedIn.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? loggedIn.GetBoolean()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string _KeyFor(ClaudeProviderConfig config) => config.ConfigDir ?? string.Empty;

    // Tests share a static cache; each one starts from a known state.
    internal static void ResetForTests() => _Cache.Clear();
}
