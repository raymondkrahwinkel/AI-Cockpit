using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Sessions;

// AC-1531: the conversation index's search over its own rows and their transcripts, and what a reopen makes of a row.
public sealed class ConversationIndexStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"conversation-index-{Guid.NewGuid():N}")).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ConversationIndexStore Store(IProjectStore? projects = null) =>
        new(Path.Combine(_root, "conversations.jsonl"), NullLogger<ConversationIndexStore>.Instance, projects);

    private ConversationRecord Row(string pane, string title, string project, int day, SessionConversationIdState state = SessionConversationIdState.Known, string? transcript = null) =>
        new(pane, title, project, _root, "Echo", "claude", state == SessionConversationIdState.Known ? "conversation-" + pane : null, state,
            new DateTimeOffset(2026, 10, day, 9, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, day, 10, 0, 0, TimeSpan.Zero),
            "Done", new DateTimeOffset(2026, 10, day, 11, 0, 0, TimeSpan.Zero), false, false, transcript);

    // One conversation per way of matching: its title, its project's name, a line of its transcript. The project
    // and from filters are held by EarlierConversationsJourney.
    [Theory]
    [InlineData(null, null, null, "by-title,by-project,by-transcript")]
    [InlineData(null, null, "2026-10-06", "by-title,by-project")]
    public async Task SearchAsync_FindsByTitleProjectNameAndTranscript_WithAFragment_WithinTheFilters(string? projectId, string? from, string? to, string expected)
    {
        var transcript = Path.Combine(_root, "by-transcript.jsonl");
        await File.WriteAllTextAsync(transcript, """{"Kind":"User","Text":"Heb je de mail van Jansen gezien?"}""" + Environment.NewLine);
        var projects = Substitute.For<IProjectStore>();
        projects.LoadAsync(Arg.Any<CancellationToken>()).Returns(new ProjectSettings { Projects = [new Project("p-jansen", "Jansen BV"), new Project("p-other", "Intern")] });
        var store = Store(projects);
        await store.RecordAsync(Row("by-title", "Offerte jansen", "p-other", 2));
        await store.RecordAsync(Row("by-project", "Kick-off", "p-jansen", 5));
        await store.RecordAsync(Row("by-transcript", "Losse vraag", "p-other", 8, transcript: transcript));

        var results = await store.SearchAsync("jansen", projectId, from is null ? null : DateTimeOffset.Parse(from), to is null ? null : DateTimeOffset.Parse(to));

        Assert.Equal(expected.Split(',').Order(), results.Select(result => result.Record.PaneId).Order());
        Assert.All(results, result => Assert.Contains("jansen", result.Fragment, StringComparison.OrdinalIgnoreCase));
    }

    // A row read back from the index plans a reopen only when its conversation is one the provider can resume.
    [Theory]
    [InlineData(SessionConversationIdState.Unsupported, SessionRestoreAvailability.Unsupported, "This provider does not keep a resumable conversation.")]
    [InlineData(SessionConversationIdState.Unknown, SessionRestoreAvailability.Unknown, "Nothing is known yet about this session's earlier conversation.")]
    [InlineData(SessionConversationIdState.Known, SessionRestoreAvailability.Known, "This session's earlier conversation can be resumed.")]
    public async Task ARowFromTheIndex_IsPlannedForReopenByItsConversationState(SessionConversationIdState state, SessionRestoreAvailability availability, string reason)
    {
        await Store().RecordAsync(Row("pane", "Jansen", "p-jansen", 2, state));
        var profiles = Substitute.For<ISessionProfileStore>();
        profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns([new SessionProfile("Echo", new ClaudeConfig(_root))]);

        var plan = await new SessionRestorePlanner(profiles).ComposeAsync(Assert.Single(await Store().LoadAsync()));

        Assert.Equal((availability, reason), (plan.Availability, plan.Explanation));
    }
}
