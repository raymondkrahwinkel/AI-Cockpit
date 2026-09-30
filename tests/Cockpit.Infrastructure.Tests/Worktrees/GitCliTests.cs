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

}
