using Cockpit.Core.ModelIntelligence;

namespace Cockpit.Core.Tests.Sessions;

public sealed class ImageInputTests
{
    [Theory]
    [InlineData(ImageInputMode.On, true, false, false, true, "manual On")]
    [InlineData(ImageInputMode.Off, true, true, true, false, "manual Off")]
    [InlineData(ImageInputMode.Auto, true, false, true, false, "service API")]
    [InlineData(ImageInputMode.Auto, true, true, false, true, "service API")]
    [InlineData(ImageInputMode.Auto, true, null, true, true, "OpenRouter")]
    [InlineData(ImageInputMode.Auto, true, null, false, false, "OpenRouter")]
    [InlineData(ImageInputMode.Auto, true, null, null, false, "unknown")]
    [InlineData(ImageInputMode.On, false, true, true, false, "driver has no image transport")]
    public void Image_input_obeys_precedence_and_transport(ImageInputMode mode, bool transport, bool? service, bool? catalog, bool expected, string source)
    {
        Assert.Equal(new ImageInputDecision(expected, source), ImageInput.Resolve(mode, transport, service, catalog));
    }
}
