using Avalonia;
using Cockpit.App.Services;
using Cockpit.Core.Rendering;

namespace Cockpit.Core.Tests.Services;

/// <summary>
/// The AC-57 render-backend probe's env→modes mapping. Pure, so it is exercised without an Avalonia app or a
/// Mac; every recognised backend keeps Software as the final fallback so a machine that cannot create the
/// requested surface still starts.
/// </summary>
public class RenderBackendOverrideTests
{
    /// <summary>
    /// Every recognised backend keeps Software as the final fallback, so a machine that cannot create the
    /// requested surface still starts — and the name is trimmed and case-folded, because it comes off an
    /// environment variable somebody typed.
    /// </summary>
    [Theory]
    [MemberData(nameof(RecognisedBackends))]
    public void Parse_PrefersTheNamedBackend_AndFallsBackToSoftware(string value, object expectedModes, string label)
    {
        var selection = RenderBackendOverride.Parse(value);

        Assert.NotNull(selection);
        Assert.Equal(expectedModes, selection!.Modes);
        Assert.Equal(label, selection.Label);
    }

    public static IEnumerable<object[]> RecognisedBackends() =>
    [
        ["opengl", new[] { AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software }, "OpenGL"],
        ["OpenGL", new[] { AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software }, "OpenGL"],
        ["  gl  ", new[] { AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software }, "OpenGL"],
        ["metal", new[] { AvaloniaNativeRenderingMode.Metal, AvaloniaNativeRenderingMode.Software }, "Metal"],
        ["software", new[] { AvaloniaNativeRenderingMode.Software }, "Software"],
    ];

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("vulkan")]
    [InlineData("metal2")]
    public void Parse_UnknownOrEmpty_IsNoOverride(string? value) =>
        Assert.Null(RenderBackendOverride.Parse(value));

    // AC-67: the Options choice maps through the same modes as the env var.
    [Fact]
    public void FromChoice_Auto_IsNoOverride() =>
        Assert.Null(RenderBackendOverride.FromChoice(RenderBackendChoice.Auto));

    [Theory]
    [MemberData(nameof(ChosenBackends))]
    public void FromChoice_PrefersTheChosenBackend_AndFallsBackToSoftware(
        RenderBackendChoice choice, object expectedModes, string label)
    {
        var selection = RenderBackendOverride.FromChoice(choice);

        Assert.NotNull(selection);
        Assert.Equal(expectedModes, selection!.Modes);
        Assert.Equal(label, selection.Label);
    }

    public static IEnumerable<object[]> ChosenBackends() =>
    [
        [RenderBackendChoice.OpenGl, new[] { AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software }, "OpenGL"],
        [RenderBackendChoice.Metal, new[] { AvaloniaNativeRenderingMode.Metal, AvaloniaNativeRenderingMode.Software }, "Metal"],
    ];
}
