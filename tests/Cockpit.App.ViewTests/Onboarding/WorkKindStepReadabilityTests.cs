using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.ViewModels.Onboarding;
using Cockpit.Core.Plugins;

namespace Cockpit.App.ViewTests.Onboarding;

/// <summary>
/// What a first-time operator sees on the work-kind step (AC-1316, under AC-511): the kinds are choices and not a
/// sentence, a row leads with what the plugin does, and the grant, origin and checksum are folded but never hidden
/// (AC-489's rule). The standing terms are said once, where the Install button is.
/// </summary>
[Collection("avalonia")]
public class WorkKindStepReadabilityTests
{
    /// <summary>
    /// A plain label with a lighter tint for the chosen one is a sentence; a segment in a group is a choice. Choosing
    /// through the control — not the model — is what the operator does, so that is the route that has to set the
    /// model, and the caption under it has to say what that did to the list, including when it did nothing.
    /// </summary>
    [Fact]
    public void TheKinds_AreSegmentsInOneGroup_AndChoosingOneDrivesTheModelAndTheCaption() => HeadlessAvalonia.Run(() =>
    {
        var window = Screenshotter.ShowScene("first-run-work-kind");
        try
        {
            window.UpdateLayout();
            var model = (WorkKindStepViewModel)_Step(window).DataContext!;
            var segments = window.GetVisualDescendants().OfType<RadioButton>()
                .Where(button => button.Classes.Contains("Segment")).ToList();

            Assert.Equal(PluginWorkKinds.All.Select(kind => kind.Label), segments.Select(segment => (string?)segment.Content));
            Assert.All(segments, segment => Assert.Equal("WorkKind", segment.GroupName));

            // The scene chose Development; the rows carry that audience, so the caption says they were suggested.
            Assert.Same(PluginWorkKinds.All[0], model.SelectedWorkKind);
            Assert.Equal("Suggested for Development — change any tick.", model.ListCaption);

            // A kind no row is tagged for: the click lands, the list unticks, and the caption says so instead of
            // leaving a list of empty boxes to explain itself.
            var documents = segments.Single(segment => (string?)segment.Content == "Documents and design");
            documents.IsChecked = true;
            documents.Command!.Execute(documents.CommandParameter);

            Assert.Same(PluginWorkKinds.All[3], model.SelectedWorkKind);
            Assert.Equal("Nothing suggested for Documents and design yet — tick what you want.", model.ListCaption);
            Assert.All(model.Plugins, plugin => Assert.False(plugin.IsSelected));
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>
    /// The row leads with the store's description; the grant, origin and checksum sit behind the fold on every row —
    /// in the tree and one click away, not shown until asked. A row that dropped them would pass a test that only
    /// checked they are not visible, which is why both halves are asserted.
    /// </summary>
    [Fact]
    public void ARow_LeadsWithTheDescription_AndFoldsTheTechnicalLinesWithoutHidingThem() => HeadlessAvalonia.Run(() =>
    {
        var window = Screenshotter.ShowScene("first-run-work-kind");
        try
        {
            window.UpdateLayout();
            var rows = window.GetVisualDescendants().OfType<CheckBox>().Count();
            var texts = window.GetVisualDescendants().OfType<TextBlock>().ToList();

            var description = texts.Single(text => text.Text == "Browse open GitHub issues across your repos in a searchable, sortable dialog.");
            Assert.True(description.IsEffectivelyVisible, "the description leads the row and has to be visible without a click");

            var folded = texts.Where(text => text.Text is "From:" or "SHA-256 (pinned on install):" or "May:").ToList();
            Assert.Equal(rows * 3, folded.Count);
            Assert.All(folded, text => Assert.False(text.IsEffectivelyVisible, $"'{text.Text}' is shown before the fold is opened"));

            var fold = window.GetVisualDescendants().OfType<ToggleButton>().First(toggle => toggle.Name == "Fold");
            fold.IsChecked = true;
            window.UpdateLayout();

            var checksum = texts.First(text => text.Text?.StartsWith("9f2c4b1ea7d0", StringComparison.Ordinal) == true);
            Assert.True(checksum.IsEffectivelyVisible, "opening the fold has to show the checksum — folded, never hidden");
            Assert.True(texts.First(text => text.Text == PluginConsentTerms.PermissionSummary).IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>
    /// The sandbox warning is important and stays; said four times under each other it turns into noise people look
    /// past. Once, next to Install, and not again on the rows until a fold is opened.
    /// </summary>
    [Fact]
    public void TheSandboxWarning_IsVisibleExactlyOnce_UntilAFoldIsOpened() => HeadlessAvalonia.Run(() =>
    {
        var window = Screenshotter.ShowScene("first-run-work-kind");
        try
        {
            window.UpdateLayout();
            var visible = window.GetVisualDescendants().OfType<TextBlock>()
                .Where(text => text.IsEffectivelyVisible)
                .Count(text => text.Text is not null && text.Text.Contains("sandboxed", StringComparison.Ordinal));

            Assert.Equal(1, visible);
        }
        finally
        {
            window.Close();
        }
    });

    private static Control _Step(Window window) =>
        window.GetVisualDescendants().OfType<Views.Onboarding.WorkKindStepView>().First();
}
