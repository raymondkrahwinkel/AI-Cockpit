using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.TestSupport;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The recorded state of the repaint (AC-169). Every scene the screenshot harness can render is rendered here and
/// the colours it paints are held against a file, so a colour that appears, disappears or changes token shows up
/// as a diff on a reviewable text file rather than on nobody's screen.
/// </summary>
/// <remarks>
/// <para>
/// The epic that this closes had no finish line of its own — that is what the ticket says it is for. What a
/// baseline can still be, now that <c>[a]</c>–<c>[d]</c> are merged, is an anchor forwards: there is no "before"
/// left to diff against, so this records the "after" and holds the next change to it.
/// </para>
/// <para>
/// The set of scenes is not written out here. It is read from <see cref="Screenshotter.SceneNames"/>, the table
/// the app itself builds from, because a hand-written list of screens carries the blind spot of whoever wrote it
/// — which is the same mistake, one level up, as the token count in <c>[d]</c> that measured only the places
/// already doing it right.
/// </para>
/// <para>
/// To re-record after an intended change: run with <c>COCKPIT_UPDATE_THEME_BASELINES=1</c>, review the diff, then
/// run again without it. The rewriting run still fails — a run that rewrites the thing it is checking must never
/// be able to come out green.
/// </para>
/// </remarks>
[Collection("avalonia")]
public class ThemePaletteBaselineTests
{
    private const string RewriteVariable = "COCKPIT_UPDATE_THEME_BASELINES";

    public static TheoryData<string> Scenes => [.. Screenshotter.SceneNames];

    [Theory]
    [MemberData(nameof(Scenes))]
    public void AScene_PaintsTheColoursItsBaselineRecords(string scene)
    {
        var painted = HeadlessAvalonia.Run(() => _Painted(scene));

        var baseline = Path.Combine(RepositoryPaths.Root, "tests", "Cockpit.App.ViewTests", "Baselines", $"{scene}.palette.txt");
        var recorded = File.Exists(baseline) ? _Normalised(File.ReadAllText(baseline)) : null;

        if (Environment.GetEnvironmentVariable(RewriteVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(baseline)!);
            File.WriteAllText(baseline, painted);
            Assert.Fail($"Rewrote the baseline for '{scene}'. Review the diff, then run again without {RewriteVariable}.");
        }

        Assert.True(recorded is not null,
            $"Scene '{scene}' has no baseline. Every scene the harness can render carries one, so a new screen "
            + $"cannot join the app unrecorded — run with {RewriteVariable}=1 to write it, then review it.");

        Assert.Equal(recorded, _Normalised(painted));
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

        var painted = _Painted("options");

        Assert.Contains($"#{primary.A:X2}{primary.R:X2}{primary.G:X2}{primary.B:X2}", painted, StringComparison.Ordinal);
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

    /// <summary>Line endings are the checkout's, not the palette's.</summary>
    private static string _Normalised(string report) => report.ReplaceLineEndings("\n");
}
