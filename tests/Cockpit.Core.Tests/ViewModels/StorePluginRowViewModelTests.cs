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
        string latestVersion = "1.0.0",
        int? abstractionsVersion = 1,
        string? minHostVersion = "1.0.0",
        string? logoAsset = null) => new(
        Id: "github-issues",
        Name: name,
        Description: "d",
        Author: "me",
        LatestVersion: latestVersion,
        Versions: [new PluginStoreVersion(latestVersion, "github-issues/1.0.0.zip", abstractionsVersion, minHostVersion, "sha", null)],
        Category: category,
        Icon: icon,
        Homepage: homepage,
        Repository: repository,
        Featured: featured,
        Published: published,
        LogoAsset: logoAsset);

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

    // AC-553 option A: entry.LogoAsset carries either a vendor CDN URL (the provider trio) or a bundled file
    // name (everyone else) — the same field, told apart by whether it parses as an absolute http(s) URI.
    [Theory]
    [InlineData("https://claude.ai/favicon.svg", true)]
    [InlineData("http://example.com/logo.svg", true)]
    [InlineData("depot.svg", false)]
    [InlineData(null, false)]
    [InlineData("ftp://example.com/logo.svg", false)]
    public void IsRemoteLogoAsset_TrueOnlyForAnHttpOrHttpsUrl(string? logoAsset, bool expected)
    {
        var row = new StorePluginRowViewModel(_Entry(logoAsset: logoAsset), PluginStoreConfig.Remote("url"), null);

        Assert.Equal(expected, row.IsRemoteLogoAsset);
    }

    [Fact]
    public void RemoteLogoAsset_NeverResolvesAsABundledLocalAsset()
    {
        var row = new StorePluginRowViewModel(_Entry(logoAsset: "https://claude.ai/favicon.svg"), PluginStoreConfig.Remote("url"), null);

        Assert.Null(row.LogoAssetUri);
        Assert.False(row.HasLogoAsset);
    }

    [Fact]
    public void ShowsIconGlyph_WhileARemoteLogoIsDeclaredButNotYetLoaded_FallsBackToTheGlyph()
    {
        // Mirrors the real gap between a row appearing (AC-553's IsRemoteLogoAsset check) and
        // PluginManagerViewModel's async fetch landing RemoteLogo — a plugin must never render a blank tile
        // in between, or if the fetch never succeeds at all.
        var row = new StorePluginRowViewModel(_Entry(icon: "🌙", logoAsset: "https://claude.ai/favicon.svg"), PluginStoreConfig.Remote("url"), null);

        Assert.False(row.HasRemoteLogo);
        Assert.True(row.ShowsIconGlyph);
        Assert.False(row.ShowsMonogram);
    }

    // The "once it loads" half — RemoteLogo actually flips the flags — needs a real decoded Bitmap, which needs
    // Avalonia's headless platform running; see StorePluginRowViewModelRemoteLogoTests in Cockpit.App.ViewTests.

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

    // --- Compatibility (AC-181): the store stays a catalogue that shows everything, but a plugin this host
    // cannot run is visibly marked and its install action disabled — never wegfiltered, never a button that
    // clicks and then fails. -------------------------------------------------------------------------------

    [Fact]
    public void IsIncompatible_AbstractionsMajorMismatch_IsFlagged()
    {
        var row = new StorePluginRowViewModel(
            _Entry(abstractionsVersion: 2), PluginStoreConfig.Remote("url"), installedVersion: null,
            hostAbstractionsMajor: 1, hostVersion: new Version(1, 5, 0));

        Assert.True(row.IsIncompatible);
        Assert.Contains("contract version 2", row.IncompatibilityReason);
        Assert.Contains("provides 1", row.IncompatibilityReason);
        Assert.False(row.CanTakePrimaryAction);
        Assert.Equal("Incompatible", row.PrimaryActionLabel);
        Assert.True(row.HasStateBadge);
        Assert.Equal("Incompatible", row.StateBadgeText);
        Assert.Equal("CockpitStatusErrorBrush", row.StateBadgeBrushKey);
    }

    [Fact]
    public void IsIncompatible_HostTooOldForMinHostVersion_IsFlagged()
    {
        var row = new StorePluginRowViewModel(
            _Entry(minHostVersion: "2.0.0"), PluginStoreConfig.Remote("url"), installedVersion: null,
            hostAbstractionsMajor: 1, hostVersion: new Version(1, 5, 0));

        Assert.True(row.IsIncompatible);
        Assert.Contains("2.0.0", row.IncompatibilityReason);
        Assert.False(row.CanTakePrimaryAction);
    }

    // Mutation guard: the exact boundary, not just "some too-new version is flagged" — a `<` mistakenly written
    // as `<=` (or vice versa) in the shared gate would flip exactly this case.
    [Fact]
    public void IsIncompatible_HostExactlyMeetsMinHostVersion_IsNotFlagged()
    {
        var row = new StorePluginRowViewModel(
            _Entry(minHostVersion: "1.5.0"), PluginStoreConfig.Remote("url"), installedVersion: null,
            hostAbstractionsMajor: 1, hostVersion: new Version(1, 5, 0));

        Assert.False(row.IsIncompatible);
        Assert.Null(row.IncompatibilityReason);
        Assert.True(row.CanTakePrimaryAction);
        Assert.Equal("Install", row.PrimaryActionLabel);
    }

    [Fact]
    public void IsIncompatible_BeforeHostReachesOnePointZero_ADeclaredOnePointZeroRequirement_IsNotFlagged()
    {
        // Mirrors PluginLoadPolicy's own exemption for the stale 1.0.0 template-default value — the browse badge
        // can never disagree with what an actual install attempt on this same host would do.
        var row = new StorePluginRowViewModel(
            _Entry(minHostVersion: "1.0.0"), PluginStoreConfig.Remote("url"), installedVersion: null,
            hostAbstractionsMajor: 1, hostVersion: new Version(0, 13, 0));

        Assert.False(row.IsIncompatible);
    }

    // AC-181 review: an honest sub-1.0 minHostVersion is enforced against a 0.x host exactly as against a 1.x
    // one — only the stale 1.0.0 template default is exempt pre-1.0.
    [Fact]
    public void IsIncompatible_HonestSubOnePointZeroMinHostVersion_IsFlagged_EvenOnASubOnePointZeroHost()
    {
        var row = new StorePluginRowViewModel(
            _Entry(minHostVersion: "0.14.0"), PluginStoreConfig.Remote("url"), installedVersion: null,
            hostAbstractionsMajor: 1, hostVersion: new Version(0, 13, 0));

        Assert.True(row.IsIncompatible);
        Assert.Contains("0.14.0", row.IncompatibilityReason);
    }

    // --- Installed but the update is not (AC-181 review): an already-installed, working plugin must never be
    // mislabelled "Incompatible" over a newer version it merely cannot take — only the offer, not the plugin
    // itself, is out of reach. ------------------------------------------------------------------------------

    [Fact]
    public void InstalledPlugin_WhoseOnlyNewerVersionIsIncompatible_StaysLabelledInstalled_NotIncompatible()
    {
        var row = new StorePluginRowViewModel(
            _Entry(latestVersion: "2.0.0", minHostVersion: "2.0.0"), PluginStoreConfig.Remote("url"), installedVersion: "1.0.0",
            hostAbstractionsMajor: 1, hostVersion: new Version(1, 5, 0));

        Assert.False(row.IsIncompatible); // the installed copy runs fine — only the update is out of reach
        Assert.False(row.CanUpdate);
        Assert.False(row.CanTakePrimaryAction);
        Assert.Equal("Installed", row.PrimaryActionLabel);
        Assert.Equal("Installed", row.StateBadgeText);
        Assert.Equal("CockpitStatusDoneBrush", row.StateBadgeBrushKey);
        Assert.Null(row.StateBadgeTooltip); // the tooltip belongs to the red Incompatible state, not a plain Installed badge
        // the status line, not the badge, carries the reason an update can't be taken
        Assert.Contains("v1.0.0", row.StatusText);
        Assert.Contains("v2.0.0", row.StatusText);
        Assert.Contains("Requires", row.StatusText);
    }

    [Fact]
    public void IsIncompatible_NoVersionsDeclared_IsNotFlagged()
    {
        var row = new StorePluginRowViewModel(
            _Entry(abstractionsVersion: null, minHostVersion: null), PluginStoreConfig.Remote("url"), installedVersion: null,
            hostAbstractionsMajor: 1, hostVersion: new Version(1, 5, 0));

        Assert.False(row.IsIncompatible);
        Assert.Null(row.IncompatibilityReason);
    }

    [Fact]
    public void StatusText_WhenIncompatible_ShowsTheReason_InsteadOfTheInstallState()
    {
        var row = new StorePluginRowViewModel(
            _Entry(abstractionsVersion: 2), PluginStoreConfig.Remote("url"), installedVersion: null,
            hostAbstractionsMajor: 1, hostVersion: new Version(1, 5, 0));

        Assert.Equal(row.IncompatibilityReason, row.StatusText);
    }
}
