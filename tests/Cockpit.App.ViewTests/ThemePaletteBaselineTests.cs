using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Cockpit.App.Theming;
using Cockpit.TestSupport;

namespace Cockpit.App.ViewTests;

// AC-169: records the after, since there is no before left to diff against; duplicate files stay on purpose (AC-414).
[Collection("avalonia")]
public class ThemePaletteBaselineTests
{
    public static IEnumerable<object[]> Scenes =>
        from scene in Screenshotter.SceneNames from variant in ThemeVariants.Names select new object[] { scene, variant };

    private static string BaselineDirectory =>
        Path.Combine(RepositoryPaths.Root, "tests", "Cockpit.App.ViewTests", "Baselines");

    [Theory]
    [MemberData(nameof(Scenes))]
    public void AScene_PaintsNothingItsBaselineDoesNotAccountFor(string scene, string variant)
    {
        var painted = HeadlessAvalonia.Run(() => ThemeVariants.Under(variant, () => _Painted(scene)));

        ThemePaletteBaseline.Verify(ThemePaletteBaseline.PathFor(BaselineDirectory, scene, variant), painted);
    }

    // AC-414: a removed scene takes its test case with it and leaves its file green forever, so this walks the other direction.
    [Fact]
    public void EveryBaseline_BelongsToASceneThatStillExists() =>
        ThemePaletteBaseline.VerifyNoOrphans(BaselineDirectory, Screenshotter.SceneNames);

    // AC-337: a tree never shown resolves resources but not selector styles, so a render looks plausible and is another program.
    [Fact]
    public void TheHarness_ShowsItsWindow_SoTheThemesSelectorsHaveRun() => HeadlessAvalonia.Run(() =>
    {
        var primary = ThemeTokens.Colour("CockpitTextPrimaryColor");

        Assert.Contains(ThemePalette.Hex(primary), _Painted("options"), StringComparison.Ordinal);
    });

    [Fact]
    public void ThemeBrush_ResolvesTheApplicationsActualThemeVariant() => HeadlessAvalonia.Run(() =>
    {
        var app = Application.Current!;
        var resources = app.Resources;
        var variant = app.RequestedThemeVariant;
        var expected = new SolidColorBrush(Colors.Red);

        try
        {
            app.Resources = new ResourceDictionary
            {
                ThemeDictionaries =
                {
                    [ThemeVariant.Dark] = new ResourceDictionary { ["ThemeBrushVariantTest"] = expected },
                },
            };
            app.RequestedThemeVariant = ThemeVariant.Dark;

            Assert.Same(expected, ThemeBrush.Resolve("ThemeBrushVariantTest", "#000000"));
        }
        finally
        {
            app.Resources = resources;
            app.RequestedThemeVariant = variant;
        }
    });

    private static string _Painted(string scene)
    {
        var window = Screenshotter.ShowScene(scene);
        try
        {
            window.UpdateLayout();
            return ThemePalette.Describe(window);
        }
        finally
        {
            window.Close();
        }
    }
}
