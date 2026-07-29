using Cockpit.App.ViewModels;
using Cockpit.Core.Voice;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-68 slice 1: the transcription-model dropdown wiring on the view model. The dropdown replaced a free-text
/// box, so these pin that a curated model drives the effective <c>VoiceModelName</c> directly, "Custom…" reveals
/// the free-text box and mirrors it, and — with no advisor in the design-time graph — the backend list is the
/// CPU-only Auto + CPU. Runs on the Avalonia collection because constructing the view model needs a platform.
/// </summary>
[Collection("avalonia")]
public class TranscriptionModelDropdownViewTests
{
    [Fact]
    public void WithoutAnAdvisor_TheBackendList_IsAutoAndCpuOnly() => HeadlessAvalonia.Run(() =>
    {
        var vm = new CockpitViewModel();
        Assert.Equal(
            new[] { VoiceBackendPreference.Auto, VoiceBackendPreference.Cpu },
            vm.VoiceBackendPreferences.Select(option => option.Value));
    });

    [Fact]
    public void TheModelDropdown_DefaultsToACuratedModel_NotCustom() => HeadlessAvalonia.Run(() =>
    {
        var vm = new CockpitViewModel();
        Assert.Equal("large-v3-turbo", vm.SelectedTranscriptionModel!.Name);
        Assert.False(vm.IsTranscriptionModelCustom);
    });

    [Fact]
    public void PickingCustom_RevealsTheBox_AndItsTextBecomesTheEffectiveModel() => HeadlessAvalonia.Run(() =>
    {
        var vm = new CockpitViewModel();

        vm.SelectedTranscriptionModel = vm.TranscriptionModelChoices.Single(model => model.IsCustom);
        Assert.True(vm.IsTranscriptionModelCustom, "the Custom… choice reveals the free-text box");

        vm.VoiceCustomModelName = "large-v3-turbo-q5_0";
        Assert.Equal("large-v3-turbo-q5_0", vm.VoiceModelName);
    });

    [Fact]
    public void PickingACuratedModel_SetsTheEffectiveModel_AndLeavesCustom() => HeadlessAvalonia.Run(() =>
    {
        var vm = new CockpitViewModel { SelectedTranscriptionModel = null };

        vm.SelectedTranscriptionModel = vm.TranscriptionModelChoices.Single(model => model.Name == "small");

        Assert.Equal("small", vm.VoiceModelName);
        Assert.False(vm.IsTranscriptionModelCustom);
    });
}
