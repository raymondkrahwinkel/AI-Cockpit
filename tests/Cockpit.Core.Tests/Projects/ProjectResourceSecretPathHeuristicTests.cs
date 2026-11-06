using Cockpit.Core.Projects;

namespace Cockpit.Core.Tests.Projects;

/// <summary>
/// AC-612: pins <see cref="ProjectResourceSecretPathHeuristic.IsLikelySecretPath"/> against every hostile-input
/// shape the ticket named — the same table this ticket's own throwaway harness measured before this file existed,
/// made permanent. Every case names only a path shape, never file content (Iron Law #8) — this heuristic never
/// reads a file at all, so there is no content for a test name to leak even by accident.
/// </summary>
public class ProjectResourceSecretPathHeuristicTests
{
    [Theory]
    // ~/.ssh — the whole directory, not just key-shaped filenames (the ticket lists "~/.ssh/" as its own bullet,
    // distinct from the single-file bullets below — an ordinary file living there, e.g. known_hosts, is caught too).
    [InlineData("~/.ssh/id_rsa", true)]
    [InlineData("~/.ssh/id_ed25519", true)]
    [InlineData("~/.ssh/known_hosts", true)]
    [InlineData("~/.ssh/", true)]
    [InlineData("~/.ssh", true)]
    // The one deliberate exception: a public key names nothing secret.
    [InlineData("~/.ssh/id_rsa.pub", false)]
    // A directory that merely starts with the same four letters is not ".ssh" — a prefix match would over-trigger.
    [InlineData("~/.sshfoo/x", false)]
    [InlineData("~/.ssh-notes/x", false)]
    // ~/.gnupg, ~/.aws, ~/.kube — whole directories, same reasoning as ~/.ssh.
    [InlineData("~/.gnupg/private-keys-v1.d/foo", true)]
    [InlineData("~/.aws", true)]
    [InlineData("~/.aws/", true)]
    [InlineData("~/.aws/credentials", true)]
    [InlineData("~/.kube/config", true)]
    // ~/.config/gh — one directory under the shared ~/.config tree; the rest of ~/.config is not touched.
    [InlineData("~/.config/gh/hosts.yml", true)]
    [InlineData("~/.config/nvim/init.lua", false)]
    // Single named files, not whole directories.
    [InlineData("~/.docker/config.json", true)]
    [InlineData("~/.docker/daemon.json", false)]
    [InlineData("~/.netrc", true)]
    [InlineData("~/.npmrc", true)]
    [InlineData("~/.pypirc", true)]
    // Key-shaped filenames, wherever they sit under home — not confined to the directories above.
    [InlineData("~/Downloads/id_ed25519", true)]
    [InlineData("~/Downloads/id_ed25519.pub", false)]
    [InlineData("~/Downloads/server.pem", true)]
    [InlineData("~/Downloads/server.key", true)]
    [InlineData("~/Downloads/cert.p12", true)]
    [InlineData("~/Downloads/cert.pfx", true)]
    [InlineData("~/Downloads/identity.txt", false)]
    // Directory traversal must resolve before judging — "looks like it leaves .ssh" must not read as an escape.
    [InlineData("~/Projects/../.ssh/id_rsa", true)]
    [InlineData("~/.ssh/../Projects/notes.md", false)]
    // Out of scope entirely: repo-relative, a bare filename, a plugin-scheme reference, blank.
    [InlineData("docs/.ssh-notes.md", false)]
    [InlineData("id_rsa", false)]
    [InlineData("depot:cockpit", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    // "~" alone names the home folder itself, not any file under it.
    [InlineData("~", false)]
    public void IsLikelySecretPath_MatchesTheMeasuredTable(string? reference, bool expected) =>
        Assert.Equal(expected, ProjectResourceSecretPathHeuristic.IsLikelySecretPath(reference));
}
