using NSubstitute;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// <see cref="ProjectDialogViewModel"/>'s Save-then-write-back path (AC-247): what happens once the operator
/// presses Save on a project a source claimed editable — success, permission denied, and the checksum-conflict
/// window's three answers (cancel, take theirs, merge my own edit onto the fresh remote state).
/// </summary>
public class ProjectDialogWriteBackTests
{
    private static ISessionProfileStore ProfileStore(params string[] labels)
    {
        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(
            labels.Select(label => new SessionProfile(label, new ClaudeConfig("~/.claude"))).ToList());
        return store;
    }

    private static IMcpServerCatalog Catalog() => Substitute.For<IMcpServerCatalog>();

    private static SharedProjectBinding Baseline(
        string name = "Cockpit", string? description = null, string? behaviorPrompt = null,
        bool isolate = false, IReadOnlyList<string>? enabledMcp = null, string checksum = "chk-open") =>
        new(name)
        {
            Description = description,
            BehaviorPrompt = behaviorPrompt,
            IsolateInWorktreeByDefault = isolate,
            EnabledMcpServerNames = enabledMcp,
            Checksum = checksum,
        };

    private static Dictionary<HostProjectField, ProjectFieldOwnership?> EditableName() => new()
    {
        [HostProjectField.Name] = new ProjectFieldOwnership("Depot — Work", IsEditable: true),
    };

    // A source whose WriteBackAsync answer is scripted per call — most tests need only the first answer; the
    // merge-retry test needs a second, different one once the first call reports a conflict.
    private sealed class _FakeSource(params SharedProjectWriteBackResult[] answers) : ISharedProjectSource
    {
        private int _calls;

        public List<(SharedProjectDefinitionEdit Edit, string BaseChecksum)> Calls { get; } = [];

        public string Key => "depot";

        public string SourceName => "Depot — Work";

        public Task<SharedProjectListResult> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("not exercised by SaveAsync");

        public Task<SharedProjectBindingResult> PrepareBindingAsync(string id, CancellationToken cancellationToken) =>
            throw new NotSupportedException("not exercised by SaveAsync");

        public Task<SharedProjectWriteBackResult> WriteBackAsync(
            string id, SharedProjectDefinitionEdit edit, string baseChecksum, CancellationToken cancellationToken)
        {
            Calls.Add((edit, baseChecksum));
            var answer = answers[Math.Min(_calls, answers.Length - 1)];
            _calls++;
            return Task.FromResult(answer);
        }

        public bool CanPublish => false;

        public Task<SharedProjectPublishTargetListResult> ListPublishTargetsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("not exercised by SaveAsync");

        public Task<SharedProjectPublishResult> PublishAsync(string targetId, SharedProjectPublishDefinition definition, CancellationToken cancellationToken) =>
            throw new NotSupportedException("not exercised by SaveAsync");
    }

    private static async Task<ProjectDialogViewModel> ViewModelAsync(
        Project project, ISharedProjectSource source, SharedProjectBinding baseline) =>
        await ProjectDialogViewModel.CreateAsync(
            project, ProfileStore("personal"), Catalog(),
            fieldOwnership: EditableName(),
            sharedWriteBack: new ProjectSharedWriteBackContext(source, "depot:cockpit", baseline));

    [Fact]
    public async Task SaveAsync_Success_ClosesWithTheEditedProject()
    {
        var project = Project.Create("Cockpit");
        var source = new _FakeSource(SharedProjectWriteBackResult.Success("chk-after"));
        var viewModel = await ViewModelAsync(project, source, Baseline());
        viewModel.Name = "Edited name";

        Project? closed = null;
        var closedAtAll = false;
        viewModel.CloseRequested += result => { closedAtAll = true; closed = result; };

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(closedAtAll);
        Assert.Equal("Edited name", closed!.Name);
        Assert.Null(viewModel.SaveError);
    }

    [Fact]
    public async Task SaveAsync_PermissionDenied_SetsSaveErrorAndDoesNotClose()
    {
        var project = Project.Create("Cockpit");
        var source = new _FakeSource(SharedProjectWriteBackResult.PermissionDenied("You are a Viewer on this project."));
        var viewModel = await ViewModelAsync(project, source, Baseline());
        viewModel.Name = "Edited name"; // an edit is required — SaveAsync now skips the write-back entirely when nothing changed.

        var closed = false;
        viewModel.CloseRequested += _ => closed = true;

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.False(closed);
        Assert.Equal("You are a Viewer on this project.", viewModel.SaveError);
    }

