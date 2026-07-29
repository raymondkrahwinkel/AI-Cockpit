using System.Reflection;
using Cockpit.App.ViewModels;

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

        Assert.Equal("Wispslate Cockpit", info.AppName);
        Assert.Equal("https://github.com/raymondkrahwinkel/AI-Cockpit", info.GitHubUrl);
        Assert.Equal("https://github.com/raymondkrahwinkel/AI-Cockpit/issues", info.IssuesUrl);
        Assert.Equal("https://github.com/raymondkrahwinkel/AI-Cockpit-Plugins", info.PluginStoreUrl);
        Assert.False(string.IsNullOrWhiteSpace(info.Description));
        Assert.Contains("Commons Clause", info.LicenseText);
    }

    [Fact]
    public void FromAssembly_ListsTheBuiltInLocalProviders_NotAnyProviderTheCoreNoLongerShipsWith()
    {
        var info = AboutInfo.FromAssembly(Assembly.GetExecutingAssembly());

        // The core ships only the local OpenAI-compatible providers now; Claude is a plugin (Fase 4), so it is not
        // hard-coded as a built-in here — it comes from the plugin registry like every other agent.
        Assert.Contains("Ollama", info.Providers);
        Assert.Contains("LM Studio", info.Providers);
        Assert.DoesNotContain("Claude", info.Providers);
    }

    [Fact]
    public void FromAssembly_WithNoProviderPluginsInstalled_NamesOnlyTheBuiltInOnes()
    {
        var info = AboutInfo.FromAssembly(Assembly.GetExecutingAssembly());

        // Naming Claude, Codex or Gemini on an install that has none of them would be advertising, not information.
        Assert.Equal("Ollama · LM Studio", info.Providers);
    }

    [Fact]
    public void FromAssembly_ListsTheProviderPluginsThatAreActuallyInstalled_IncludingClaudeAndCodex()
    {
        var info = AboutInfo.FromAssembly(Assembly.GetExecutingAssembly(), ["Claude", "Codex (CLI)"]);

        Assert.Equal("Ollama · LM Studio · Claude · Codex (CLI)", info.Providers);
    }

    [Fact]
    public void FromAssembly_BuildText_NamesThePluginContractAndRuntime()
    {
        var info = AboutInfo.FromAssembly(Assembly.GetExecutingAssembly());

        // The plugin contract major the host provides — the gate PluginLoadPolicy enforces — and the runtime, the
        // identifiers a bug report needs. Matches AbstractionsContract.Version so it moves with the actual gate.
        Assert.Contains($"Plugin API {Cockpit.Plugins.Abstractions.AbstractionsContract.Version}", info.BuildText);
        Assert.Contains("SDK ", info.BuildText);
        Assert.Contains(".NET", info.BuildText);
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
        Assert.Equal(expected, info.VersionText);
        Assert.DoesNotContain("+", info.VersionText);
    }
}
