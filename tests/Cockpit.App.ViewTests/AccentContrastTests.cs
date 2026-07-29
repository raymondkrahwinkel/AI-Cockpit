using Cockpit.TestSupport;

namespace Cockpit.App.ViewTests;

/// <summary>
/// WCAG AA for every accent stand that carries the on-accent text colour (AC-381). Button.Accent draws its label
/// with CockpitTextOnAccentColor directly on CockpitAccentColor/HoverColor/PressedColor depending on which state
/// the pointer is in, so all three fills have to clear AA on their own — a check that only covers the resting
/// state is exactly how the accent shipped at 3.68:1 in the first place (#3b82f6 against white).
/// </summary>
/// <remarks>
/// Generic on purpose: this reads the live token values through <see cref="RenderedScene.Token"/> rather than
/// pinning today's hex, so the next re-tint fails here first instead of shipping a fourth stand nobody measured.
/// </remarks>
[Collection("avalonia")]
public class AccentContrastTests
{
    [Theory]
    [InlineData("CockpitAccentColor")]
    [InlineData("CockpitAccentHoverColor")]
    [InlineData("CockpitAccentPressedColor")]
    public void EveryAccentStand_MeetsAaForTheTextItCarries(string accentToken) => HeadlessAvalonia.Run(() =>
    {
        var accent = RenderedScene.Token(accentToken);
        var textOnAccent = RenderedScene.Token("CockpitTextOnAccentColor");

        var ratio = WcagContrast.Ratio(accent, textOnAccent);

        Assert.True(ratio >= WcagContrast.AaNormalText,
            $"{accentToken} ({ThemePalette.Hex(accent)}) against CockpitTextOnAccentColor ({ThemePalette.Hex(textOnAccent)}) "
            + $"measures {ratio:F2}:1, short of the {WcagContrast.AaNormalText}:1 AA floor the 12.5px SemiBold "
            + "button label needs.");
    });
}