    [Fact]
    public async Task SaveAsync_ConflictWithNoOneListening_FailsClosedRatherThanOverwritingOrDropping()
    {
        var project = Project.Create("Cockpit");
        var source = new _FakeSource(SharedProjectWriteBackResult.Conflict(Baseline(name: "Remote edit", checksum: "chk-now")));
        var viewModel = await ViewModelAsync(project, source, Baseline());
        viewModel.Name = "Edited name"; // an edit is required — SaveAsync now skips the write-back entirely when nothing changed.
        // ConflictRequested deliberately left unsubscribed.

        var closed = false;
        viewModel.CloseRequested += _ => closed = true;

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.False(closed);
        Assert.NotNull(viewModel.SaveError);
    }

    [Fact]
    public async Task SaveAsync_ConflictThenCancel_ReturnsToEditingWithNothingWritten()
    {
        var project = Project.Create("Cockpit");
        var source = new _FakeSource(SharedProjectWriteBackResult.Conflict(Baseline(name: "Remote edit", checksum: "chk-now")));
        var viewModel = await ViewModelAsync(project, source, Baseline());
        viewModel.Name = "Edited name"; // an edit is required — SaveAsync now skips the write-back entirely when nothing changed.
        viewModel.ConflictRequested += (_, _) => Task.FromResult<ProjectDefinitionConflictResolution?>(null);

        var closed = false;
        viewModel.CloseRequested += _ => closed = true;

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.False(closed);
        Assert.Null(viewModel.SaveError);
        Assert.Single(source.Calls); // no retry — the operator cancelled the conflict window, not chose a resolution.
    }

    [Fact]
    public async Task SaveAsync_ConflictThenTakeTheirs_ClosesWithTheRemoteValuesRatherThanTheOperatorsEdit()
    {
        var project = Project.Create("Cockpit");
        var source = new _FakeSource(SharedProjectWriteBackResult.Conflict(Baseline(name: "Remote edit", checksum: "chk-now")));
        var viewModel = await ViewModelAsync(project, source, Baseline());
        viewModel.ConflictRequested += (_, _) => Task.FromResult<ProjectDefinitionConflictResolution?>(new(TakeTheirs: true));
        viewModel.Name = "My edit";

        Project? closed = null;
        viewModel.CloseRequested += result => closed = result;

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(closed);
        Assert.Equal("Remote edit", closed!.Name);
        Assert.Equal("Remote edit", viewModel.Name); // the field itself is updated too, not only the saved project.
        Assert.Single(source.Calls); // "take theirs" never retries the write — nothing to write, remote already holds it.
    }

    [Fact]
    public async Task SaveAsync_ConflictThenApplyMine_RetriesWithAFieldByFieldMergeAndTheFreshChecksum()
    {
        // Baseline opened with Name="Cockpit", Description=null. The operator edits only Name. Remote moved
        // Description (a field the operator never touched) but left Name alone on its own side. The retry must
        // keep the operator's Name and adopt the remote's Description — never the reverse.
        var project = Project.Create("Cockpit");
        var conflict = SharedProjectWriteBackResult.Conflict(
            Baseline(name: "Cockpit", description: "Remote description", checksum: "chk-now"));
        var source = new _FakeSource(conflict, SharedProjectWriteBackResult.Success("chk-final"));
        var viewModel = await ViewModelAsync(project, source, Baseline(name: "Cockpit", description: null));
        viewModel.ConflictRequested += (_, _) => Task.FromResult<ProjectDefinitionConflictResolution?>(new(TakeTheirs: false));
        viewModel.Name = "My edited name";

        Project? closed = null;
        viewModel.CloseRequested += result => closed = result;

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(closed);
        Assert.Equal(2, source.Calls.Count);
        var retry = source.Calls[1];
        Assert.Equal("My edited name", retry.Edit.Name); // the operator touched Name — their value wins.
        Assert.Equal("Remote description", retry.Edit.Description); // the operator never touched Description — the fresh remote value wins.
        Assert.Equal("chk-now", retry.BaseChecksum); // retried with the fresh checksum, not the stale one.

        // Adversarial review finding: the saved project (and the field itself) must carry what the merge actually
        // sent, not what the operator's own untouched Description property still held — otherwise cockpit.json and
        // Depot disagree on Description from the moment this Save returns.
        Assert.Equal("Remote description", closed!.Description);
        Assert.Equal("Remote description", viewModel.Description);
    }

