using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-551 criterion 4: embedding Cascadia Mono must not cut off <see cref="Cockpit.App.Program.CockpitFontOptions"/>'s
/// emoji fallback. Cascadia Mono carries no emoji glyphs, same as the OS names it replaces, so a Claude transcript
/// emoji has always had to fall through to Segoe UI Emoji/Noto Color Emoji/Apple Color Emoji — the risk is that
/// giving <c>CockpitMonoFont</c> an avares:// source (a composite font key) short-circuits that fall-through instead
/// of just supplying the primary family, which is not visible in any test that only checks layout geometry.
/// </summary>
[Collection("avalonia")]
public class CockpitMonoFontEmojiFallbackTests
{
    [Fact]
    public void EmojiInTheMonoFontStillResolvesThroughTheFallbackChain() => HeadlessAvalonia.Run(() =>
    {
        // Measured against what this machine can do rather than against an absolute, because the claim is about the
        // composite font and not about the fonts installed: a bare container has no emoji font at all, and holding
        // it to the same answer as a desktop reports a defect on every run until nobody reads the result.
        var platformHasEmojiGlyphs = FontManager.Current.TryMatchCharacter(
            0x2705, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, null, null, out _);

        var monoFont = (FontFamily)Application.Current!.FindResource("CockpitMonoFont")!;

        using var layout = new TextLayout("✅", new Typeface(monoFont), 12.5);
        var run = Assert.IsType<ShapedTextRun>(layout.TextLines[0].TextRuns[0]);
        var glyphTypeface = run.GlyphRun.GlyphTypeface;

        if (!platformHasEmojiGlyphs)
        {
            // Nothing to fall through to, so the mono font is the honest answer — and pinning it keeps this from
            // passing vacuously where the interesting assertion cannot run.
            Assert.Equal("Cascadia Mono", glyphTypeface.FamilyName);
            return;
        }

        Assert.NotEqual("Cascadia Mono", glyphTypeface.FamilyName);
        Assert.True(glyphTypeface.CharacterToGlyphMap.TryGetGlyph(0x2705, out var glyph));
        Assert.NotEqual((ushort)0, glyph);
    });
}
