using System.Text.Json;

namespace Cockpit.Plugin.CliAgentProvider.Tests;

// AC-713: the login gate — `codex login status` has no `--json`, only its exit code is structured (empirically verified).
[Collection(nameof(CodexLoginStatusTests))]
[CollectionDefinition(nameof(CodexLoginStatusTests), DisableParallelization = true)]
public class CodexLoginStatusTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-11T13:00:00Z");

    // A distinct config dir per test, since the cache is static and keyed on it.
    private static string ConfigFor(string name) =>
        JsonSerializer.Serialize(new CliAgentConfig(ConfigDir: $"/tmp/{name}-{Guid.NewGuid():N}"), CliAgentConfig.JsonOptions);

    private static Func<string, string?, CancellationToken, Task<bool?>> Answers(bool? loggedIn, TaskCompletionSource? gate = null) =>
        async (_, _, _) =>
        {
            if (gate is not null)
            {
                await gate.Task;
            }

            return loggedIn;
        };

    // Before the CLI has ever answered, the gate guesses "logged in" rather than lock the operator out —
    // Codex had no gate at all before this, so a wrong guess is still strictly better than the old "always ready".
    [Fact]
    public void AColdGate_GuessesLoggedIn()
    {
        CodexLoginStatus.ResetForTests();
        var config = ConfigFor("cold");

        Assert.True(CodexLoginStatus.IsLoggedIn(config, Now, null, Answers(null)));
    }

    [Fact]
    public async Task TheCliesAnswerReplacesTheColdGuess()
    {
        CodexLoginStatus.ResetForTests();
        var config = ConfigFor("replace");

        Assert.True(CodexLoginStatus.IsLoggedIn(config, Now, null, Answers(false)));
        await _UntilAsync(() => !CodexLoginStatus.IsLoggedIn(config, Now, null, Answers(false)));
    }

    [Fact]
    public void TheGateNeverWaitsForTheCli()
    {
        CodexLoginStatus.ResetForTests();
        var config = ConfigFor("slow");
        var stuck = new TaskCompletionSource();

        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            CodexLoginStatus.IsLoggedIn(config, Now, null, Answers(true, stuck));
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
        CodexLoginStatus.ResetForTests();
        var config = ConfigFor("age");
        var asked = 0;
        Func<string, string?, CancellationToken, Task<bool?>> counting = (_, _, _) =>
        {
            Interlocked.Increment(ref asked);
            return Task.FromResult<bool?>(false);
        };

        await CodexLoginStatus.RefreshAsync(config, Now, null, counting, CancellationToken.None);
        Assert.Equal(1, asked);

        Assert.False(CodexLoginStatus.IsLoggedIn(config, Now, null, counting));
        Assert.False(CodexLoginStatus.IsLoggedIn(config, Now, null, counting));
        Assert.Equal(1, asked);

        Assert.False(CodexLoginStatus.IsLoggedIn(config, Now.Add(CodexLoginStatus.MaxAge).AddSeconds(1), null, counting));
        await _UntilAsync(() => Volatile.Read(ref asked) == 2);
    }

    [Fact]
    public async Task AFailedAttempt_BacksOffInsteadOfSpawningPerCall()
    {
        CodexLoginStatus.ResetForTests();
        var config = ConfigFor("backoff");
        var asked = 0;
        Func<string, string?, CancellationToken, Task<bool?>> failing = (_, _, _) =>
        {
            Interlocked.Increment(ref asked);
            return Task.FromResult<bool?>(null);
        };

        await CodexLoginStatus.RefreshAsync(config, Now, null, failing, CancellationToken.None);
        Assert.Equal(1, asked);

        await CodexLoginStatus.RefreshAsync(config, Now.AddSeconds(1), null, failing, CancellationToken.None);
        Assert.Equal(1, Volatile.Read(ref asked));

        await CodexLoginStatus.RefreshAsync(config, Now.AddMinutes(10), null, failing, CancellationToken.None);
        Assert.Equal(2, Volatile.Read(ref asked));
    }

    [Fact]
    public async Task TwoProfilesDoNotShareAnAnswer()
    {
        CodexLoginStatus.ResetForTests();
        var work = ConfigFor("work");
        var personal = ConfigFor("personal");

        await CodexLoginStatus.RefreshAsync(work, Now, null, Answers(false), CancellationToken.None);
        await CodexLoginStatus.RefreshAsync(personal, Now, null, Answers(true), CancellationToken.None);

        Assert.False(CodexLoginStatus.IsLoggedIn(work, Now, null, Answers(false)));
        Assert.True(CodexLoginStatus.IsLoggedIn(personal, Now, null, Answers(true)));
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
