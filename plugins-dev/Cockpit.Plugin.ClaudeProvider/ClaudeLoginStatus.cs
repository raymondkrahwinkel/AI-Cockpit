using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Cockpit.Plugin.ClaudeProvider;

// Whether a Claude profile is logged in, asked of the CLI itself (AC-629).
//
// The gate used to be `File.Exists(".credentials.json")`, which is wrong in both directions: on macOS the
// credentials live in the Keychain and that file never appears (an ingelogd profile reads as logged out), and a
// token that has expired or been revoked leaves the file exactly where it was (a logged-out profile reads as
// logged in, and the session then dies with an unexplained error). Since CLI 2.x there is an answer that is
// neither guess:
//
//     $ claude auth status --json          (--json is already the default)
//     {"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","subscriptionType":"max"}
//     {"loggedIn":false,"authMethod":"none","apiProvider":"firstParty"}
//
// Only `loggedIn` is read. No credential value is ever read or logged (Iron Law #8).
//
// ⚠️ The reason this is a cache and not just a call: `IProfileLoginChecker.IsLoggedIn` is synchronous and runs on
// the UI thread — once per profile while the Manage-profiles list is built, and again in a property setter when
// the New-session dialog changes profile. `claude auth status` measured at ~575ms warm and 9.3s cold (it is a
// 287MB native binary that has to come off disk). Spawning it there would freeze the dialog for as long as the
// operator has profiles. So the gate answers from this cache and the subprocess refreshes it behind.
//
// What a cold cache answers is a deliberate choice: `true`. The two ways to be wrong are not equal — a false
// "logged out" blocks the operator from starting a session at all and tells them to log in when they already are,
// while a false "logged in" costs a session that starts and fails with an auth error. `Warm` is called at plugin
// start for every detected profile so the first dialog rarely sees a cold entry at all.
internal static class ClaudeLoginStatus
{
    // How long an answer stands before the next ask refreshes it behind the caller's back. A login does not change
    // on its own; this is short enough that logging in elsewhere shows up within a minute.
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(1);

    // How long the subprocess gets. Generous against the 9.3s cold start measured on this machine — it runs on a
    // background task, so the only cost of waiting is a cache entry that stays stale a little longer.
    private static readonly TimeSpan _Timeout = TimeSpan.FromSeconds(30);

    // Keyed by the config directory, which is what decides whose login the CLI reports. Empty string is the
    // machine's own default profile.
    private static readonly ConcurrentDictionary<string, _Entry> _Cache = new(StringComparer.OrdinalIgnoreCase);

    // One immutable reading behind one volatile reference, rather than a bool and a DateTimeOffset written
    // separately: the refresh runs on a background task while the gate reads on the UI thread, and a 16-byte
    // DateTimeOffset is not written atomically — a torn pair would date an answer to a moment that never was.
    private sealed record _Reading(bool LoggedIn, DateTimeOffset AsOf);

    private sealed class _Entry
    {
        private volatile _Reading? _reading;
        public int Refreshing;

        public _Reading? Reading
        {
            get => _reading;
            set => _reading = value;
        }
    }

    // The gate the host calls. Never blocks: a fresh answer comes straight back, anything else starts a refresh
    // and answers with the last known value — or `true` when there is none yet (see the class remarks).
    public static bool IsLoggedIn(string configJson, Func<string, string?>? managedResolver = null) =>
        IsLoggedIn(configJson, DateTimeOffset.UtcNow, managedResolver, _AskCliAsync);

    // Test seam: the same decision against an injected clock and an injected "ask the CLI".
    internal static bool IsLoggedIn(
        string configJson,
        DateTimeOffset now,
        Func<string, string?>? managedResolver,
        Func<string, string?, CancellationToken, Task<bool?>> ask)
    {
        var entry = _Cache.GetOrAdd(_KeyFor(ClaudeProviderConfig.Parse(configJson)), _ => new _Entry());
        var reading = entry.Reading;

        if (reading is not null && now - reading.AsOf <= MaxAge)
        {
            return reading.LoggedIn;
        }

        // Aged out or never taken: refresh behind the caller and answer with what is known. Nothing known at all
        // answers "logged in" — see the class remarks on why the two ways to be wrong are not equal.
        _ = RefreshAsync(configJson, now, managedResolver, ask, CancellationToken.None);
        return reading?.LoggedIn ?? true;
    }

    // Starts a refresh for this profile without waiting for it — called at plugin start for every detected
    // profile, and by the gate whenever its answer has aged out. Self-throttling per profile: a second call while
    // one is in flight does nothing rather than spawning a second CLI.
    public static void Warm(string configJson, Func<string, string?>? managedResolver = null) =>
        _ = RefreshAsync(configJson, DateTimeOffset.UtcNow, managedResolver, _AskCliAsync, CancellationToken.None);

    // `now` stamps the reading rather than the wall clock, so a test driving the gate at a fixed time gets an
    // entry dated on that same clock — otherwise "has this aged out" compares two different clocks.
    internal static async Task RefreshAsync(
        string configJson,
        DateTimeOffset now,
        Func<string, string?>? managedResolver,
        Func<string, string?, CancellationToken, Task<bool?>> ask,
        CancellationToken cancellationToken)
    {
        var config = ClaudeProviderConfig.Parse(configJson);
        var entry = _Cache.GetOrAdd(_KeyFor(config), _ => new _Entry());
        if (Interlocked.CompareExchange(ref entry.Refreshing, 1, 0) != 0)
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

            if (await ask(executablePath, spawnOverride, cancellationToken).ConfigureAwait(false) is { } loggedIn)
            {
                entry.Reading = new _Reading(loggedIn, now);
            }

            // A null answer (CLI missing, timed out, output this build cannot read) leaves the previous entry —
            // and a cold one cold, so the next ask tries again instead of standing on a failure.
        }
        catch (Exception)
        {
            // A login status is not worth failing a dialog over.
        }
        finally
        {
            Interlocked.Exchange(ref entry.Refreshing, 0);
        }
    }

    // Runs `claude auth status --json` and reads `loggedIn` out of it. Null when the CLI could not be asked or
    // said something this build does not understand — never a guess.
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

        // stdin closed at once so the CLI never waits on input, and both pipes drained while waiting — a child
        // that fills one and blocks is the failure mode that has turned tests flaky in this repo before.
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

    // Reads `loggedIn` out of an `auth status --json` payload. The exit code is deliberately not used: it is 1
    // when logged out, which is indistinguishable from the CLI failing to run at all — and those two must not
    // produce the same answer.
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
