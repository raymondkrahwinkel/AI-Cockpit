using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-746: the rows were bound to <c>VoiceEnabled</c> — the Transcribe page's dictation toggle, unrelated
/// here — while the heading itself had no binding, so assistant-on/dictation-off orphaned it over empty space.
/// Heading and rows now share one gate: the mic pipeline (<c>VoiceEnabled</c>) and the assistant switch
/// (<c>AssistantOptions.IsEnabled</c>), both of which open-mic/barge-in actually need.
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
