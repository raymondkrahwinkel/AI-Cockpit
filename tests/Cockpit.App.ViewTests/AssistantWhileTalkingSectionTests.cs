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
/// <remarks>
/// AC-1000: "while you're talking" moved from the old Assistant sub-page to the Voice category (it needs the mic
/// pipeline Voice owns at least as much as the assistant switch) — this test follows it there. The visibility
/// condition itself did not change.
/// </remarks>
[Collection("avalonia")]
public class AssistantWhileTalkingSectionTests
{
    // AC-1000: this section's heading is now one of the category page's numbered subsection headers, like every
    // other section on the page, rather than a plain unnumbered label.
    private const string Heading = "4. WHILE YOU'RE TALKING";

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

            // AC-1000: the sidebar's CategoryNav ListBox replaced the old per-tab TabControl + VoiceNav rail —
            // Voice is one flat page now, no sub-page selection needed.
            var nav = dialog.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "CategoryNav");
            nav.SelectedItem = nav.Items.OfType<ListBoxItem>().Single(item => item.Tag as string == "voice");
            dialog.UpdateLayout();

            var heading = dialog.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == Heading);
            var stopAssistantCheckBox = dialog.GetVisualDescendants().OfType<CheckBox>()
                .Single(box => box.Content as string == "Stop the assistant when I start talking");

            Assert.Equal(expectedVisible, heading.IsEffectivelyVisible);
            Assert.Equal(expectedVisible, stopAssistantCheckBox.IsEffectivelyVisible);

            dialog.Close();
        });
}
