using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// AC-529: the batching that replaced one <c>Dispatcher.UIThread.Post</c> per streamed event. Covers the queue's
/// drain protocol (nothing enqueued is ever left without a drain coming for it) and the coalescer's merge rules
/// (adjacent deltas fold, order never moves, and a fold never crosses a row boundary).
/// <para>
/// The dispatcher is a manual pump rather than a real one: a posted drain runs exactly when a test says so, which is
/// what makes "an event arrived while the drain was running" a deterministic case instead of a race to reproduce.
/// </para>
/// </summary>
public class SessionEventQueueTests
{
    private const string Sid = "S1";

    // --- the drain protocol ---------------------------------------------------------------------------------------

    [Fact]
    public void Enqueue_ABurstOfEvents_PostsOneDrainRatherThanOnePerEvent()
    {
        var pump = new ManualPump();
        var applied = new List<SessionEvent>();
        var queue = new SessionEventQueue(applied.Add, pump.Post);

        for (var i = 0; i < 10; i++)
        {
            queue.Enqueue(Text("x", block: 0));
        }

        Assert.Equal(1, pump.PostCount);

        pump.RunAll();
        Assert.Equal(10, applied.Sum(e => ((AssistantTextDelta)e).Text.Length));
    }

    [Fact]
    public void Drain_AppliesEveryEnqueuedEvent_InArrivalOrder()
    {
        var pump = new ManualPump();
        var applied = new List<SessionEvent>();
        var queue = new SessionEventQueue(applied.Add, pump.Post);

        var stream = new SessionEvent[]
        {
            Text("a", block: 0),
            new ToolUseRequested { SessionId = Sid, ToolUseId = "t1", ToolName = "Read", InputJson = "{}" },
            Text("b", block: 1),
            new ToolResult { SessionId = Sid, ToolUseId = "t1", Content = "ok", IsError = false },
            Text("c", block: 2),
        };

        foreach (var evt in stream)
        {
            queue.Enqueue(evt);
        }

        pump.RunAll();

        Assert.Equal(
            new[] { "a", "ToolUseRequested", "b", "ToolResult", "c" },
            applied.Select(Describe));
    }

    /// <summary>
    /// The case that would be silent loss: the last delta of a turn (and the turn's own end event) arriving while a
    /// drain is already running. The flag is cleared before the queue is read precisely so that such an event claims
    /// a drain of its own instead of finding one "already pending" that has in fact stopped looking.
    /// </summary>
    [Fact]
    public void Enqueue_WhileADrainIsRunning_PostsAFurtherDrain_SoTheLateEventStillLands()
    {
        var pump = new ManualPump();
        var applied = new List<SessionEvent>();
        SessionEventQueue? queue = null;
        var lateEventsSent = 0;

        queue = new SessionEventQueue(
            evt =>
            {
                applied.Add(evt);

                // The tail of the turn arrives while this very drain is applying what came before it. ("a" and "b"
                // fold into one applied event, so this fires on that fold — the drain is demonstrably mid-flight.)
                if (lateEventsSent++ == 0)
                {
                    queue!.Enqueue(Text("c", block: 0));
                    queue.Enqueue(new TurnCompleted { SessionId = Sid, Subtype = "success", Result = "ok", IsError = false });
                }
            },
            pump.Post);

        queue.Enqueue(Text("a", block: 0));
        queue.Enqueue(Text("b", block: 0));
        Assert.Equal(1, pump.PostCount);

        pump.RunAll();

        Assert.Equal(2, pump.PostCount);
        Assert.Equal(new[] { "ab", "c", "TurnCompleted" }, applied.Select(Describe));
    }

