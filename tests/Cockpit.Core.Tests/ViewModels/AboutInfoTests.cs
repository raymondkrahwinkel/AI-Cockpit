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
    public void FromAssembly_ListsEveryBuiltInProvider_SoTheAppDoesNotReadAsClaudeOnly()
    {
        var info = AboutInfo.FromAssembly(Assembly.GetExecutingAssembly());

        info.Providers.Should().Contain("Claude Code")
            .And.Contain("Ollama")
            .And.Contain("LM Studio");
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
