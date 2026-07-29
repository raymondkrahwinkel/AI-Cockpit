using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Projects;

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

    private string _File(string name) => Path.Combine(_root, name);

    [Fact]
    public void ARowTickedForContent_IsReadAndKeyedByItsReference()
    {
        var file = _File("house-rules.md");
        File.WriteAllText(file, "Always write tests first.");
        var resources = new[] { new ProjectResource(file, ProjectResourceRole.Instructions) { SendsContent = true } };

        var result = ProjectInstructionContentReader.Read(resources);

        Assert.True(result.ContainsKey(file));
        Assert.Equal("Always write tests first.", result[file]);
    }

    [Fact]
    public void ARowThatDidNotTickSendsContent_IsNeverRead()
    {
        var file = _File("house-rules.md");
        File.WriteAllText(file, "Always write tests first.");
        var resources = new[] { new ProjectResource(file, ProjectResourceRole.Instructions) };

        Assert.Empty(ProjectInstructionContentReader.Read(resources));
    }

    [Fact]
    public void ARowOfADifferentRole_IsNeverReadEvenIfTicked()
    {
        var file = _File("notes");
        File.WriteAllText(file, "some content");
        var resources = new[] { new ProjectResource(file, ProjectResourceRole.Memory) { SendsContent = true } };

        Assert.Empty(ProjectInstructionContentReader.Read(resources));
    }

    [Fact]
    public void ARowThatDoesNotReachSessions_IsNeverReadEvenIfTicked()
    {
        var file = _File("house-rules.md");
        File.WriteAllText(file, "content");
        var resources = new[]
        {
            new ProjectResource(file, ProjectResourceRole.Instructions) { SendsContent = true, ReachesSessions = false },
        };

        Assert.Empty(ProjectInstructionContentReader.Read(resources));
    }

    [Fact]
    public void AMissingFile_IsLeftOutRatherThanThrowing()
    {
        var missing = _File("does-not-exist.md");
        var resources = new[] { new ProjectResource(missing, ProjectResourceRole.Instructions) { SendsContent = true } };

        var result = ProjectInstructionContentReader.Read(resources);

        Assert.Empty(result);
    }

    /// <summary>
    /// AC-486's read-limit rule: a file whose size alone already exceeds what the shared prompt ceiling could ever
    /// hold must never be opened at all — proven with a hook that would throw if the read were ever attempted.
    /// </summary>
    [Fact]
    public void AFileLargerThanTheReadLimit_IsNeverOpened()
    {
        var file = _File("huge.md");
        var resources = new[] { new ProjectResource(file, ProjectResourceRole.Instructions) { SendsContent = true } };

        var result = ProjectInstructionContentReader.Read(
            resources,
            fileLength: _ => 32 * 1024 + 1,
            readAllText: _ => throw new InvalidOperationException("must never be called for a file over the read limit"));

        Assert.Empty(result);
    }

    /// <summary>
    /// The same rule, proven a second way that a broken size check cannot silently pass: <c>readAllText</c> here
    /// returns normally rather than throwing, so a size check that failed to skip this file would let its content
    /// straight into the result — the earlier throwing hook alone could not catch that, because this reader's own
    /// unreadable-file handling swallows any exception the same way, masking a broken size check behind the
    /// unrelated "never blocks a session" guarantee.
    /// </summary>
    [Fact]
    public void AFileLargerThanTheReadLimit_IsNeverOpenedEvenWhenTheReadWouldHaveSucceeded()
    {
        var file = _File("huge-but-readable.md");
        var resources = new[] { new ProjectResource(file, ProjectResourceRole.Instructions) { SendsContent = true } };

        var result = ProjectInstructionContentReader.Read(
            resources,
            fileLength: _ => 32 * 1024 + 1,
            readAllText: _ => "this must never appear in the result");

        Assert.Empty(result);
    }

    [Fact]
    public void AFileExactlyAtTheReadLimit_IsStillRead()
    {
        var file = _File("boundary.md");
        var resources = new[] { new ProjectResource(file, ProjectResourceRole.Instructions) { SendsContent = true } };

        var result = ProjectInstructionContentReader.Read(
            resources,
            fileLength: _ => 32 * 1024,
            readAllText: _ => "content at exactly the boundary");

        Assert.True(result.ContainsKey(file));
        Assert.Equal("content at exactly the boundary", result[file]);
    }

    [Fact]
    public void AnEmptyFile_IsReadAsEmptyContent()
    {
        var file = _File("empty.md");
        File.WriteAllText(file, string.Empty);
        var resources = new[] { new ProjectResource(file, ProjectResourceRole.Instructions) { SendsContent = true } };

        var result = ProjectInstructionContentReader.Read(resources);

        Assert.True(result.ContainsKey(file));
        Assert.Equal(string.Empty, result[file]);
    }

    [Fact]
    public void AWhitespaceOnlyFile_IsReadFaithfully()
    {
        var file = _File("whitespace.md");
        File.WriteAllText(file, "   \n\t  \n");
        var resources = new[] { new ProjectResource(file, ProjectResourceRole.Instructions) { SendsContent = true } };

        var result = ProjectInstructionContentReader.Read(resources);

        Assert.True(result.ContainsKey(file));
        Assert.Equal("   \n\t  \n", result[file]);
    }

    [Fact]
    public void MultibyteUnicodeContent_IsReadCorrectly()
    {
        var file = _File("unicode.md");
        var content = "Lees dit zorgvuldig: 🚀 中文说明 café naïve — schema volgen.";
        File.WriteAllText(file, content);
        var resources = new[] { new ProjectResource(file, ProjectResourceRole.Instructions) { SendsContent = true } };

        var result = ProjectInstructionContentReader.Read(resources);

        Assert.True(result.ContainsKey(file));
        Assert.Equal(content, result[file]);
    }

    /// <summary>
    /// A race between this reader's own size check and the actual read — the file is removed in between — must
    /// never surface as an exception. Simulated rather than genuinely racing a background deletion, which would
    /// make this test flaky by nature.
    /// </summary>
    [Fact]
    public void AFileRemovedBetweenTheSizeCheckAndTheRead_IsLeftOutRatherThanThrowing()
    {
        var file = _File("vanishes.md");
        var resources = new[] { new ProjectResource(file, ProjectResourceRole.Instructions) { SendsContent = true } };

        var result = ProjectInstructionContentReader.Read(
            resources,
            fileLength: _ => 10,
            readAllText: _ => throw new FileNotFoundException("simulated race"));

        Assert.Empty(result);
    }

    [Fact]
    public void AnUnreadableFile_IsLeftOutRatherThanThrowing()
    {
        var file = _File("locked.md");
        var resources = new[] { new ProjectResource(file, ProjectResourceRole.Instructions) { SendsContent = true } };

        var result = ProjectInstructionContentReader.Read(
            resources,
            fileLength: _ => 10,
            readAllText: _ => throw new UnauthorizedAccessException("simulated permissions error"));

        Assert.Empty(result);
    }

    [Fact]
    public void FiveRowsTickedAtOnce_AreAllReadIndependently()
    {
        var resources = Enumerable.Range(0, 5).Select(i =>
        {
            var file = _File($"row-{i}.md");
            File.WriteAllText(file, $"content {i}");
            return new ProjectResource(file, ProjectResourceRole.Instructions) { SendsContent = true };
        }).ToList();

        var result = ProjectInstructionContentReader.Read(resources);

        Assert.Equal(5, result.Count);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal($"content {i}", result[resources[i].Reference]);
        }
    }

    [Fact]
    public void ABlankReference_IsNeverRead()
    {
        var resources = new[] { new ProjectResource("   ", ProjectResourceRole.Instructions) { SendsContent = true } };

        Assert.Empty(ProjectInstructionContentReader.Read(resources));
    }

    /// <summary>
    /// AC-486 review, must-fix 2: this class promised it "never blocks a session from starting", and every test
    /// here proved only that it never <em>throws</em>. Reading is heavier than the existence check its sibling
    /// probe does, and it runs on the thread that handles Start — a cloud-sync placeholder (OneDrive, Nextcloud
    /// "online-only") downloads when it is opened, which on these machines is how files are normally stored. A read
    /// that overruns its budget is dropped, and the session is told the content did not make it in.
    /// </summary>
    [Fact]
    public void AReadThatDoesNotReturnPromptly_IsGivenUpOnRatherThanWaitedOut()
    {
        var resources = new[] { new ProjectResource("/slow.md", ProjectResourceRole.Instructions) { SendsContent = true } };
        var started = new ManualResetEventSlim(initialState: false);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = ProjectInstructionContentReader.Read(
            resources,
            fileLength: _ => 10,
            readAllText: _ =>
            {
                started.Set();
                Thread.Sleep(TimeSpan.FromSeconds(5));
                return "far too late to be of use";
            });
        clock.Stop();

        Assert.True(started.Wait(TimeSpan.FromSeconds(1)), "the read has to have been attempted for this to prove anything");
        Assert.Empty(result);
        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(2),
            $"the caller waited {clock.Elapsed.TotalMilliseconds:F0} ms on a read that never returns — the budget is what stops Start freezing");
    }
}
