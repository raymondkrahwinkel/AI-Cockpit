using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.TestSupport;

namespace Cockpit.App.ViewTests;

// AC-1323 criterion 4: the pairing offer and the node card say, in the operator's words, that a controller's
// assistant runs sessions here — and the scope choice under it is the one AC-1292 drew, unchanged.
[Collection("avalonia")]
public class Ac1323PairingConsentTextTests
{
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void TheNodesPage_SaysWhatAControllerMayDoHere_OverTheSameScopeChoice(string variant) => HeadlessAvalonia.Run(() => ThemeVariants.Under(variant, () =>
    {
        var window = Screenshotter.ShowScene("options-nodes-paired");
        try
        {
            window.UpdateLayout();
            var security = ((CockpitViewModel)window.DataContext!).Security;
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(block => block.IsEffectivelyVisible).Select(block => block.Text ?? "").ToList();

            var offer = Assert.Single(texts, text => text.Contains("wants to pair", StringComparison.Ordinal));
            Assert.Contains("\"LAPTOP\"", offer);
            Assert.Contains("its assistant is the assistant here", offer);
            foreach (var verb in new[] { "starts", "stops", "steers", "reads their transcripts" })
            {
                Assert.Contains(verb, offer);
            }

            Assert.Contains("checked on every call", offer);
            Assert.Contains("Confirm only if the code below", offer);

            var card = Assert.Single(texts, text => text.StartsWith("Paired with", StringComparison.Ordinal));
            Assert.Contains("\"DESK\"", card);
            foreach (var verb in new[] { "start and stop sessions", "prompts and messages", "rename them", "read their transcripts" })
            {
                Assert.Contains(verb, card);
            }

            Assert.Contains(texts, text => text == "What this controller may see and control here");

            // The scope choice itself: the same two "all, including later" boxes, still bound to the pairing.
            var boxes = window.GetVisualDescendants().OfType<CheckBox>().Where(box => box.IsEffectivelyVisible).ToList();
            Assert.Contains(boxes, box => box.Content as string == "All profiles, including ones made later" && box.IsChecked == security.AllowAllProfiles);
            Assert.Contains(boxes, box => box.Content as string == "All projects, including ones made later" && box.IsChecked == security.AllowAllProjects);
            Assert.True(security.AllowAllProfiles && security.AllowAllProjects, "the scene pairs with everything allowed");
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), button => button.Content as string == "The codes match — pair");
        }
        finally
        {
            window.Close();
        }
    }));
}
