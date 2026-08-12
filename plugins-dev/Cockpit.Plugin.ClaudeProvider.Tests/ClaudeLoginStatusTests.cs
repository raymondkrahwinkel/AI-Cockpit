using System.Text.Json;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

// The login gate (AC-629). Payloads verbatim from CLI 2.1.226. What most of these pin down is *not blocking*:
// the host calls this synchronously on the UI thread and the CLI costs ~575ms warm, 9.3s cold.
[Collection(nameof(ClaudeLoginStatusTests))]
[CollectionDefinition(nameof(ClaudeLoginStatusTests), DisableParallelization = true)]
public class ClaudeLoginStatusTests
{
    private const string LoggedInPayload =
        """{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","email":"a@b.c","subscriptionType":"max"}""";

    private const string LoggedOutPayload =
        """{"loggedIn":false,"authMethod":"none","apiProvider":"firstParty"}""";

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-08T13:00:00Z");

    // A distinct config dir per test, since the cache is static and keyed on it.
    private static string ConfigFor(string name) =>
        JsonSerializer.Serialize(new ClaudeProviderConfig(ConfigDir: $"/tmp/{name}-{Guid.NewGuid():N}"), ClaudeProviderConfig.JsonOptions);

    private static Func<string, string?, CancellationToken, Task<bool?>> Answers(bool? loggedIn, TaskCompletionSource? gate = null) =>
        async (_, _, _) =>
        {
            if (gate is not null)
            {
                await gate.Task;
            }

            return loggedIn;
        };

    [Fact]
    public void ReadLoggedIn_ReadsTheCliesOwnPayloads()
    {
        Assert.True(ClaudeLoginStatus.ReadLoggedIn(LoggedInPayload));
        Assert.False(ClaudeLoginStatus.ReadLoggedIn(LoggedOutPayload));
    }

