using Cockpit.App.ViewModels;
using Cockpit.Core.Plugins;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The store dialog's (#62) presentation projections over a catalogue entry: category fallback,
/// icon/monogram, homepage/repository visibility, Featured, and the parsed Published date. All pure
/// derivations over <see cref="PluginStoreEntry"/> — no install/consent behaviour here.
/// </summary>
public class StorePluginRowViewModelTests
{
    private static PluginStoreEntry _Entry(
        string name = "GitHub Issues",
        string? category = null,
        string? icon = null,
        string? homepage = null,
        string? repository = null,
        bool featured = false,
        string? published = null,
        string latestVersion = "1.0.0") => new(
        Id: "github-issues",
        Name: name,
        Description: "d",
        Author: "me",
        LatestVersion: latestVersion,
        Versions: [new PluginStoreVersion(latestVersion, "github-issues/1.0.0.zip", 1, "1.0.0", "sha", null)],
        Category: category,
        Icon: icon,
        Homepage: homepage,
        Repository: repository,
        Featured: featured,
        Published: published);

    [Fact]
    public void Category_WhenEntryHasNone_FallsBackToOther()
    {
        var row = new StorePluginRowViewModel(_Entry(category: null), PluginStoreConfig.Remote("url"),null);

        Assert.Equal(StorePluginRowViewModel.OtherCategory, row.Category);
        Assert.False(row.HasCategory);
    }

    [Fact]
    public void Category_WhenEntryHasOne_IsUsedAsIs()
    {
        var row = new StorePluginRowViewModel(_Entry(category: "Issue trackers"), PluginStoreConfig.Remote("url"),null);

        Assert.Equal("Issue trackers", row.Category);
        Assert.True(row.HasCategory);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Category_TreatsBlankAsNoCategory(string? category)
    {
        var row = new StorePluginRowViewModel(_Entry(category: category!), PluginStoreConfig.Remote("url"),null);

        Assert.Equal(StorePluginRowViewModel.OtherCategory, row.Category);
        Assert.False(row.HasCategory);
    }

    [Fact]
    public void IconGlyphOrNull_WhenEntryHasAnIcon_ReturnsIt()
    {
        var row = new StorePluginRowViewModel(_Entry(icon: "🐛"), PluginStoreConfig.Remote("url"),null);

        Assert.Equal("🐛", row.IconGlyphOrNull);
    }

    [Fact]
    public void IconGlyphOrNull_WhenEntryHasNone_IsNull_AndMonogramFallsBackToFirstLetter()
    {
        var row = new StorePluginRowViewModel(_Entry(name: "gemini provider", icon: null), PluginStoreConfig.Remote("url"),null);

        Assert.Null(row.IconGlyphOrNull);
        Assert.Equal("G", row.MonogramLetter);
    }

    [Fact]
    public void HasHomepageAndRepository_ReflectWhetherTheFieldsAreSet()
    {
        var withLinks = new StorePluginRowViewModel(_Entry(homepage: "https://x", repository: "https://y"), PluginStoreConfig.Remote("url"),null);
        var withoutLinks = new StorePluginRowViewModel(_Entry(), PluginStoreConfig.Remote("url"),null);

        Assert.True(withLinks.HasHomepage);
        Assert.True(withLinks.HasRepository);
        Assert.False(withoutLinks.HasHomepage);
        Assert.False(withoutLinks.HasRepository);
    }

    [Fact]
    public void IsFeatured_ReflectsTheEntryFlag()
    {
        Assert.True(new StorePluginRowViewModel(_Entry(featured: true), PluginStoreConfig.Remote("url"),null).IsFeatured);
        Assert.False(new StorePluginRowViewModel(_Entry(featured: false), PluginStoreConfig.Remote("url"),null).IsFeatured);
    }

    [Fact]
    public void PublishedDate_ParsesAValidIsoDate()
    {
        var row = new StorePluginRowViewModel(_Entry(published: "2026-05-12"), PluginStoreConfig.Remote("url"),null);

        Assert.Equal(new DateOnly(2026, 5, 12), row.PublishedDate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-date")]
    public void PublishedDate_WhenMissingOrInvalid_IsNull_NeverThrows(string? published)
    {
        var row = new StorePluginRowViewModel(_Entry(published: published), PluginStoreConfig.Remote("url"),null);

        Assert.Null(row.PublishedDate);
    }

    [Fact]
    public void PrimaryActionLabel_NotInstalled_IsInstall()
    {
        var row = new StorePluginRowViewModel(_Entry(), PluginStoreConfig.Remote("url"),null);

        Assert.Equal("Install", row.PrimaryActionLabel);
        Assert.True(row.CanTakePrimaryAction);
    }

    [Fact]
    public void PrimaryActionLabel_InstalledWithNewerStoreVersion_IsUpdate()
    {
        var row = new StorePluginRowViewModel(_Entry(latestVersion: "2.0.0"), PluginStoreConfig.Remote("url"),"1.0.0");

        Assert.Equal("Update", row.PrimaryActionLabel);
        Assert.True(row.CanTakePrimaryAction);
    }

    [Fact]
    public void PrimaryActionLabel_InstalledUpToDate_IsDisabledBadge()
    {
        var row = new StorePluginRowViewModel(_Entry(latestVersion: "1.0.0"), PluginStoreConfig.Remote("url"),"1.0.0");

        Assert.Equal("Installed", row.PrimaryActionLabel);
        Assert.False(row.CanTakePrimaryAction);
    }
}
