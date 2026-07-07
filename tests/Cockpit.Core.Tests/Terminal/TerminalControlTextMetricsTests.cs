using Avalonia;
using Avalonia.Headless;
using FluentAssertions;
using Cockpit.Terminal;

namespace Cockpit.Core.Tests.Terminal;

/// <summary>
/// Guards the fix behind the Cockpit.Terminal fork (see <c>src/Cockpit.Terminal/NOTICE.md</c>): the
/// per-cell grid width driving the column count, caret and selection overlay must come from a
/// measured glyph advance of the actual font, not a guessed factor. A wrong cell width is what let the
/// last column (e.g. a status-bar "%") clip past the viewport, since the column count is
/// <c>(int)(viewportWidth / cellWidth)</c> — only correct if cellWidth reflects what the font really
/// renders.
/// </summary>
public sealed class TerminalControlTextMetricsTests
{
    static TerminalControlTextMetricsTests()
    {
        AppBuilder.Configure<Application>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .SetupWithoutStarting();
    }

    [Fact]
    public void CalculateTextSize_MeasuresTheFontsGlyphAdvance_NotAGuessedFactor()
    {
        const double fontSize = 13;
        var control = new TerminalControl { FontFamily = "monospace", FontSize = fontSize };

        var measuredWidth = control.ConsoleTextSizeForTests.Width;
        var guessedFactorWidth = fontSize * 0.6;

        measuredWidth.Should().BeGreaterThan(0);
        measuredWidth.Should().NotBe(guessedFactorWidth);
    }

    [Theory]
    [InlineData(13)]
    [InlineData(16)]
    [InlineData(20)]
    public void CalculateTextSize_ScalesWithFontSize_ConfirmingItIsAMeasurementNotAConstant(double fontSize)
    {
        var smaller = new TerminalControl { FontFamily = "monospace", FontSize = fontSize };
        var larger = new TerminalControl { FontFamily = "monospace", FontSize = fontSize * 2 };

        larger.ConsoleTextSizeForTests.Width.Should().BeGreaterThan(smaller.ConsoleTextSizeForTests.Width);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(517)]
    [InlineData(80)]
    public void Resize_NeverLetsTheColumnGridOverflowTheViewportWidth(double viewportWidth)
    {
        var control = new TerminalControl { FontFamily = "monospace", FontSize = 13 };
        var cellWidth = control.ConsoleTextSizeForTests.Width;
        var cellHeight = control.ConsoleTextSizeForTests.Height;

        var model = new TerminalControlModel();
        model.Resize(viewportWidth, 200, cellWidth, cellHeight);

        (model.Terminal.Cols * cellWidth).Should().BeLessThanOrEqualTo(viewportWidth);
    }
}
