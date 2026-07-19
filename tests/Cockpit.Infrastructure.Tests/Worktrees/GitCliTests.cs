using Cockpit.Infrastructure.Worktrees;
using FluentAssertions;

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

        cleaned.Should().Contain("Preparing worktree");
        cleaned.Should().Contain("Filename too long");
        cleaned.Should().Contain("fatal: could not checkout worktree");
        cleaned.Should().NotContain("Updating files:");
    }

    [Fact]
    public void StripProgress_HandlesCarriageReturnOverwrittenProgress()
    {
        // git overwrites the progress line in place with a bare carriage return, so the whole run arrives as one
        // \r-separated blob — the split has to treat it the same as newlines.
        var stderr = "Updating files:  50%\rUpdating files:  99%\rUpdating files: 100%\rerror: boom";

        GitCli.StripProgress(stderr).Should().Be("error: boom");
    }

    [Fact]
    public void StripProgress_WhenOnlyProgress_FallsBackToTheRawText()
    {
        // A git that reported nothing but progress must not be reduced to an empty message.
        var stderr = "Updating files: 100% (11974/11974)";

        GitCli.StripProgress(stderr).Should().Be(stderr.Trim());
    }
}
