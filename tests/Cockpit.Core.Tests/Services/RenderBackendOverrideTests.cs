using Avalonia;
using Cockpit.App.Services;
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
}