    /// <summary>
    /// One bad event must not cost its neighbours. With a post per event a throwing apply took down exactly that
    /// event; batching would have let it swallow the rest of the burst — a silently truncated turn, since
    /// <c>Program</c>'s unhandled-exception handler marks it handled and the app stays up.
    /// </summary>
    [Fact]
    public void Drain_WhenApplyThrowsPartWay_StillAppliesTheRestOfTheBatch_AndSurfacesTheFailureOnItsOwnPost()
    {
        var pump = new ManualPump();
        var applied = new List<SessionEvent>();
        var queue = new SessionEventQueue(
            evt =>
            {
                if (evt is UnknownEvent)
                {
                    // Thrown from a named method, not inline, so the frame it leaves in the stack is something the
                    // assertion below can name — see there for why the caller frames alone prove nothing.
                    ThrowTheFailingApply();
                }

                applied.Add(evt);
            },
            pump.Post);

        queue.Enqueue(Text("a", 0));
        queue.Enqueue(new UnknownEvent { SessionId = Sid, RawJson = "boom" });
        queue.Enqueue(Text("c", 0));
        Assert.Equal(1, pump.PostCount);

        // The drain applies "a", trips on the bad event, carries on with "c", and leaves the failure on a post of
        // its own — which is what the pump runs into next, exactly as the dispatcher would.
        var failure = Assert.Throws<InvalidOperationException>(pump.RunAll);

        Assert.Equal("boom", failure.Message);

        // The stack still names where the throw actually happened. It has to be asserted on a frame *below* the
        // re-post: the test method and the pump are on the stack either way (they are what ran the posted action),
        // so naming those would pass just as well against a plain `throw ex`, which resets the stack and loses the
        // real site.
        Assert.Contains(nameof(ThrowTheFailingApply), failure.StackTrace ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(new[] { "a", "c" }, applied.Select(Describe));

        // And the queue still works afterwards.
        queue.Enqueue(Text("d", 0));
        pump.RunAll();
        Assert.Equal(new[] { "a", "c", "d" }, applied.Select(Describe));
    }

    private static void ThrowTheFailingApply() => throw new InvalidOperationException("boom");

    [Fact]
    public void Drain_WithAnEmptyQueue_AppliesNothing()
    {
        var pump = new ManualPump();
        var applied = new List<SessionEvent>();
        var queue = new SessionEventQueue(applied.Add, pump.Post);

        queue.Enqueue(Text("a", block: 0));
        pump.RunAll();

        // A drain that finds nothing is the safe outcome of the flag protocol, and must stay a no-op.
        queue.Drain();

        Assert.Equal(new[] { "a" }, applied.Select(Describe));
    }

    /// <summary>
    /// A producer thread enqueueing while the consumer drains — the real arrangement, with a definite end rather
    /// than a sleep: the producer is joined, then drained until empty, and every event must have landed exactly once
    /// and in order.
    /// </summary>
    [Fact]
    public void Enqueue_FromTheProducerThreadWhileDrainsRun_LosesNothingAndKeepsOrder()
    {
        const int total = 5000;
        var pump = new ManualPump();
        var applied = new List<SessionEvent>();
        var queue = new SessionEventQueue(applied.Add, pump.Post);

        var producer = new Thread(() =>
        {
            for (var i = 0; i < total; i++)
            {
                queue.Enqueue(new UnknownEvent { SessionId = Sid, RawJson = i.ToString() });
            }
        });

        producer.Start();
        while (producer.IsAlive)
        {
            pump.RunAll();
        }

        producer.Join();
        pump.RunAll();

        Assert.Equal(total, applied.Count);
        Assert.Equal(
            Enumerable.Range(0, total).Select(i => i.ToString()),
            applied.Select(e => ((UnknownEvent)e).RawJson));
    }

    // --- the merge rules ------------------------------------------------------------------------------------------

    [Fact]
    public void Coalesce_AdjacentTextDeltasOnTheSameBlockAndLane_BecomeOneEventCarryingTheConcatenation()
    {
        var folded = SessionEventCoalescer.Coalesce([Text("Hel", 0), Text("lo w", 0), Text("orld", 0)]);

        var only = Assert.IsType<AssistantTextDelta>(Assert.Single(folded));
        Assert.Equal("Hello world", only.Text);
        Assert.Equal(0, only.BlockIndex);
    }

    [Fact]
    public void Coalesce_AdjacentThinkingDeltasOnTheSameBlock_BecomeOneEventCarryingTheConcatenation()
    {
        var folded = SessionEventCoalescer.Coalesce([Thinking("rea", 2), Thinking("son", 2)]);

        var only = Assert.IsType<AssistantThinkingDelta>(Assert.Single(folded));
        Assert.Equal("reason", only.Thinking);
        Assert.Equal(2, only.BlockIndex);
    }

    [Fact]
    public void Coalesce_ThinkingDeltasFromDifferentBlocks_StayApart_BecauseTheBlockIndexIsTheRowIdentity()
    {
        var folded = SessionEventCoalescer.Coalesce([Thinking("first", 0), Thinking("second", 1)]);

        Assert.Equal(2, folded.Count);
        Assert.Equal("first", ((AssistantThinkingDelta)folded[0]).Thinking);
        Assert.Equal("second", ((AssistantThinkingDelta)folded[1]).Thinking);
    }

    [Fact]
    public void Coalesce_TextDeltasFromDifferentBlocks_StayApart()
    {
        var folded = SessionEventCoalescer.Coalesce([Text("first", 0), Text("second", 1)]);

        Assert.Equal(2, folded.Count);
    }

    [Fact]
    public void Coalesce_DeltasFromDifferentSubAgentLanes_StayApart_SoNeitherLanesTextEverJoinsTheOthers()
    {
        var folded = SessionEventCoalescer.Coalesce(
        [
            Text("top", 0),
            Text("agent-a", 0, parent: "a"),
            Text("agent-b", 0, parent: "b"),
        ]);

        Assert.Equal(3, folded.Count);
        Assert.Null(folded[0].ParentToolUseId);
        Assert.Equal("a", folded[1].ParentToolUseId);
        Assert.Equal("b", folded[2].ParentToolUseId);
    }

    [Fact]
    public void Coalesce_TextAndThinkingDeltas_AreNeverFoldedIntoEachOther()
    {
        var folded = SessionEventCoalescer.Coalesce([Text("prose", 0), Thinking("reason", 0)]);

        Assert.Equal(2, folded.Count);
        Assert.IsType<AssistantTextDelta>(folded[0]);
        Assert.IsType<AssistantThinkingDelta>(folded[1]);
    }

    [Fact]
    public void Coalesce_DeltasSeparatedByANonDeltaEvent_StayApart_SoTheEventOrderIsUntouched()
    {
        var tool = new ToolUseRequested { SessionId = Sid, ToolUseId = "t1", ToolName = "Read", InputJson = "{}" };
        var folded = SessionEventCoalescer.Coalesce([Text("a", 0), Text("b", 0), tool, Text("c", 0), Text("d", 0)]);

        Assert.Equal(new[] { "ab", "ToolUseRequested", "cd" }, folded.Select(Describe));
    }

    [Fact]
    public void Coalesce_ABatchWithNothingToFold_IsHandedBackUntouched()
    {
        var batch = new SessionEvent[]
        {
            Text("a", 0),
            new ToolUseRequested { SessionId = Sid, ToolUseId = "t1", ToolName = "Read", InputJson = "{}" },
            Text("b", 0),
        };

        Assert.Same(batch, SessionEventCoalescer.Coalesce(batch));
    }

    [Fact]
    public void Coalesce_DeltasOfDifferentSessions_StayApart()
    {
        var folded = SessionEventCoalescer.Coalesce(
        [
            new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "a" },
            new AssistantTextDelta { SessionId = "S2", BlockIndex = 0, Text = "b" },
        ]);

        Assert.Equal(2, folded.Count);
    }

