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

    // One behaviour, one test: a ticked file's bytes come back verbatim under the key it was named by. The rows
    // are the content classes that used to be a Fact each — empty, whitespace only, and multibyte unicode.
    [Theory]
    [InlineData("")]
    [InlineData("   \n\t  \n")]
    [InlineData("Lees dit zorgvuldig: 🚀 中文说明 café naïve — schema volgen.")]
    public void ATickedFile_IsReadFaithfully(string content)
    {
        var file = _File("faithful.md");
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

    // --- AC-605: a "~"-anchored reference is resolved before it ever reaches disk -----------------------------

    /// <summary>
    /// AC-605 (coordinator review): this reader used to hand <see cref="ProjectResource.Reference"/> straight to
    /// <c>new FileInfo(path)</c>/<c>File.ReadAllText(path)</c> — both throw for a literal <c>"~/..."</c> string,
    /// which this reader's own unreadable-file <c>catch</c> then swallowed the same way it swallows a genuinely
    /// missing file, so a ticked "~/house-rules.md" row silently never reached a session. Proven here against the
    /// <em>content</em> a session would actually receive, not merely "something came back" — a test that only
    /// checked the result was non-empty stayed green through exactly this bug, because a broken resolve and a
    /// working one can both produce a non-empty dictionary keyed by the same stored reference if the assertion
    /// never looks at what path the hooks were actually called with or what the row's own key maps to.
    /// </summary>
    [Fact]
    public void AHomeAnchoredReference_IsResolvedBeforeEitherHookRunsAndKeptUnderItsOwnStoredReference()
    {
        const string reference = "~/house-rules.md";
        var resolvedPath = ProjectResourcePathPortability.ResolveHomeAnchor(reference);
        // The whole point of this test is that resolution actually changes the path handed to disk — if it did
        // not (a $HOME so exotic that "~/house-rules.md" already looks fully qualified as typed, which never
        // happens on any platform this repo builds on), the assertions below would pass trivially and prove
        // nothing about the fix.
        Assert.NotEqual(reference, resolvedPath);
        var resources = new[] { new ProjectResource(reference, ProjectResourceRole.Instructions) { SendsContent = true } };

        var result = ProjectInstructionContentReader.Read(
            resources,
            fileLength: path =>
            {
                Assert.Equal(resolvedPath, path);
                return 10;
            },
            readAllText: path =>
            {
                Assert.Equal(resolvedPath, path);
                return "Always write tests first.";
            });

        Assert.True(result.ContainsKey(reference));
        Assert.Equal("Always write tests first.", result[reference]);
        // Keyed by the stored reference, never by the resolved path — SessionStartDefaults.Resolve looks a row's
        // content up by that row's own Reference (its stored, unresolved text), so a result keyed by the resolved
        // path would be unfindable by the only key its caller ever uses.
        Assert.False(result.ContainsKey(resolvedPath));
    }

    /// <summary>
    /// AC-605 review: the same file named two different ways (an anchor form and its own already-resolved absolute
    /// form) is not deduplicated into one entry — each row is read and kept under its own stored Reference, because
    /// that is the only key <see cref="Cockpit.Core.Sessions.SessionStartDefaults.Resolve"/> ever looks a row up
    /// by. Both entries carry the same content because both resolve to the same underlying path (proven by the
    /// hook receiving that identical path for each), which is the point: this is a deliberate "read it twice, key
    /// it twice" choice, not a missed dedup opportunity.
    /// </summary>
    [Fact]
    public void TwoRowsNamingTheSameFileThroughDifferentReferenceForms_AreBothReadAndKeptUnderTheirOwnKey()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var anchoredForm = "~/shared.md";
        var resolvedForm = Path.Combine(home, "shared.md");
        Assert.Equal(ProjectResourcePathPortability.ResolveHomeAnchor(anchoredForm), resolvedForm);
        var resources = new[]
        {
            new ProjectResource(anchoredForm, ProjectResourceRole.Instructions) { SendsContent = true },
            new ProjectResource(resolvedForm, ProjectResourceRole.Instructions) { SendsContent = true },
        };

        var result = ProjectInstructionContentReader.Read(
            resources,
            fileLength: _ => 10,
            readAllText: path => $"content for {path}");

        Assert.Equal(2, result.Count);
        Assert.Equal($"content for {resolvedForm}", result[anchoredForm]);
        Assert.Equal($"content for {resolvedForm}", result[resolvedForm]);
    }

    /// <summary>AC-605: a form starting with "~" that is not a supported anchor (Raymond's decision — only "~" itself and "~/..." resolve) is left exactly as typed, so it reads (and fails to read) as ordinary relative text, not as this operator's home.</summary>
    [Fact]
    public void ATildeReferenceThatIsNotASupportedAnchorForm_IsPassedThroughUnresolved()
    {
        const string reference = "~henk/private-notes.md";
        var resources = new[] { new ProjectResource(reference, ProjectResourceRole.Instructions) { SendsContent = true } };

        var result = ProjectInstructionContentReader.Read(
            resources,
            fileLength: path =>
            {
                Assert.Equal(reference, path);
                return 10;
            },
            readAllText: path =>
            {
                Assert.Equal(reference, path);
                return "unchanged";
            });

        Assert.Equal("unchanged", result[reference]);
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

    // --- AC-612: a row pointing at a likely secrets location is never opened, however it got its tick -------------

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
