using Avalonia;
using Cockpit.App.Services;
using Cockpit.Core.Rendering;
using FluentAssertions;

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

        selection.Should().NotBeNull();
        selection!.Modes.Should().Equal(AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software);
        selection.Label.Should().Be("OpenGL");
    }

    [Fact]
    public void Parse_Software_IsSoftwareOnly() =>
        RenderBackendOverride.Parse("software")!.Modes.Should().Equal(AvaloniaNativeRenderingMode.Software);

    [Fact]
    public void Parse_Metal_PrefersMetalThenSoftware() =>
        RenderBackendOverride.Parse("metal")!.Modes
            .Should().Equal(AvaloniaNativeRenderingMode.Metal, AvaloniaNativeRenderingMode.Software);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("vulkan")]
    [InlineData("metal2")]
    public void Parse_UnknownOrEmpty_IsNoOverride(string? value) =>
        RenderBackendOverride.Parse(value).Should().BeNull();

    // AC-67: the Options choice maps through the same modes as the env var.
    [Fact]
    public void FromChoice_Auto_IsNoOverride() =>
        RenderBackendOverride.FromChoice(RenderBackendChoice.Auto).Should().BeNull();

    [Fact]
    public void FromChoice_OpenGl_PrefersOpenGlThenSoftware()
    {
        var selection = RenderBackendOverride.FromChoice(RenderBackendChoice.OpenGl);

        selection.Should().NotBeNull();
        selection!.Label.Should().Be("OpenGL");
        selection.Modes.Should().Equal(AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software);
    }

    [Fact]
    public void FromChoice_Metal_PrefersMetalThenSoftware() =>
        RenderBackendOverride.FromChoice(RenderBackendChoice.Metal)!.Modes
            .Should().Equal(AvaloniaNativeRenderingMode.Metal, AvaloniaNativeRenderingMode.Software);
}