    // --- the whole point: the transcript must come out the same -----------------------------------------------------

    /// <summary>
    /// AC-529's "the view changes in smoothness, not in shape": a batch applied through the coalescer must leave the
    /// transcript byte-for-byte what applying every delta on its own would have left — same rows, same kinds, same
    /// text, sub-agent lanes included.
    /// </summary>
    [Fact]
    public void ACoalescedBatch_LeavesTheSameTranscript_AsApplyingEveryDeltaOnItsOwn()
    {
        var stream = MixedTurn();

        var oneByOne = new SessionViewModel(Substitute.For<ISessionManager>());
        foreach (var evt in stream)
        {
            oneByOne.Apply(evt);
        }

        var batched = new SessionViewModel(Substitute.For<ISessionManager>());
        foreach (var evt in SessionEventCoalescer.Coalesce(stream))
        {
            batched.Apply(evt);
        }

        Assert.Equal(Render(oneByOne), Render(batched));
    }

    private static List<string> Render(SessionViewModel vm)
    {
        var rows = new List<string>();

        void Walk(IEnumerable<TranscriptEntryViewModel> entries, string prefix)
        {
            foreach (var entry in entries)
            {
                rows.Add($"{prefix}{entry.Kind}:{entry.Text}");
                Walk(entry.SubAgentRows, prefix + ">");
            }
        }

        Walk(vm.Transcript, string.Empty);
        return rows;
    }

