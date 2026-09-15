using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.TestSupport;

namespace Cockpit.App.ViewTests.Onboarding;

// AC-511: a batch that says less than the per-plugin dialog is a weakened guard, so the two screens are rendered and compared.
[Collection("avalonia")]
public class WorkKindConsentParityTests
{
    [Fact]
    public void TheStandingTerms_ReadTheSameOnBothScreens() => HeadlessAvalonia.Run(() =>
    {
        var dialog = _Texts("plugin-consent");
        var batch = _Texts("first-run-work-kind");

        Assert.Contains(PluginConsentTerms.PermissionsNotice, dialog, StringComparer.Ordinal);
        Assert.Contains(PluginConsentTerms.PermissionsNotice, batch, StringComparer.Ordinal);
    });

    // Two fields read differently because nothing is downloaded yet: the row names the store's zip and the published checksum.
    [Theory]
    [InlineData("GitHub Issues")] // identity: name
    [InlineData("1.1.0")] // identity: version
    [InlineData("Cockpit")] // identity: author
    [InlineData("From:")] // the dialog's "Location", before there is a folder
    [InlineData("SHA-256 (pinned on install):")] // the dialog's "SHA-256 (pinned on consent)"
    [InlineData("9f2c4b1ea7d05836c1b4e0f9a3d7c25e8b6041fd93a7e2c5b80d1a6a4e37c9b01")] // the checksum itself
    [InlineData("May:")] // said per row behind the fold, where the dialog says it once per dialog
    public void EveryFactTheDialogCarries_HasItsCounterpartOnARow(string expected) => HeadlessAvalonia.Run(() =>
    {
        var batch = _Texts("first-run-work-kind");

        Assert.Contains(batch, text => text.Contains(expected, StringComparison.Ordinal));
    });

    // The grant reads the same on every row only until AC-107 narrows it per plugin; the fold is already where that lands.
    [Fact]
    public void EachRow_SaysWhatItsPluginMay_RatherThanOnceForTheList() => HeadlessAvalonia.Run(() =>
    {
        var window = Screenshotter.ShowScene("first-run-work-kind");
        try
        {
            window.UpdateLayout();
            var grants = window.GetVisualDescendants().OfType<TextBlock>()
                .Count(text => text.Text == PluginConsentTerms.PermissionSummary);
            var rows = window.GetVisualDescendants().OfType<CheckBox>().Count();

            Assert.Equal(rows, grants);
            Assert.True(rows > 1, $"the long-list case needs more than one row to prove 'per row'; the scene rendered {rows}");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void TheMayLine_MeetsAaAgainstTheRowItSitsOn() => HeadlessAvalonia.Run(() =>
    {
        var window = Screenshotter.ShowScene("first-run-work-kind");
        try
        {
            window.UpdateLayout();

            var line = window.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(text => text.Text == PluginConsentTerms.PermissionSummary)
                ?? throw new InvalidOperationException("the work-kind step rendered no 'May' line to measure");

            var ink = Assert.IsType<ImmutableSolidColorBrush>(line.Foreground?.ToImmutable()).Color;
            var ground = _GroundBehind(line);
            var ratio = WcagContrast.Ratio(ink, ground);

            Assert.True(ratio >= WcagContrast.AaNormalText,
                $"the 'May' line ({ThemePalette.Hex(ink)}) on {ThemePalette.Hex(ground)} measures {ratio:F2}:1, "
                + $"short of the {WcagContrast.AaNormalText}:1 floor an 11.5px line needs.");
        }
        finally
        {
            window.Close();
        }
    });

    private static Color _GroundBehind(Visual visual) =>
        visual.GetVisualAncestors().OfType<Border>()
            .Select(border => border.Background?.ToImmutable())
            .OfType<ImmutableSolidColorBrush>()
            .Select(brush => brush.Color)
            .FirstOrDefault(colour => colour.A > 0, Colors.Black);

    private static IReadOnlyList<string> _Texts(string scene)
    {
        var window = Screenshotter.ShowScene(scene);
        try
        {
            window.UpdateLayout();

            return [.. window.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text ?? string.Empty)];
        }
        finally
        {
            window.Close();
        }
    }
}
