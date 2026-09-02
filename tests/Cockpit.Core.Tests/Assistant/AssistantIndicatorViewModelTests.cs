using Cockpit.App.ViewModels;
using Cockpit.Core.Assistant;

namespace Cockpit.Core.Tests.Assistant;

/// <summary>
/// AC-543 (strand 3): the reusable assistant indicator's own state — every <see cref="AssistantActivity"/> maps
/// to a distinct label and colour class (criterion 6), F9 and F10 differ in both (the hard half of criterion 6),
/// the rail keeps colour and drops the label (criterion 19), the two listening stands are each readable
/// (criterion 17 — "Wake word" left the picker in the vormgeving pass, 2026-08-01), and the AlwaysOn cost warning
/// fires exactly once (criterion 18).
/// </summary>
public class AssistantIndicatorViewModelTests
{
    /// <summary>
    /// Every state the enum declares, read from the enum rather than listed here.
    /// </summary>
    /// <remarks>
    /// It was a hand-written list, and that is a test which cannot fail for the reason it exists. Adding
    /// <see cref="AssistantActivity.AwaitingOperator"/> to the enum left <c>EveryActivity_HasItsOwnColorClass</c>
    /// green while the new state had no colour of its own at all — the assertion was about seven strings someone
    /// had typed out, not about the states the chip can actually be put into. A list that has to be maintained
    /// alongside the thing it claims to cover only ever falls behind it silently.
    /// </remarks>
    private static readonly AssistantActivity[] _AllActivities = Enum.GetValues<AssistantActivity>();

    [Fact]
    public void EveryActivity_HasItsOwnLabel()
    {
        var vm = new AssistantIndicatorViewModel();
        var labels = new List<string>();
        foreach (var activity in _AllActivities)
        {
            vm.Activity = activity;
            labels.Add(vm.Label);
        }

        Assert.Equal(labels.Count, labels.Distinct().Count());
    }

    /// <summary>
    /// Every activity has its own colour, with one deliberate exception: Thinking, Transcribing and Preparing
    /// share it (2026-08-08, when the last two moved off the voice pill). All three are the assistant working
    /// through something you are waiting on, and three shades of "wait" would be three things to learn for one
    /// meaning — the words are what tell them apart, which is what criterion 6 asks of a state anyway.
    /// </summary>
    [Fact]
    public void EveryActivity_HasItsOwnColorClass_ExceptTheThreeThatMeanWaiting()
    {
        var vm = new AssistantIndicatorViewModel();
        var classes = new List<string>();
        foreach (var activity in _AllActivities)
        {
            vm.Activity = activity;
            classes.Add(vm.ColorClass);
        }

        var waiting = new[] { AssistantActivity.Thinking, AssistantActivity.Transcribing, AssistantActivity.Preparing };
        Assert.Equal(classes.Count - waiting.Length + 1, classes.Distinct().Count());
        Assert.All(waiting, activity =>
        {
            vm.Activity = activity;
            Assert.Equal("thinking", vm.ColorClass);
        });
    }

    /// <summary>
    /// Criterion 6, the hard requirement: dictating (F9, into a session) must never read like the assistant
    /// listening (F10) — not in the word, and not in the colour class either, since colour alone is explicitly
    /// ruled insufficient.
    /// </summary>
    [Fact]
    public void Dictating_DiffersFromListening_InLabelAndColorClass()
    {
        var dictating = new AssistantIndicatorViewModel { Activity = AssistantActivity.Dictating };
        var listening = new AssistantIndicatorViewModel { Activity = AssistantActivity.Listening };

        Assert.NotEqual(dictating.Label, listening.Label);
        Assert.NotEqual(dictating.ColorClass, listening.ColorClass);
    }

    [Fact]
    public void Dictating_SaysInWords_ThatItIsNotTheAssistant()
    {
        var vm = new AssistantIndicatorViewModel { Activity = AssistantActivity.Dictating };

        Assert.Contains("not the assistant", vm.Detail);
    }

