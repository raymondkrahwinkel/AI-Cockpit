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
/// <para>
/// <b>Many of these files repeat another one byte for byte, and they stay (AC-414).</b> The update-badge and
/// toolbar-action scenes equal the plain session scene; the seven mark scenes equal one another, each carrying
/// one corner radius the resting surface has not and not a single colour it has not, because every mark's ink is
/// already on screen as a swatch before a mark is drawn; speaking equals listening and unavailable equals
/// transcribing. No count is written here on purpose — one was, it was wrong by three the day it was written,
/// and it would have gone stale again with the next scene. Count them if you need the number.
/// </para>
/// <para>
/// As evidence of what the app paints <em>today</em> those files say almost nothing new, and that is not what a
/// baseline is for. Each is the guard for its own scene, and the scenes are not the same window: the badge scene
/// renders the cockpit with an update count on it and the session scene renders it without, so a colour the badge
/// starts painting can land in the badge's file and nowhere else. A set that had dropped it for being a duplicate
/// would have nothing to fail. Dropping one would not even need a hand-written list — a flag on its row in the
/// scene table would do it and leave the derivation intact — but it would need someone to have decided, once,
/// that this scene is a duplicate, and nothing ever re-checks that. A scene stops being a duplicate the moment
/// its screen changes, which is the moment the baseline was wanted. What keeping them costs is review rather than
/// runtime: a token whose value moves has to be re-recorded in every file carrying it, and many of those diffs
/// will repeat another.
/// </para>
/// </remarks>
[Collection("avalonia")]
public class ThemePaletteBaselineTests
{
    public static TheoryData<string> Scenes => [.. Screenshotter.SceneNames];

    private static string BaselineDirectory =>
        Path.Combine(RepositoryPaths.Root, "tests", "Cockpit.App.ViewTests", "Baselines");

    [Theory]
    [MemberData(nameof(Scenes))]
    public void AScene_PaintsNothingItsBaselineDoesNotAccountFor(string scene)
    {
        var painted = HeadlessAvalonia.Run(() => _Painted(scene));

        ThemePaletteBaseline.Verify(ThemePaletteBaseline.PathFor(BaselineDirectory, scene), painted);
    }

    /// <summary>
    /// The other direction (AC-414): the theory above walks the scenes and can therefore only miss in one way —
    /// a scene that goes away takes its test case with it and leaves its file, green forever because nothing reads
    /// it any more. Removing a scene is precisely the change that makes this suite look healthier.
    /// </summary>
    [Fact]
    public void EveryBaseline_BelongsToASceneThatStillExists() =>
        ThemePaletteBaseline.VerifyNoOrphans(BaselineDirectory, Screenshotter.SceneNames);

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