    [Fact]
    public async Task CreateAsync_AWriteBackProject_PopulatesFieldsFromTheFreshBaselineRatherThanTheStaleLocalCopy()
    {
        // Adversarial review finding: this machine's own stored Project can be stale (a colleague renamed the
        // project in Depot since this machine last synced it). Populating the editor from that stale copy would
        // let a plain, no-op-looking Save resend the old name with a checksum that legitimately matches Depot's
        // current state — a silent overwrite, not a caught conflict.
        var project = Project.Create("Cockpit") with { Description = "Stale local description" };
        var source = new _FakeSource(SharedProjectWriteBackResult.Success("chk-after"));

        var viewModel = await ViewModelAsync(project, source, Baseline(name: "Renamed in Depot", description: "Fresh remote description"));

        Assert.Equal("Renamed in Depot", viewModel.Name);
        Assert.Equal("Fresh remote description", viewModel.Description);
    }

    [Fact]
    public async Task SaveAsync_NothingChanged_ClosesWithoutCallingWriteBackAsync()
    {
        var project = Project.Create("Cockpit");
        var source = new _FakeSource(SharedProjectWriteBackResult.Failed("should never be called"));
        var viewModel = await ViewModelAsync(project, source, Baseline());
        // No edit at all — CreateAsync's own fresh-baseline population (see the test above) is what the operator sees.

        Project? closed = null;
        viewModel.CloseRequested += result => closed = result;

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(closed);
        Assert.Empty(source.Calls);
    }

    [Fact]
    public async Task SaveAsync_LogoPicked_SendsAReplaceLogoEditWithTheFilesBytes()
    {
        var project = Project.Create("Cockpit");
        var source = new _FakeSource(SharedProjectWriteBackResult.Success("chk-after"));
        var viewModel = await ViewModelAsync(project, source, Baseline());
        var picked = Path.GetTempFileName();
        var bytes = new byte[] { 137, 80, 78, 71 };
        try
        {
            await File.WriteAllBytesAsync(picked, bytes);
            viewModel.LogoSource = picked;

            await viewModel.SaveCommand.ExecuteAsync(null);

            var sent = Assert.Single(source.Calls).Edit.LogoEdit;
            Assert.NotNull(sent);
            Assert.Equal(bytes, sent!.PngBytes);
        }
        finally
        {
            File.Delete(picked);
        }
    }

    [Fact]
    public async Task SaveAsync_OnlyTheLogoWasTouched_StillCallsWriteBackAsyncRatherThanSkippingIt()
    {
        // AC-763: _MatchesBaseline must count an untouched-but-for-the-logo edit as a real change — otherwise a
        // logo-only save would silently never reach Depot.
        var project = Project.Create("Cockpit");
        var source = new _FakeSource(SharedProjectWriteBackResult.Success("chk-after"));
        var viewModel = await ViewModelAsync(project, source, Baseline());
        var picked = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(picked, [1]);
            viewModel.LogoSource = picked; // every other field left exactly as CreateAsync populated it

            await viewModel.SaveCommand.ExecuteAsync(null);

            Assert.Single(source.Calls);
        }
        finally
        {
            File.Delete(picked);
        }
    }

    [Fact]
    public async Task SaveAsync_LogoCleared_SendsAClearedLogoEdit()
    {
        var project = Project.Create("Cockpit") with { LogoPath = "/home/erik/logo.png" };
        var source = new _FakeSource(SharedProjectWriteBackResult.Success("chk-after"));
        var viewModel = await ViewModelAsync(project, source, Baseline());
        viewModel.ClearLogoCommand.Execute(null);

        await viewModel.SaveCommand.ExecuteAsync(null);

        var sent = Assert.Single(source.Calls).Edit.LogoEdit;
        Assert.NotNull(sent);
        Assert.Null(sent!.PngBytes);
    }

    [Fact]
    public async Task SaveAsync_ConflictThenTakeTheirs_RevertsAnyPickedLogoToo()
    {
        var project = Project.Create("Cockpit");
        var source = new _FakeSource(SharedProjectWriteBackResult.Conflict(Baseline(name: "Remote edit", checksum: "chk-now")));
        var viewModel = await ViewModelAsync(project, source, Baseline());
        viewModel.ConflictRequested += (_, _) => Task.FromResult<ProjectDefinitionConflictResolution?>(new(TakeTheirs: true));
        var picked = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(picked, [1]);
            viewModel.LogoSource = picked;

            await viewModel.SaveCommand.ExecuteAsync(null);

            Assert.Equal(string.Empty, viewModel.LogoSource); // this project opened with no logo at all
        }
        finally
        {
            File.Delete(picked);
        }
    }
}
