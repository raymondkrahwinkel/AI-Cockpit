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
        info.PluginStoreUrl.Should().Be("https://github.com/raymondkrahwinkel/AI-Cockpit-Plugins");
        info.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void FromAssembly_UsesTheInformationalVersionWhenPresent()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var expected = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                        ?? assembly.GetName().Version?.ToString();

        var info = AboutInfo.FromAssembly(assembly);

        info.VersionText.Should().Be(expected);
    }
}
