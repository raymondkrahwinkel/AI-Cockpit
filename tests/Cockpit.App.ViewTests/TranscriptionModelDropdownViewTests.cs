using Cockpit.App.ViewModels;
using Cockpit.Core.Voice;
using FluentAssertions;

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
        vm.VoiceBackendPreferences.Select(option => option.Value)
            .Should().Equal(VoiceBackendPreference.Auto, VoiceBackendPreference.Cpu);
    });

    [Fact]
    public void TheModelDropdown_DefaultsToACuratedModel_NotCustom() => HeadlessAvalonia.Run(() =>
    {
        var vm = new CockpitViewModel();
        vm.SelectedTranscriptionModel!.Name.Should().Be("large-v3-turbo");
        vm.IsTranscriptionModelCustom.Should().BeFalse();
    });

    [Fact]
    public void PickingCustom_RevealsTheBox_AndItsTextBecomesTheEffectiveModel() => HeadlessAvalonia.Run(() =>
    {
        var vm = new CockpitViewModel();

        vm.SelectedTranscriptionModel = vm.TranscriptionModelChoices.Single(model => model.IsCustom);
        vm.IsTranscriptionModelCustom.Should().BeTrue("the Custom… choice reveals the free-text box");

        vm.VoiceCustomModelName = "large-v3-turbo-q5_0";
        vm.VoiceModelName.Should().Be("large-v3-turbo-q5_0", "the box drives the effective model while custom is active");
    });

    [Fact]
    public void PickingACuratedModel_SetsTheEffectiveModel_AndLeavesCustom() => HeadlessAvalonia.Run(() =>
    {
        var vm = new CockpitViewModel { SelectedTranscriptionModel = null };

        vm.SelectedTranscriptionModel = vm.TranscriptionModelChoices.Single(model => model.Name == "small");

        vm.VoiceModelName.Should().Be("small");
        vm.IsTranscriptionModelCustom.Should().BeFalse();
    });
}
