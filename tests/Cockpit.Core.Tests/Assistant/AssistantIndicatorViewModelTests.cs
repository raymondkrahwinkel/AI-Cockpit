using Cockpit.App.ViewModels;
using Cockpit.Core.Assistant;

namespace Cockpit.Core.Tests.Assistant;

/// <summary>
/// AC-543 (strand 3): the reusable assistant indicator's own state — every <see cref="AssistantActivity"/> maps
/// to a distinct label and colour class (criterion 6), F9 and F10 differ in both (the hard half of criterion 6),
/// the rail keeps colour and drops the label (criterion 19), the three listening stands are each readable
/// (criterion 17), and the AlwaysOn cost warning fires exactly once (criterion 18).
/// </summary>
public class AssistantIndicatorViewModelTests
{
    private static readonly AssistantActivity[] _AllActivities =
    [
        AssistantActivity.Ready,
        AssistantActivity.Listening,
        AssistantActivity.ListeningContinuously,
        AssistantActivity.Thinking,
        AssistantActivity.Speaking,
        AssistantActivity.Dictating,
        AssistantActivity.Unavailable,
    ];

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

    [Fact]
    public void EveryActivity_HasItsOwnColorClass()
    {
        var vm = new AssistantIndicatorViewModel();
        var classes = new List<string>();
        foreach (var activity in _AllActivities)
        {
            vm.Activity = activity;
            classes.Add(vm.ColorClass);
        }

        Assert.Equal(classes.Count, classes.Distinct().Count());
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
    public void ListeningMode_EachOfTheThreeStands_IsReadableWithoutClicking()
    {
        var vm = new AssistantIndicatorViewModel { ListeningMode = AssistantListeningMode.Off };
        Assert.True(vm.IsListeningModeOff);
        Assert.False(vm.IsListeningModeAlwaysOn);
        Assert.False(vm.IsListeningModeAlwaysOnWithWakeWord);

        vm.ListeningMode = AssistantListeningMode.AlwaysOn;
        Assert.False(vm.IsListeningModeOff);
        Assert.True(vm.IsListeningModeAlwaysOn);
        Assert.False(vm.IsListeningModeAlwaysOnWithWakeWord);

        vm.ListeningMode = AssistantListeningMode.AlwaysOnWithWakeWord;
        Assert.False(vm.IsListeningModeOff);
        Assert.False(vm.IsListeningModeAlwaysOn);
        Assert.True(vm.IsListeningModeAlwaysOnWithWakeWord);
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
    /// AlwaysOnWithWakeWord is not selectable this phase (comment 5) — picking it must be a no-op, not an
    /// exception, since the view still offers it as a click target ("not set up yet" rather than hidden).
    /// </summary>
    [Fact]
    public void SelectingAlwaysOnWithWakeWord_IsRefused_AndNeverCommits()
    {
        var vm = new AssistantIndicatorViewModel { ListeningMode = AssistantListeningMode.Off };
        var raised = false;
        vm.ListeningModeSelected += (_, _) => raised = true;

        vm.SelectListeningModeAlwaysOnWithWakeWordCommand.Execute(null);

        Assert.False(raised);
        Assert.Equal(AssistantListeningMode.Off, vm.ListeningMode);
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
