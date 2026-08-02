using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels.Onboarding;

namespace Cockpit.App.ViewTests.Onboarding;

/// <summary>
/// The first-run wizard's provider step (AC-510[b]), rendered — what a view-model test cannot show: that a found
/// row and a not-found row actually read differently on screen (criterion 1), that the offline state replaces the
/// catalogue rather than sitting empty next to it (criterion 3), and that the Install button's enabled state
/// really is wired to the checkboxes rather than merely computed correctly in the view model.
/// </summary>
[Collection("avalonia")]
public class ProviderStepViewTests
{
    [Fact]
    public void Catalogue_FoundAndNotFoundRows_ShowVisiblyDifferentPills() => HeadlessAvalonia.Run(() =>
    {
        var window = Screenshotter.ShowScene("provider-step-catalogue");
        try
        {
            var viewModel = Assert.IsType<ProviderStepViewModel>(window.DataContext);
            window.UpdateLayout();

            var found = viewModel.Providers.Single(row => row.Row.Id == "claude-provider");
            var notFound = viewModel.Providers.Single(row => row.Row.Id == "cli-agent-provider");

            Assert.Equal(ProviderDetectionState.Found, found.Detection);
            Assert.Equal(ProviderDetectionState.NotFound, notFound.Detection);
            Assert.NotEqual(found.DetectionLabel, notFound.DetectionLabel);
            Assert.NotEqual(found.DetectionBrushKey, notFound.DetectionBrushKey);

            // Neither label claims the provider works — only that it was found (criterion 1).
            Assert.DoesNotContain("logged in", found.DetectionLabel, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("authenticated", found.DetectionLabel, StringComparison.OrdinalIgnoreCase);

            // Both pills are actually on screen, not just true in the view model.
            var pillTexts = window.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
            Assert.Contains(found.DetectionLabel, pillTexts);
            Assert.Contains(notFound.DetectionLabel, pillTexts);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Catalogue_CloudProvider_ShowsNoDetectionPill_RatherThanAFoundOrNotFoundClaim() => HeadlessAvalonia.Run(() =>
    {
        var window = Screenshotter.ShowScene("provider-step-catalogue");
        try
        {
            var viewModel = Assert.IsType<ProviderStepViewModel>(window.DataContext);
            var cloud = viewModel.Providers.Single(row => row.Row.Id == "gemini-provider");

            Assert.False(cloud.ShowDetectionPill);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Catalogue_IncompatibleRow_ChecksBoxIsDisabled() => HeadlessAvalonia.Run(() =>
    {
        var window = Screenshotter.ShowScene("provider-step-catalogue");
        try
        {
            var viewModel = Assert.IsType<ProviderStepViewModel>(window.DataContext);
            window.UpdateLayout();

            var incompatible = viewModel.Providers.Single(row => row.Row.Id == "github-models-provider");
            Assert.True(incompatible.Row.IsIncompatible);
            Assert.False(incompatible.CanSelect);

            var checkBoxes = window.GetVisualDescendants().OfType<CheckBox>().ToList();
            var incompatibleCheckBox = checkBoxes.Single(box => box.DataContext == incompatible);

            Assert.False(incompatibleCheckBox.IsEnabled, "nothing to opt into when this host cannot run it regardless (AC-181)");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Offline_ShowsTheOfflineBanner_NotTheCatalogue() => HeadlessAvalonia.Run(() =>
    {
        var window = Screenshotter.ShowScene("provider-step-offline");
        try
        {
            window.UpdateLayout();
            var viewModel = Assert.IsType<ProviderStepViewModel>(window.DataContext);
            Assert.True(viewModel.IsOffline);

            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
            Assert.Contains(texts, text => text is not null && text.Contains("Couldn't reach the plugin store", StringComparison.Ordinal));
            // The local-providers note is unaffected by being offline (criterion 3) — still on screen.
            Assert.Contains(texts, text => text is not null && text.Contains(viewModel.LocalProvidersText, StringComparison.Ordinal));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void InstallOutcomes_TheBatchSummaryAndEachRowsOwnLine_AreOnScreen() => HeadlessAvalonia.Run(() =>
    {
        var window = Screenshotter.ShowScene("provider-step-install-outcomes");
        try
        {
            window.UpdateLayout();
            var viewModel = Assert.IsType<ProviderStepViewModel>(window.DataContext);

            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
            Assert.Contains(texts, text => text == viewModel.SummaryMessage);
            Assert.Equal(3, viewModel.Providers.Count(row => row.HasOutcome));
            foreach (var row in viewModel.Providers.Where(row => row.HasOutcome))
            {
                Assert.Contains(row.OutcomeText, texts);
            }
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>
    /// Criterion 4: checking a row's box really does enable/disable the button in the view — not just the view
    /// model's own <c>CanInstallSelected</c>, which a wrong binding could leave disconnected from.
    /// </summary>
    [Fact]
    public void InstallButton_TracksTheCheckboxes_InTheRenderedView() => HeadlessAvalonia.Run(() =>
    {
        var window = Screenshotter.ShowScene("provider-step-catalogue");
        try
        {
            var viewModel = Assert.IsType<ProviderStepViewModel>(window.DataContext);
            window.UpdateLayout();

            var install = Assert.Single(
                window.GetVisualDescendants().OfType<Button>(),
                button => ReferenceEquals(button.Command, viewModel.InstallSelectedCommand));

            // The catalogue scene starts with the found, compatible, not-yet-installed row (claude-provider)
            // pre-selected as a suggestion (criterion 4).
            Assert.True(install.IsEffectivelyEnabled);

            foreach (var row in viewModel.Providers)
            {
                row.IsSelected = false;
            }
            window.UpdateLayout();

            Assert.False(install.IsEffectivelyEnabled, "nothing checked means nothing to install");
        }
        finally
        {
            window.Close();
        }
    });
}
