using Cockpit.Core.Configuration;
using Cockpit.TestSupport;

namespace Cockpit.App.ViewTests.Onboarding;

/// <summary>
/// The product name lives in one place (AC-509 criterion 5): <see cref="CockpitBrand.ProductName"/>. Every
/// onboarding screen resolves it from there instead of spelling it out, so a rename (AC-167 has not settled it
/// yet) is a one-line change rather than a search-and-replace across the wizard.
/// </summary>
public class ProductNameGuardTests
{
    [Fact]
    public void OnboardingScreens_NeverRepeatTheProductNameLiterally()
    {
        var onboardingDirectory = Path.Combine(RepositoryPaths.Root, "src", "Cockpit.App", "Views", "Onboarding");
        Assert.True(Directory.Exists(onboardingDirectory), $"expected {onboardingDirectory} to exist");

        var offenders = Directory.EnumerateFiles(onboardingDirectory, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains(CockpitBrand.ProductName, StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(RepositoryPaths.Root, path))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(offenders);
    }
}
