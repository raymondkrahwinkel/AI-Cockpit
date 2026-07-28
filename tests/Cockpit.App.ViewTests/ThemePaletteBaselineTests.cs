using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.TestSupport;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The recorded state of the repaint (AC-169). Every scene the screenshot harness can render is rendered here and
/// held against a file, so a screen that starts painting a colour no theme token accounts for shows up as a diff on
/// something reviewable rather than on nobody's screen.
/// </summary>
/// <remarks>
/// <para>
/// The epic this closes had no finish line of its own — that is what the ticket says it is for. What a baseline can
/// still be, now that the repaint is merged, is an anchor forwards: there is no "before" left to diff against, so
/// this records the after and holds the next change to it.
/// </para>
/// <para>
/// The set of scenes is not written out here. It is read from <see cref="Screenshotter.SceneNames"/>, the table the
/// app itself builds from, because a hand-written list of screens carries the blind spot of whoever wrote it — the
/// same mistake, one level up, as a token count that only measures the places already doing it right.
/// </para>
/// </remarks>
[Collection("avalonia")]
public class ThemePaletteBaselineTests
{
    public static TheoryData<string> Scenes => [.. Screenshotter.SceneNames];

    [Theory]
    [MemberData(nameof(Scenes))]
    public void AScene_PaintsNothingItsBaselineDoesNotAccountFor(string scene)
    {
        var painted = HeadlessAvalonia.Run(() => _Painted(scene));

        ThemePaletteBaseline.Verify(
            Path.Combine(RepositoryPaths.Root, "tests", "Cockpit.App.ViewTests", "Baselines", $"{scene}.palette.txt"),
            painted);
    }

    /// <summary>
    /// Proves the harness is honest before any baseline built on it is believed. Avalonia applies
    /// <c>Application.Styles</c> only once a control reaches a styling root, so a tree that is measured but never
    /// shown still resolves its resource lookups — the fills come out right — while every selector-driven colour
    /// silently falls back to Fluent's. That shipped once already (AC-337): the render looked plausible and was of
    /// a different program. The theme's text colour arrives through a selector, so it is exactly what goes missing.
    /// </summary>
    [Fact]
    public void TheHarness_ShowsItsWindow_SoTheThemesSelectorsHaveRun() => HeadlessAvalonia.Run(() =>
    {
        var primary = (Color)(Application.Current?.FindResource("CockpitTextPrimaryColor")
            ?? throw new InvalidOperationException("The theme has no CockpitTextPrimaryColor."));

        Assert.Contains(ThemePalette.Hex(primary), _Painted("options"), StringComparison.Ordinal);
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