    [Fact]
    public void Unavailable_CarriesTheReason_AsItsDetail()
    {
        var vm = new AssistantIndicatorViewModel
        {
            Activity = AssistantActivity.Unavailable,
            UnavailableReason = "No model on this machine",
        };

        Assert.Equal("No model on this machine", vm.Detail);
    }

    /// <summary>
    /// Ready has no second line at all (Raymond, 2026-08-08). It used to name the provider/model; which model the
    /// assistant runs on is a setting, not something the chip has to keep repeating.
    /// </summary>
    [Fact]
    public void Ready_HasNoSecondLine()
    {
        var vm = new AssistantIndicatorViewModel { Activity = AssistantActivity.Ready };

        Assert.Null(vm.Detail);
    }

    [Theory]
    [InlineData(AssistantActivity.Listening)]
    [InlineData(AssistantActivity.ListeningContinuously)]
    [InlineData(AssistantActivity.Thinking)]
    [InlineData(AssistantActivity.Speaking)]
    public void EveryAssistantSideActivity_NamesTheAssistant_AsItsDetail(AssistantActivity activity)
    {
        var vm = new AssistantIndicatorViewModel { Activity = activity };

        Assert.Equal("Assistant", vm.Detail);
    }

    [Theory]
    [InlineData(AssistantActivity.Ready, "F10")]
    [InlineData(AssistantActivity.Listening, "F10")]
    [InlineData(AssistantActivity.Speaking, "Esc")]
    [InlineData(AssistantActivity.Dictating, "F9")]
    public void KeyHint_NamesTheKeyBoundToThatState(AssistantActivity activity, string expected)
    {
        var vm = new AssistantIndicatorViewModel { Activity = activity };

        Assert.Equal(expected, vm.KeyHint);
    }

    /// <summary>
    /// Thinking, ListeningContinuously and Unavailable have no key bound to them right now (criterion 6's key
    /// badge only names a key that actually does something) — the badge must hide rather than show empty.
    /// </summary>
    [Theory]
    [InlineData(AssistantActivity.ListeningContinuously)]
    [InlineData(AssistantActivity.Thinking)]
    [InlineData(AssistantActivity.Unavailable)]
    public void KeyHint_IsNull_WhereNoKeyIsBound(AssistantActivity activity)
    {
        var vm = new AssistantIndicatorViewModel { Activity = activity };

        Assert.Null(vm.KeyHint);
    }

    /// <summary>
    /// Criterion 19: "listening continuously" is a stand the operator switched on, "listening" is a handeling
    /// that lasts as long as F10 is held — they must read as different, not as the same word with a suffix.
    /// </summary>
    [Fact]
    public void ListeningContinuously_DiffersFromListening_InLabelAndColorClass()
    {
        var held = new AssistantIndicatorViewModel { Activity = AssistantActivity.Listening };
        var standing = new AssistantIndicatorViewModel { Activity = AssistantActivity.ListeningContinuously };

        Assert.NotEqual(held.Label, standing.Label);
        Assert.NotEqual(held.ColorClass, standing.ColorClass);
    }

    [Theory]
    [InlineData(AssistantActivity.Ready)]
    [InlineData(AssistantActivity.Listening)]
    [InlineData(AssistantActivity.Dictating)]
    public void Collapsing_KeepsTheColorClass_RegardlessOfActivity(AssistantActivity activity)
    {
        // The rail form drops the label — see AssistantIndicator.axaml — but the view model's own state, which
        // the ring colour is driven from, never changes just because the sidebar collapsed. IsCollapsed is a
        // presentation flag, not a fourth axis of Activity.
        var vm = new AssistantIndicatorViewModel { Activity = activity };
        var colorBeforeCollapse = vm.ColorClass;

        vm.IsCollapsed = true;

        Assert.Equal(colorBeforeCollapse, vm.ColorClass);
        Assert.True(vm.IsCollapsed);
    }

