using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.TestSupport;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The rules the light variant was derived by (AC-860), each held in both variants so the derivation stays a rule
/// and not a taste: a tint is an alpha echo of its base, the surface ladder reorders rather than mirrors, ink clears
/// the AA floor on the fill it is for, and the two places light is a style rule rather than a value really do
/// resolve differently. The baselines say what each screen paints; this says why those values are the ones.
/// </summary>
[Collection("avalonia")]
public class ThemeVariantTests
{
    public static IEnumerable<object[]> Variants => ThemeVariants.Names.Select(name => new object[] { name });

    // Every tint, and the base it echoes. Same RGB, only the alpha differs — a tint copied as a hex instead of
    // re-derived is how AC-406/AC-337 drifted, and a second variant doubles the places that can happen.
    private static readonly (string Tint, string Base)[] Echoes =
    [
        ("CockpitAccentSelectionColor", "CockpitAccentColor"),
        ("CockpitFocusHairlineColor", "CockpitAccentColor"),
        ("CockpitAccentRingTintColor", "CockpitAccentColor"),
        ("CockpitCategoryTintBlueColor", "CockpitAccentColor"),
        ("CockpitStatusBusyChipTintColor", "CockpitStatusBusyColor"),
        ("CockpitStatusBusyRingTintColor", "CockpitStatusBusyColor"),
        ("CockpitCategoryTintCyanColor", "CockpitStatusBusyColor"),
        ("CockpitStatusWaitingChipTintColor", "CockpitStatusWaitingColor"),
        ("CockpitStatusWaitingRingTintColor", "CockpitStatusWaitingColor"),
        ("CockpitStatusWaitingRowTintColor", "CockpitStatusWaitingColor"),
        ("CockpitCategoryTintAmberColor", "CockpitStatusWaitingColor"),
        ("CockpitStatusDoneChipTintColor", "CockpitStatusDoneColor"),
        ("CockpitStatusDoneRingTintColor", "CockpitStatusDoneColor"),
        ("CockpitCategoryTintGreenColor", "CockpitStatusDoneColor"),
        ("CockpitStatusErrorChipTintColor", "CockpitStatusErrorColor"),
        ("CockpitStatusErrorRingTintColor", "CockpitStatusErrorColor"),
        ("CockpitCategoryTintPurpleColor", "CockpitStatusBackgroundColor"),
    ];

    [Theory]
    [MemberData(nameof(Variants))]
    public void EveryTint_IsAnAlphaEchoOfItsBase(string variant) => HeadlessAvalonia.Run(() => ThemeVariants.Under(variant, () =>
    {
        var drifted = Echoes
            .Select(echo => (echo.Tint, Value: RenderedScene.Token(echo.Tint), Base: RenderedScene.Token(echo.Base)))
            .Where(echo => echo.Value.A == 255 || (echo.Value.R, echo.Value.G, echo.Value.B) != (echo.Base.R, echo.Base.G, echo.Base.B))
            .Select(echo => $"{echo.Tint} is {ThemePalette.Hex(echo.Value)}, its base is {ThemePalette.Hex(echo.Base)}")
            .ToList();

        Assert.True(drifted.Count == 0, $"In {variant}, tints that are not an alpha echo of their base:\n  {string.Join("\n  ", drifted)}");
    }));

    // The surface ladder by lightness. Four of five keep their direction; only the inset moves, because "raised"
    // cannot be lighter than a white panel — so a light theme that mirrored dark would be wrong on exactly one rung.
    [Theory]
    [InlineData("Dark", "CockpitSecondaryBgColor", "CockpitWindowBgColor", "CockpitChromeBgColor", "CockpitPanelBgColor", "CockpitInsetBgColor")]
    [InlineData("Light", "CockpitSecondaryBgColor", "CockpitInsetBgColor", "CockpitWindowBgColor", "CockpitChromeBgColor", "CockpitPanelBgColor")]
    public void TheSurfaceLadder_ReordersRatherThanMirrors(string variant, params string[] darkestToLightest) => HeadlessAvalonia.Run(() => ThemeVariants.Under(variant, () =>
    {
        var luminance = darkestToLightest.Select(token => (token, value: WcagContrast.RelativeLuminance(RenderedScene.Token(token)))).ToList();

        Assert.True(luminance.Zip(luminance.Skip(1)).All(pair => pair.First.value < pair.Second.value),
            $"In {variant} the surfaces sort as: {string.Join(" < ", luminance.OrderBy(entry => entry.value).Select(entry => entry.token))}");
    }));

