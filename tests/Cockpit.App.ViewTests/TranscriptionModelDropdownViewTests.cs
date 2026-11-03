using Cockpit.App.ViewModels;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-68 slice 1: the transcription-model dropdown wiring on the view model. The dropdown replaced a free-text
/// box, so this pins that "Custom…" reveals the free-text box and that its text becomes the effective
/// <c>VoiceModelName</c>. Runs on the Avalonia collection because constructing the view model needs a platform.
/// </summary>
[Collection("avalonia")]
public class TranscriptionModelDropdownViewTests
{
    [Fact]
    public void PickingCustom_RevealsTheBox_AndItsTextBecomesTheEffectiveModel() => HeadlessAvalonia.Run(() =>
    {
        var vm = new CockpitViewModel();

        vm.SelectedTranscriptionModel = vm.TranscriptionModelChoices.Single(model => model.IsCustom);
        Assert.True(vm.IsTranscriptionModelCustom, "the Custom… choice reveals the free-text box");

        vm.VoiceCustomModelName = "large-v3-turbo-q5_0";
        Assert.Equal("large-v3-turbo-q5_0", vm.VoiceModelName);
    });
}