    [Fact]
    public void SelectingOff_NeverAsksForConfirmation_AndCommitsImmediately()
    {
        var vm = new AssistantIndicatorViewModel { ListeningMode = AssistantListeningMode.AlwaysOn };
        AssistantListeningMode? selected = null;
        vm.ListeningModeSelected += (_, mode) => selected = mode;

        vm.SelectListeningModeOffCommand.Execute(null);

        Assert.False(vm.IsAlwaysOnConfirmationPending);
        Assert.Equal(AssistantListeningMode.Off, selected);
    }

    /// <summary>Criterion 18: the first pick of AlwaysOn opens the explanation instead of committing straight away.</summary>
    [Fact]
    public void SelectingAlwaysOn_WhenNotYetAcknowledged_OpensTheConfirmation_AndDoesNotCommitYet()
    {
        var vm = new AssistantIndicatorViewModel { AlwaysOnCostAcknowledged = false };
        var raised = false;
        vm.ListeningModeSelected += (_, _) => raised = true;

        vm.SelectListeningModeAlwaysOnCommand.Execute(null);

        Assert.True(vm.IsAlwaysOnConfirmationPending);
        Assert.False(raised);
    }

    /// <summary>Criterion 18: once acknowledged, picking AlwaysOn again is immediate — no repeat warning.</summary>
    [Fact]
    public void SelectingAlwaysOn_WhenAlreadyAcknowledged_CommitsImmediately_WithNoConfirmation()
    {
        var vm = new AssistantIndicatorViewModel { AlwaysOnCostAcknowledged = true };
        AssistantListeningMode? selected = null;
        vm.ListeningModeSelected += (_, mode) => selected = mode;

        vm.SelectListeningModeAlwaysOnCommand.Execute(null);

        Assert.False(vm.IsAlwaysOnConfirmationPending);
        Assert.Equal(AssistantListeningMode.AlwaysOn, selected);
    }

    /// <summary>
    /// Criterion 18, the "exactly once" half: confirming raises the event exactly one time and flips the
    /// acknowledgement so a second pick later in the same session never reopens the explanation.
    /// </summary>
    [Fact]
    public void ConfirmingAlwaysOn_RaisesTheEventExactlyOnce_AndRemembersTheAcknowledgement()
    {
        var vm = new AssistantIndicatorViewModel { AlwaysOnCostAcknowledged = false };
        var raisedCount = 0;
        vm.ListeningModeSelected += (_, mode) =>
        {
            Assert.Equal(AssistantListeningMode.AlwaysOn, mode);
            raisedCount++;
        };

        vm.SelectListeningModeAlwaysOnCommand.Execute(null);
        vm.ConfirmAlwaysOnCommand.Execute(null);

        Assert.Equal(1, raisedCount);
        Assert.True(vm.AlwaysOnCostAcknowledged);
        Assert.False(vm.IsAlwaysOnConfirmationPending);

        // Picking it again in the same session must not reopen the explanation (criterion 18: once, not recurring).
        vm.SelectListeningModeAlwaysOnCommand.Execute(null);
        Assert.Equal(2, raisedCount);
        Assert.False(vm.IsAlwaysOnConfirmationPending);
    }

    [Fact]
    public void CancellingTheConfirmation_LeavesTheModeUnchanged_AndClosesThePrompt()
    {
        var vm = new AssistantIndicatorViewModel { ListeningMode = AssistantListeningMode.Off, AlwaysOnCostAcknowledged = false };
        var raised = false;
        vm.ListeningModeSelected += (_, _) => raised = true;

        vm.SelectListeningModeAlwaysOnCommand.Execute(null);
        vm.CancelAlwaysOnConfirmationCommand.Execute(null);

        Assert.False(vm.IsAlwaysOnConfirmationPending);
        Assert.False(raised);
    }

