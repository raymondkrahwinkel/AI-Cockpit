using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-746: the "While you're talking" heading on the Assistant sub-page was bound to <c>VoiceEnabled</c> — the
/// Transcribe page's push-to-talk-dictation toggle, unrelated to this page — while its own heading carried no
/// binding at all. An operator with the assistant on and dictation off saw an orphaned heading over empty space.
/// Both the heading and its rows now share one visibility gate: the mic pipeline the section's open-mic/barge-in
/// features need (<c>VoiceEnabled</c>) and the assistant master switch (<c>AssistantOptions.IsEnabled</c>).
/// </summary>
[Collection("avalonia")]
public class AssistantWhileTalkingSectionTests
{
    private const string Heading = "While you're talking";

    [Theory]
    [InlineData(true, true, true)]   // both on: unchanged existing behaviour
    [InlineData(true, false, false)] // AC-746's reported bug: assistant on, dictation off — must hide, not orphan the heading
    [InlineData(false, true, false)] // the regression case from the ticket: the inverse combination must also hide
    [InlineData(false, false, false)]
    public void TheHeadingAndItsRows_ShowOrHideTogether(bool assistantEnabled, bool voiceEnabled, bool expectedVisible) =>
        HeadlessAvalonia.Run(() =>
        {
            var viewModel = new CockpitViewModel();
            viewModel.AssistantOptions.IsEnabled = assistantEnabled;
            viewModel.VoiceEnabled = voiceEnabled;

            var dialog = new OptionsDialog { DataContext = viewModel };
            dialog.Show();

            var tabs = dialog.GetVisualDescendants().OfType<TabControl>().Single();
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Header as string == "Voice");
            dialog.UpdateLayout();

            var rail = dialog.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "VoiceNav");
            rail.SelectedIndex = 1; // Assistant sub-page
            dialog.UpdateLayout();

            var heading = dialog.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == Heading);
            var stopAssistantCheckBox = dialog.GetVisualDescendants().OfType<CheckBox>()
                .Single(box => box.Content as string == "Stop the assistant when I start talking");

            Assert.Equal(expectedVisible, heading.IsEffectivelyVisible);
            Assert.Equal(expectedVisible, stopAssistantCheckBox.IsEffectivelyVisible);

            dialog.Close();
        });
}
