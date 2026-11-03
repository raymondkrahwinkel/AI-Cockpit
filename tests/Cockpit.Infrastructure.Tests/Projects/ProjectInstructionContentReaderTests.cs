using Cockpit.Core.Projects;

namespace Cockpit.Infrastructure.Tests.Projects;

/// <summary>
/// The I/O <see cref="Cockpit.Core.Sessions.SessionStartDefaults.Resolve"/> deliberately never does itself
/// (AC-486): reading an Instructions row's file content for a row that ticked
/// <see cref="ProjectResource.SendsContent"/>. Mirrors <see cref="ProjectResourceProbeTests"/>'s own shape — most of
/// these tests are about this reader never blocking a session from starting, whatever the file on disk turns out to
/// look like.
/// <para>
/// Plain xUnit assertions rather than FluentAssertions (Raymond, 2026-07-29): the repo's FluentAssertions version is
/// under the commercial Xceed licence, and this is a new file, so it never adopts the dependency in the first place.
/// </para>
/// </summary>
public class ProjectInstructionContentReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cockpit-instruction-content-reader-tests", Guid.NewGuid().ToString("n"));

    public ProjectInstructionContentReaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// This reader adds nothing of its own for AC-612 — it reads <see cref="ProjectResource.SendsContent"/>, and
    /// that getter is where the secret-path gate actually lives (the same "one place, not every reader" pattern
    /// the Role invariant already uses). Proven here anyway, at this reader's own boundary: a row constructed with
    /// <c>SendsContent = true</c> bypasses the editor entirely (a hand-edited <c>cockpit.json</c>, or a row saved
    /// before this ticket existed), so this is the one place that proves the domain model — not just the
    /// ViewModel's own live enforcement — is what actually stops the content reaching a session.
    /// <para>
    /// Hooks that succeed rather than throw (the same trap <see cref="AFileLargerThanTheReadLimit_IsNeverOpenedEvenWhenTheReadWouldHaveSucceeded"/>'s
    /// own remarks describe): a throwing hook would be swallowed by this reader's own unreadable-file <c>catch</c>
    /// exactly the way a genuinely missing file is, so the result would read empty whether or not the secret gate
    /// did anything at all — proving nothing. A hook that hands back real content is the only shape that actually
    /// fails if <see cref="ProjectResource.SendsContent"/> stops gating.
    /// </para>
    /// </summary>
    [Fact]
    public void ARowConstructedTickedForASecretPath_IsNeverOpenedEvenThoughTheConstructorAcceptedTheTick()
    {
        var resources = new[] { new ProjectResource("~/.ssh/id_rsa", ProjectResourceRole.Instructions) { SendsContent = true } };

        var result = ProjectInstructionContentReader.Read(
            resources,
            fileLength: _ => 10,
            readAllText: _ => "this marker string must never reach the result");

        Assert.Empty(result);
    }
}