    [Theory]
    // Not a logged-out account. The exit code cannot tell these apart either: 1 means logged out and also
    // means the binary was missing.
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"loggedIn":"yes"}""")]
    public void ReadLoggedIn_RubbishIsUnknown_NotLoggedOut(string json) =>
        Assert.Null(ClaudeLoginStatus.ReadLoggedIn(json));

    // A real directory, so the cold answer's `.credentials.json` probe has something to look at.
    private static string ConfigInDirectory(string dir) =>
        JsonSerializer.Serialize(new ClaudeProviderConfig(ConfigDir: dir), ClaudeProviderConfig.JsonOptions);

    private static string NewDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "claude-login-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task TheCliesAnswerReplacesTheColdOne()
    {
        ClaudeLoginStatus.ResetForTests();
        var dir = NewDirectory();
        try
        {
            var config = ConfigInDirectory(dir);
            File.WriteAllText(Path.Combine(dir, ".credentials.json"), "{}");

            // Cold, with credentials present: ready. That same call starts the refresh behind it.
            Assert.True(ClaudeLoginStatus.IsLoggedIn(config, Now, null, Answers(false)));

            // And the CLI overrules it — the expired-token case the file check could never see. Waited for, not
            // forced: a second RefreshAsync would collide with the in-flight one and quietly do nothing.
            await _UntilAsync(() => !ClaudeLoginStatus.IsLoggedIn(config, Now, null, Answers(false)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // The Manage-profiles list binds this on first paint and never re-reads, so a wrong guess stays visible.
    [Fact]
    public void AColdGateWithNoCredentialsFile_IsLoggedOutExceptOnMacOs()
    {
        ClaudeLoginStatus.ResetForTests();
        var dir = NewDirectory();
        try
        {
            var answer = ClaudeLoginStatus.IsLoggedIn(ConfigInDirectory(dir), Now, null, Answers(null));

            if (OperatingSystem.IsMacOS())
            {
                Assert.True(answer, "the file says nothing on macOS, and locking the operator out is the worse error");
            }
            else
            {
                Assert.False(answer, "no credentials file and no CLI answer yet — the old check still stands in");
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // A CLI that cannot answer must not become a subprocess per dialog paint.
    [Fact]
    public async Task AFailedAttempt_BacksOffInsteadOfSpawningPerCall()
    {
        ClaudeLoginStatus.ResetForTests();
        var config = ConfigFor("backoff");
        var asked = 0;
        Func<string, string?, CancellationToken, Task<bool?>> failing = (_, _, _) =>
        {
            Interlocked.Increment(ref asked);
            return Task.FromResult<bool?>(null);
        };

        await ClaudeLoginStatus.RefreshAsync(config, Now, null, failing, CancellationToken.None);
        Assert.Equal(1, asked);

        await ClaudeLoginStatus.RefreshAsync(config, Now.AddSeconds(1), null, failing, CancellationToken.None);
        await ClaudeLoginStatus.RefreshAsync(config, Now.AddMinutes(1), null, failing, CancellationToken.None);
        Assert.Equal(1, Volatile.Read(ref asked));

        // Past the backoff it tries again — a CLI installed while the app is open still gets picked up.
        await ClaudeLoginStatus.RefreshAsync(config, Now.AddMinutes(10), null, failing, CancellationToken.None);
        Assert.Equal(2, Volatile.Read(ref asked));
    }

    [Fact]
    public void TheGateNeverWaitsForTheCli()
    {
        ClaudeLoginStatus.ResetForTests();
        var config = ConfigFor("slow");
        var stuck = new TaskCompletionSource();

        try
        {
            // The ask never completes; the gate still returns at once. On the UI thread that is a dialog that
            // opens versus one that freezes for 9 seconds.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            ClaudeLoginStatus.IsLoggedIn(config, Now, null, Answers(true, stuck));
            clock.Stop();

            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"the gate blocked for {clock.Elapsed}");
        }
        finally
        {
            stuck.SetResult();
        }
    }

    [Fact]
    public async Task AKnownAnswerIsReused_UntilItAgesOut()
    {
        ClaudeLoginStatus.ResetForTests();
        var config = ConfigFor("age");
        var asked = 0;
        Func<string, string?, CancellationToken, Task<bool?>> counting = (_, _, _) =>
        {
            Interlocked.Increment(ref asked);
            return Task.FromResult<bool?>(false);
        };

        await ClaudeLoginStatus.RefreshAsync(config, Now, null, counting, CancellationToken.None);
        Assert.Equal(1, asked);

        // Inside the window: from the cache, no second subprocess — what keeps ten profiles from spawning ten CLIs.
        Assert.False(ClaudeLoginStatus.IsLoggedIn(config, Now, null, counting));
        Assert.False(ClaudeLoginStatus.IsLoggedIn(config, Now, null, counting));
        Assert.Equal(1, asked);

        // Past it: still answers at once, with the last known value, and asks again behind the caller.
        Assert.False(ClaudeLoginStatus.IsLoggedIn(config, Now.Add(ClaudeLoginStatus.MaxAge).AddSeconds(1), null, counting));
        await _UntilAsync(() => Volatile.Read(ref asked) == 2);
    }

    // "Could not ask" is not an answer: a CLI that fails must never overwrite a reading it did give earlier.
    [Fact]
    public async Task AFailedAttempt_DoesNotOverwriteWhatTheCliAlreadySaid()
    {
        ClaudeLoginStatus.ResetForTests();
        var config = ConfigFor("unknown");

        await ClaudeLoginStatus.RefreshAsync(config, Now, null, Answers(false), CancellationToken.None);
        Assert.False(ClaudeLoginStatus.IsLoggedIn(config, Now, null, Answers(false)));

        await ClaudeLoginStatus.RefreshAsync(config, Now.Add(ClaudeLoginStatus.MaxAge).AddMinutes(1), null, Answers(null), CancellationToken.None);

        // Still logged out, on the CLI's own word — not flipped to the optimistic cold answer by a failure.
        Assert.False(ClaudeLoginStatus.IsLoggedIn(config, Now, null, Answers(null)));
    }

    // AC-732: after a successful in-app login, the gate must answer "logged in" right away — not on the poll
    // tick after next, once its own 1-minute-old "logged out" reading has aged out.
    [Fact]
    public async Task MarkLoggedIn_OverwritesAStalePreLoginReadingImmediately()
    {
        ClaudeLoginStatus.ResetForTests();
        var config = ConfigFor("post-login");

        await ClaudeLoginStatus.RefreshAsync(config, Now, null, Answers(false), CancellationToken.None);
        Assert.False(ClaudeLoginStatus.IsLoggedIn(config, Now, null, Answers(false)));

        ClaudeLoginStatus.MarkLoggedIn(config, Now);

        // Still well inside the old reading's MaxAge window — without the fix this reads the stale "false".
        Assert.True(ClaudeLoginStatus.IsLoggedIn(config, Now.AddSeconds(1), null, Answers(false)));
    }

    [Fact]
    public async Task TwoProfilesDoNotShareAnAnswer()
    {
        ClaudeLoginStatus.ResetForTests();
        var work = ConfigFor("work");
        var personal = ConfigFor("personal");

        await ClaudeLoginStatus.RefreshAsync(work, Now, null, Answers(false), CancellationToken.None);
        await ClaudeLoginStatus.RefreshAsync(personal, Now, null, Answers(true), CancellationToken.None);

        Assert.False(ClaudeLoginStatus.IsLoggedIn(work, Now, null, Answers(false)));
        Assert.True(ClaudeLoginStatus.IsLoggedIn(personal, Now, null, Answers(true)));
    }

    // A second refresh while one is in flight must not spawn a second CLI.
    [Fact]
    public async Task ARefreshAlreadyInFlight_IsNotStartedTwice()
    {
        ClaudeLoginStatus.ResetForTests();
        var config = ConfigFor("throttle");
        var gate = new TaskCompletionSource();
        var asked = 0;
        Func<string, string?, CancellationToken, Task<bool?>> counting = async (_, _, _) =>
        {
            Interlocked.Increment(ref asked);
            await gate.Task;
            return true;
        };

        var first = ClaudeLoginStatus.RefreshAsync(config, Now, null, counting, CancellationToken.None);
        await _UntilAsync(() => Volatile.Read(ref asked) == 1);

        await ClaudeLoginStatus.RefreshAsync(config, Now, null, counting, CancellationToken.None);
        Assert.Equal(1, Volatile.Read(ref asked));

        gate.SetResult();
        await first;
    }

    private static async Task _UntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The background refresh never got there.");
    }
}