    /// <summary>One turn with everything that shares the streaming path: reasoning, prose, a tool round-trip, and a sub-agent lane.</summary>
    private static SessionEvent[] MixedTurn() =>
    [
        new SessionInitialized { SessionId = Sid, Cwd = "/repo", Tools = ["Read"] },
        Thinking("let ", 0), Thinking("me ", 0), Thinking("look", 0),
        Text("I will ", 1), Text("read ", 1), Text("the file.", 1),
        new ToolUseRequested { SessionId = Sid, ToolUseId = "t1", ToolName = "Read", InputJson = "{\"file_path\":\"/a.cs\"}" },
        new ToolResult { SessionId = Sid, ToolUseId = "t1", Content = "contents", IsError = false },
        Text("It ", 2), Text("says ", 2), Text("hello.", 2),
        new ToolUseRequested { SessionId = Sid, ToolUseId = "agent", ToolName = "Task", InputJson = "{\"prompt\":\"go\"}" },
        Thinking("sub ", 0, parent: "agent"), Thinking("thinks", 0, parent: "agent"),
        Text("sub ", 1, parent: "agent"), Text("answers", 1, parent: "agent"),

        // The lane and the top-level reply streaming back to back on the same block index, with nothing in between:
        // the arrangement in which a fold that ignored the lane would silently merge one row's text into the other's.
        Text("meanwhile ", 1), Text("the parent talks", 1),
        Text(" and", 1, parent: "agent"), Text(" so does the lane", 1, parent: "agent"),

        new ToolResult { SessionId = Sid, ToolUseId = "agent", Content = "done", IsError = false },
        Text("Done", 3), Text(".", 3),
        new TurnCompleted { SessionId = Sid, Subtype = "success", Result = "ok", IsError = false },
    ];

    private static AssistantTextDelta Text(string text, int block, string? parent = null) =>
        new() { SessionId = Sid, BlockIndex = block, Text = text, ParentToolUseId = parent };

    private static AssistantThinkingDelta Thinking(string text, int block, string? parent = null) =>
        new() { SessionId = Sid, BlockIndex = block, Thinking = text, ParentToolUseId = parent };

    private static string Describe(SessionEvent evt) => evt switch
    {
        AssistantTextDelta delta => delta.Text,
        AssistantThinkingDelta delta => delta.Thinking,
        _ => evt.GetType().Name,
    };

    /// <summary>Stands in for <c>Dispatcher.UIThread</c>: posted actions queue up and run only when the test pumps.</summary>
    private sealed class ManualPump
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();
        private int _postCount;

        public int PostCount => Volatile.Read(ref _postCount);

        public void Post(Action action)
        {
            Interlocked.Increment(ref _postCount);
            _posted.Enqueue(action);
        }

        public void RunAll()
        {
            while (_posted.TryDequeue(out var action))
            {
                action();
            }
        }
    }
}