    // Ink on the fill it is for, AA in both variants. Faint is not here: it clears no floor in either variant on
    // purpose (the third step of the text ladder), and the accent stands have AccentContrastTests of their own.
    // Dark's error fill is the one pre-existing short (4.31:1 since #61) — a change to the dark stand, not this ticket's.
    [Theory]
    [MemberData(nameof(Variants))]
    public void Ink_ClearsTheAaFloor_OnTheFillItIsFor(string variant) => HeadlessAvalonia.Run(() => ThemeVariants.Under(variant, () =>
    {
        (string Ink, string Fill)[] pairs =
        [
            ("CockpitTextOnStatusColor", "CockpitStatusBusyColor"),
            ("CockpitTextOnStatusColor", "CockpitStatusWaitingColor"),
            ("CockpitTextOnStatusColor", "CockpitStatusDoneColor"),
            ("CockpitTextOnStatusColor", "CockpitStatusBackgroundColor"),
            ("CockpitTextOnStatusColor", "CockpitStatusErrorColor"),
            ("CockpitTextPrimaryColor", "CockpitWindowBgColor"),
            ("CockpitTextPrimaryColor", "CockpitPanelBgColor"),
            ("CockpitTextPrimaryColor", "CockpitInsetBgColor"),
            ("CockpitTextPrimaryColor", "CockpitSecondaryBgColor"),
            ("CockpitTextSecondaryColor", "CockpitWindowBgColor"),
            ("CockpitTextSecondaryColor", "CockpitPanelBgColor"),
            ("CockpitTextSecondaryColor", "CockpitInsetBgColor"),
            ("CockpitTextSecondaryColor", "CockpitSecondaryBgColor"),
            ("CockpitIsolatedBadgeTextColor", "CockpitIsolatedBadgeBgColor"),
        ];
        var knownShort = variant == "Dark" ? ("CockpitTextOnStatusColor", "CockpitStatusErrorColor") : default;

        var shortfalls = pairs
            .Where(pair => pair != knownShort)
            .Select(pair => (pair.Ink, pair.Fill, Ratio: WcagContrast.Ratio(RenderedScene.Token(pair.Ink), RenderedScene.Token(pair.Fill))))
            .Where(pair => pair.Ratio < WcagContrast.AaNormalText)
            .Select(pair => $"{pair.Ink} on {pair.Fill}: {pair.Ratio:F2}:1")
            .ToList();

        Assert.True(shortfalls.Count == 0, $"In {variant}, ink short of {WcagContrast.AaNormalText}:1 on its own fill:\n  {string.Join("\n  ", shortfalls)}");
    }));

    // The two rules the dictionary cannot carry as a value: "needs you" shouts by tinted ink in dark and by a solid
    // fill in light (Raymond, 2026-09-10); a raised tag gets the full hairline in light, where it sits 1.13 from the
    // rail. Rendered rather than read off the dictionary — a role brush on the wrong alias is still a valid brush.
    [Theory]
    [InlineData("Dark", "CockpitStatusErrorChipTintColor", "CockpitTextPrimaryColor", "CockpitStatusErrorRingTintColor", "CockpitHairlineSoftColor")]
    [InlineData("Light", "CockpitStatusErrorColor", "CockpitTextOnStatusColor", "CockpitTextOnStatusColor", "CockpitHairlineColor")]
    public void TheLightOnlyStyleRules_ResolvePerVariant(string variant, string chipFill, string chipInk, string chipRing, string tagEdge) => HeadlessAvalonia.Run(() => ThemeVariants.Under(variant, () =>
    {
        var ring = new Border { Classes = { "assistantIndicatorRing", "small", "awaitingOperator" } };
        var label = new TextBlock { Classes = { "assistantIndicatorLabel" }, Text = "Needs you" };
        var chip = new Button { Classes = { "assistantIndicatorChip", "awaitingOperator" }, Content = new StackPanel { Children = { ring, label } } };
        var tag = new Border { Classes = { "tag" }, Child = new TextBlock { Text = "kind" } };
        using var scene = RenderedScene.Show(new StackPanel { Children = { chip, tag } });

        Assert.Equal(RenderedScene.Token(chipFill), ((ISolidColorBrush)chip.Background!).Color);
        Assert.Equal(RenderedScene.Token(chipInk), ((ISolidColorBrush)label.Foreground!).Color);
        Assert.Equal(RenderedScene.Token(chipRing), ((ISolidColorBrush)ring.Background!).Color);
        Assert.Equal(RenderedScene.Token(tagEdge), ((ISolidColorBrush)tag.BorderBrush!).Color);
    }));
}