    /// <summary>
    /// The switch that replaced the Off / Always on row (2026-08-08) is the same two picks underneath — including
    /// the one-time cost explanation, which a switch must not quietly skip.
    /// </summary>
    [Fact]
    public void TogglingTheSwitch_MakesTheSamePicks_AndStillAsksBeforeTheFirstAlwaysOn()
    {
        var vm = new AssistantIndicatorViewModel { ListeningMode = AssistantListeningMode.Off, AlwaysOnCostAcknowledged = false };
        var selected = new List<AssistantListeningMode>();
        vm.ListeningModeSelected += (_, mode) => selected.Add(mode);

        vm.ToggleListeningModeCommand.Execute(null);
        Assert.True(vm.IsAlwaysOnConfirmationPending);
        Assert.Empty(selected);

        vm.ConfirmAlwaysOnCommand.Execute(null);
        Assert.Equal([AssistantListeningMode.AlwaysOn], selected);

        // The host applies the pick and feeds the stand back; toggling from there turns the microphone off again.
        vm.ListeningMode = AssistantListeningMode.AlwaysOn;
        vm.ToggleListeningModeCommand.Execute(null);
        Assert.Equal([AssistantListeningMode.AlwaysOn, AssistantListeningMode.Off], selected);
    }

    /// <summary>
    /// The level arc only moves in the states that have a microphone, and closes on the way out — so a chip that
    /// is thinking never shows the tail of the hold before it. The decay between frames is what keeps a jittering
    /// RMS from reading as flicker.
    /// </summary>
    [Fact]
    public void PushLevel_OnlyDrawsInTheStatesWithAMicrophone_AndClosesOnLeaving()
    {
        var vm = new AssistantIndicatorViewModel { Activity = AssistantActivity.Thinking };

        vm.PushLevel(1);
        Assert.Equal(0, vm.Level);

        vm.Activity = AssistantActivity.Listening;
        vm.PushLevel(1);
        Assert.Equal(1, vm.Level);
        Assert.Equal(360, vm.LevelSweep);

        // Silence does not drop the arc to nothing in one frame — it falls back.
        vm.PushLevel(0);
        Assert.Equal(0.85, vm.Level, 3);

        vm.Activity = AssistantActivity.Thinking;
        Assert.Equal(0, vm.Level);
    }

    /// <summary>
    /// The two states that moved off the floating voice pill (2026-08-08). Preparing leads with the step, because
    /// on first use it is a gigabyte-and-a-half download and "Preparing…" alone does not explain a four-minute
    /// wait; the percentage rides the second line, and a step with no known total shows none rather than one it
    /// made up.
    /// </summary>
    [Fact]
    public void Preparing_LeadsWithTheStep_AndOnlyShowsAPercentageWhenThereIsATotal()
    {
        var vm = new AssistantIndicatorViewModel
        {
            Activity = AssistantActivity.Preparing,
            PreparationStatus = "Downloading speech model",
            PreparationProgress = 0.63,
        };

        Assert.Equal("Downloading speech model", vm.Label);
        Assert.Equal("63%", vm.Detail);
        Assert.True(vm.ShowsPreparationProgress);

        vm.PreparationProgress = null;
        Assert.Null(vm.Detail);
        Assert.False(vm.ShowsPreparationProgress);
    }

    /// <summary>
    /// Transcribing, Preparing and Thinking share one colour and one glyph — all three are the assistant working
    /// through something you are waiting on — and are told apart by their words.
    /// </summary>
    [Theory]
    [InlineData(AssistantActivity.Thinking, "Thinking…")]
    [InlineData(AssistantActivity.Transcribing, "Transcribing…")]
    public void TheWaitingStates_ShareAColourAndAGlyph_ButNotTheirWords(AssistantActivity activity, string label)
    {
        var vm = new AssistantIndicatorViewModel { Activity = activity };

        Assert.Equal(label, vm.Label);
        Assert.Equal("thinking", vm.ColorClass);
        Assert.True(vm.IsWorking);
        Assert.False(vm.ShowsLevel);
    }

    [Fact]
    public void Click_RaisesClicked()
    {
        var vm = new AssistantIndicatorViewModel();
        var raised = false;
        vm.Clicked += (_, _) => raised = true;

        vm.ClickCommand.Execute(null);

        Assert.True(raised);
    }
}
