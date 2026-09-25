namespace Cockpit.Plugin.Discord.Tests;

// AC-1394: the two-assembly layout AC-1390 pilots (GitStatusAssemblyLayoutTests), applied to this plugin — the
// backend part names no Avalonia, and the UI part never reaches into the backend.
public class DiscordAssemblyLayoutTests
{
    [Theory]
    [InlineData(typeof(DiscordChannelPlugin), "Avalonia")]
    [InlineData(typeof(UI.DiscordUi), "Cockpit.Plugin.Discord")]
    public void APart_DoesNotReferenceWhatItMustNot(Type entryType, string forbidden)
    {
        var references = entryType.Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => name == forbidden || name.StartsWith(forbidden + ".", StringComparison.Ordinal));

        Assert.Empty(references);
    }
}
