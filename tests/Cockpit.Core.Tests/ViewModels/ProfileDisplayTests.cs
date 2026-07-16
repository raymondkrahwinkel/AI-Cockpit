using Cockpit.App.ViewModels;
using Cockpit.Core.Profiles;
using FluentAssertions;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// Formats a profile's picker label with its provider (#26/#45). A plugin-provider profile shows the specific
/// plugin's own display name when the caller resolved it (e.g. "Claude"), rather than the generic "Plugin"
/// placeholder the bare <see cref="SessionProvider.Plugin"/> enum resolves to.
/// </summary>
public class ProfileDisplayTests
{
    [Fact]
    public void PluginProfile_WithResolvedProviderName_ShowsThatName()
    {
        ProfileDisplay.Format("default", SessionProvider.Plugin, model: null, pluginProviderName: "Claude")
            .Should().Be("default (Claude)");
    }

    [Fact]
    public void PluginProfile_WithoutAResolvedName_FallsBackToTheGenericPluginLabel()
    {
        ProfileDisplay.Format("default", SessionProvider.Plugin, model: null)
            .Should().Be("default (Plugin)");
    }

    [Fact]
    public void LocalProfile_AppendsProviderAndModel()
    {
        ProfileDisplay.Format("local", SessionProvider.LmStudio, model: "qwen2.5")
            .Should().Be("local (LM Studio - qwen2.5)");
    }

    [Fact]
    public void LocalProfile_WithoutModel_ShowsProviderOnly()
    {
        ProfileDisplay.Format("local", SessionProvider.Ollama, model: null)
            .Should().Be("local (Ollama)");
    }
}
