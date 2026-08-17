namespace Cockpit.Plugin.Diagram.Tests;

public class PinMessageTests
{
    [Fact]
    public void Compose_WithAnObjectLabel_IncludesTheSurfaceTitleAndTheLabel()
    {
        var text = PinMessage.Compose("Onboarding flow", 2, "Geweigerd", "krijgt de agent te horen waaróm?");

        Assert.Equal("📍 pin 2 · \"Onboarding flow\" · Geweigerd — krijgt de agent te horen waaróm?", text);
    }

    [Fact]
    public void Compose_WithoutAnObjectLabel_StillNamesTheSurface_JustNoObjectSegment()
    {
        var text = PinMessage.Compose("Onboarding flow", 1, objectLabel: null, "waarom staat dit hier?");

        Assert.Equal("📍 pin 1 · \"Onboarding flow\" — waarom staat dit hier?", text);
    }

    [Fact]
    public void Compose_WithABlankObjectLabel_TreatsItTheSameAsNoLabel()
    {
        var text = PinMessage.Compose("Bord", 3, "   ", "vraag");

        Assert.Equal("📍 pin 3 · \"Bord\" — vraag", text);
    }
}
