using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;
using Cockpit.Core.Profiles;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>The Options → Voice "Assistant" block (AC-543): the master switch, the Assistant Profile picker, the hotkey, and the independent speak-replies switch.</summary>
public class AssistantOptionsViewModelTests
{
    // Decision 7 / criterion 1: a fresh dialog with nothing saved yet reads as off, before any store is even asked.
    [Fact]
    public void Constructed_WithNoStores_StartsDisabled()
    {
        var vm = new AssistantOptionsViewModel();

        Assert.False(vm.IsEnabled);
    }

    [Fact]
    public async Task TogglingIsEnabled_PersistsThroughTheSettingsStore_WithoutTouchingSiblingFields()
    {
        // The indicator (a different strand) owns AlwaysOnCostAcknowledged; a save from this view model must not reset it.
        var settingsStore = new FakeSettingsStore(new AssistantSettings { AlwaysOnCostAcknowledged = true });
        var vm = new AssistantOptionsViewModel(settingsStore);
        await vm.RefreshAsync();

        vm.IsEnabled = true;

        Assert.True(settingsStore.Saved!.IsEnabled);
        Assert.True(settingsStore.Saved!.AlwaysOnCostAcknowledged);
    }

    // Criterion 9: speaking and being enabled are two separate switches.
    [Fact]
    public async Task TogglingSpeakReplies_DoesNotChangeIsEnabled()
    {
        var settingsStore = new FakeSettingsStore(new AssistantSettings { IsEnabled = true });
        var vm = new AssistantOptionsViewModel(settingsStore);
        await vm.RefreshAsync();

        vm.SpeakReplies = false;

        Assert.False(settingsStore.Saved!.SpeakReplies);
        Assert.True(settingsStore.Saved!.IsEnabled);
    }

    [Fact]
    public async Task RefreshAsync_ShowsTheSlotsFixedName_NotTheRecordsLabel()
    {
        var profileStore = new FakeProfileStore(new AssistantProfileSlot(new SessionProfile("My Claude login", new ClaudeConfig("/tmp"))));
        var vm = new AssistantOptionsViewModel(profileStore: profileStore);

        await vm.RefreshAsync();

        Assert.Equal("Assistant Profile", AssistantOptionsViewModel.ProfileSlotDisplayName);
        Assert.Equal("My Claude login", vm.SelectedProfile!.Label);
    }

    [Fact]
    public async Task RefreshAsync_WhenSlotIsUnset_SurfacesTheReason()
    {
        var profileStore = new FakeProfileStore(new AssistantProfileSlot(null, "The Codex switch failed: no API key."));
        var vm = new AssistantOptionsViewModel(profileStore: profileStore);

        await vm.RefreshAsync();

        Assert.Null(vm.SelectedProfile);
        Assert.Equal("The Codex switch failed: no API key.", vm.ProfileUnsetReason);
    }

    [Fact]
    public async Task SelectingAProfile_RepointsTheSlot()
    {
        var profileStore = new FakeProfileStore(new AssistantProfileSlot(null, "not set up yet"));
        var vm = new AssistantOptionsViewModel(profileStore: profileStore);
        await vm.RefreshAsync();
        var chosen = new SessionProfile("Codex", new ClaudeConfig("/tmp"));

        vm.SelectedProfile = chosen;
        await vm.PendingProfileRepoint!;

        Assert.Equal(chosen, profileStore.RepointedTo);
        Assert.Null(vm.ProfileUnsetReason);
    }

    private sealed class FakeSettingsStore(AssistantSettings initial) : IAssistantSettingsStore
    {
        public AssistantSettings? Saved { get; private set; }

        public Task<AssistantSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(initial);

        public Task SaveAsync(AssistantSettings settings, CancellationToken cancellationToken = default)
        {
            Saved = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProfileStore(AssistantProfileSlot initial) : IAssistantProfileStore
    {
        public SessionProfile? RepointedTo { get; private set; }

        public Task<AssistantProfileSlot> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(initial);

        public Task<AssistantProfileSlot> RepointAsync(SessionProfile record, CancellationToken cancellationToken = default)
        {
            RepointedTo = record;
            return Task.FromResult(new AssistantProfileSlot(record));
        }

        public Task<AssistantProfileSlot> UnsetAsync(string reason, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AssistantProfileSlot(null, reason));
    }
}
