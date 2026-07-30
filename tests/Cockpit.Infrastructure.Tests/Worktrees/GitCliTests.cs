using Cockpit.Infrastructure.Worktrees;

namespace Cockpit.Infrastructure.Tests.Worktrees;

/// <summary>
/// <see cref="GitCli.StripProgress"/> keeps a failed git command's error readable: a worktree add that fails
/// part-way writes a hundred "Updating files: NN%" progress lines to stderr before the actual error, and all of it
/// used to land verbatim in the "could not isolate this session" dialog (AC-85). These pin that the progress is
/// dropped and the diagnosis kept — without ever reducing a genuinely progress-only message to nothing.
/// </summary>
public class GitCliTests
{
    [Fact]
    public void StripProgress_DropsCheckoutProgress_KeepsTheError()
    {
        var stderr =
            "Preparing worktree (new branch 'cockpit/default-e54986c8')\n" +
            "Updating files:  18% (2242/11974)\n" +
            "Updating files:  19% (2276/11974)\n" +
            "error: unable to create file some/very/long/path.component.ts: Filename too long\n" +
            "Updating files:  20% (2395/11974)\n" +
            "fatal: could not checkout worktree";

        var cleaned = GitCli.StripProgress(stderr);

        Assert.Contains("Preparing worktree", cleaned);
        Assert.Contains("Filename too long", cleaned);
        Assert.Contains("fatal: could not checkout worktree", cleaned);
        Assert.DoesNotContain("Updating files:", cleaned);
    }

    // git echoes the remote URL in its own failures ("fatal: unable to access 'https://token@host/…'"); a clone
    // error must not carry a pasted credential into the operator's dialog or a log (AC-90 binding rule).
    [Fact]
    public void RedactUrlCredentials_BlanksUserInfoInGitsOwnErrorText()
    {
        const string stderr =
            "fatal: unable to access 'https://x-access-token:ghp_secretsecret@github.com/org/repo.git/': error 403";

        var redacted = GitCli.RedactUrlCredentials(stderr);

        Assert.DoesNotContain("ghp_secretsecret", redacted);
        Assert.Contains("https://***@github.com/org/repo.git", redacted);
    }

    [Fact]
    public void RedactUrlCredentials_LeavesTextWithoutCredentialsUntouched()
    {
        const string stderr = "fatal: repository 'https://github.com/org/repo.git/' not found";

        Assert.Equal(stderr, GitCli.RedactUrlCredentials(stderr));
    }

    [Fact]
    public void StripProgress_HandlesCarriageReturnOverwrittenProgress()
    {
        // git overwrites the progress line in place with a bare carriage return, so the whole run arrives as one
        // \r-separated blob — the split has to treat it the same as newlines.
        var stderr = "Updating files:  50%\rUpdating files:  99%\rUpdating files: 100%\rerror: boom";

        Assert.Equal("error: boom", GitCli.StripProgress(stderr));
    }

    [Fact]
    public void StripProgress_WhenOnlyProgress_FallsBackToTheRawText()
    {
        // A git that reported nothing but progress must not be reduced to an empty message.
        var stderr = "Updating files: 100% (11974/11974)";

        Assert.Equal(stderr.Trim(), GitCli.StripProgress(stderr));
    }

    [Fact]
    public async Task RunAsync_WorkingDirectoryDoesNotExist_NamesThatCauseRatherThanBlamingPath()
    {
        // AC-507 defect 1: a repository folder that moved away used to land in the same catch as a genuinely missing
        // git binary, so the operator was told to check their git install for a problem that was never there. The two
        // causes are told apart before git is even asked to start.
        var gone = Path.Combine(Path.GetTempPath(), $"cockpit-gone-{Guid.NewGuid():n}");

        var run = async () => await GitCli.RunAsync(gone, ["status"], CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(run);
        Assert.Contains(gone, ex.Message);
        Assert.DoesNotContain("is it installed and on PATH", ex.Message);
    }
}
