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
    [Theory]
    [InlineData("opengl")]
    [InlineData("OpenGL")]
    [InlineData("  gl  ")]
    public void Parse_OpenGl_PrefersOpenGlThenSoftware(string value)
    {
        var selection = RenderBackendOverride.Parse(value);

        Assert.NotNull(selection);
        Assert.Equal(new[] { AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software }, selection!.Modes);
        Assert.Equal("OpenGL", selection.Label);
    }

    [Fact]
    public void Parse_Software_IsSoftwareOnly() =>
        Assert.Equal(new[] { AvaloniaNativeRenderingMode.Software }, RenderBackendOverride.Parse("software")!.Modes);

    [Fact]
    public void Parse_Metal_PrefersMetalThenSoftware() =>
        Assert.Equal(new[] { AvaloniaNativeRenderingMode.Metal, AvaloniaNativeRenderingMode.Software }, RenderBackendOverride.Parse("metal")!.Modes);

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

    [Fact]
    public void FromChoice_OpenGl_PrefersOpenGlThenSoftware()
    {
        var selection = RenderBackendOverride.FromChoice(RenderBackendChoice.OpenGl);

        Assert.NotNull(selection);
        Assert.Equal("OpenGL", selection!.Label);
        Assert.Equal(new[] { AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software }, selection.Modes);
    }

    [Fact]
    public void FromChoice_Metal_PrefersMetalThenSoftware() =>
        Assert.Equal(
            new[] { AvaloniaNativeRenderingMode.Metal, AvaloniaNativeRenderingMode.Software },
            RenderBackendOverride.FromChoice(RenderBackendChoice.Metal)!.Modes);
}
