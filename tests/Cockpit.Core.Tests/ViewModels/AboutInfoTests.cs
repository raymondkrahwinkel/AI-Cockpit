using System.Reflection;
using Cockpit.App.ViewModels;
using FluentAssertions;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// <see cref="AboutInfo.FromAssembly"/> reads the running build's version from assembly metadata (#46)
/// rather than a hand-maintained string, and always fills in the app name, description and links.
/// </summary>
public class AboutInfoTests
{
    [Fact]
    public void FromAssembly_FillsAppNameAndLinks()
    {
        var info = AboutInfo.FromAssembly(Assembly.GetExecutingAssembly());

        info.AppName.Should().Be("AI-Cockpit");
        info.GitHubUrl.Should().Be("https://github.com/raymondkrahwinkel/AI-Cockpit");
        info.IssuesUrl.Should().Be("https://github.com/raymondkrahwinkel/AI-Cockpit/issues");
        info.PluginStoreUrl.Should().Be("https://github.com/raymondkrahwinkel/AI-Cockpit-Plugins");
        info.Description.Should().NotBeNullOrWhiteSpace();
        info.LicenseText.Should().Contain("Commons Clause");
    }

    [Fact]
    public void FromAssembly_ListsTheBuiltInLocalProviders_NotAnyProviderTheCoreNoLongerShipsWith()
    {
        var info = AboutInfo.FromAssembly(Assembly.GetExecutingAssembly());

        // The core ships only the local OpenAI-compatible providers now; Claude is a plugin (Fase 4), so it is not
        // hard-coded as a built-in here — it comes from the plugin registry like every other agent.
        info.Providers.Should().Contain("Ollama").And.Contain("LM Studio");
        info.Providers.Should().NotContain("Claude");
    }

    [Fact]
    public void FromAssembly_WithNoProviderPluginsInstalled_NamesOnlyTheBuiltInOnes()
    {
        var info = AboutInfo.FromAssembly(Assembly.GetExecutingAssembly());

        // Naming Claude, Codex or Gemini on an install that has none of them would be advertising, not information.
        info.Providers.Should().Be("Ollama · LM Studio");
    }

    [Fact]
    public void FromAssembly_ListsTheProviderPluginsThatAreActuallyInstalled_IncludingClaudeAndCodex()
    {
        var info = AboutInfo.FromAssembly(Assembly.GetExecutingAssembly(), ["Claude", "Codex (CLI)"]);

        info.Providers.Should().Be("Ollama · LM Studio · Claude · Codex (CLI)");
    }

    [Fact]
    public void FromAssembly_UsesTheInformationalVersion_WithoutItsBuildMetadata()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var expected = informational is null
            ? assembly.GetName().Version?.ToString()
            : informational.Split('+')[0];

        var info = AboutInfo.FromAssembly(assembly);

        // The SDK appends "+<full git sha>", which overflows the dialog's version line.
        info.VersionText.Should().Be(expected);
        info.VersionText.Should().NotContain("+");
    }
}
