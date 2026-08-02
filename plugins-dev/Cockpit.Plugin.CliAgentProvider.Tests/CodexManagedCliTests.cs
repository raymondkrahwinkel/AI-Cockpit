using Cockpit.Plugins.Abstractions.ManagedCli;

namespace Cockpit.Plugin.CliAgentProvider.Tests;

// The Codex managed-CLI descriptor (AC-20): version parsing, the target-triple/asset-name mapping, and the plan
// built from a GitHub release. The fixture mirrors the real `api.github.com/repos/openai/codex` release shape
// (verified live against rust-v0.144.5), so these assert the provider-specific knowledge without a network.
public class CodexManagedCliTests
{
    private const string Release = """
        {
          "tag_name": "rust-v0.144.5",
          "assets": [
            { "name": "codex-x86_64-unknown-linux-musl.tar.gz", "browser_download_url": "https://github.com/openai/codex/releases/download/rust-v0.144.5/codex-x86_64-unknown-linux-musl.tar.gz", "digest": "sha256:1111aaaa" },
            { "name": "codex-aarch64-apple-darwin.tar.gz",      "browser_download_url": "https://github.com/openai/codex/releases/download/rust-v0.144.5/codex-aarch64-apple-darwin.tar.gz",      "digest": "sha256:2222bbbb" },
            { "name": "codex-x86_64-pc-windows-msvc.exe.tar.gz","browser_download_url": "https://github.com/openai/codex/releases/download/rust-v0.144.5/codex-x86_64-pc-windows-msvc.exe.tar.gz","digest": "sha256:3333cccc" }
          ]
        }
        """;

    [Theory]
    [InlineData("rust-v0.144.5", "0.144.5")]
    [InlineData("rust-v1.0.0", "1.0.0")]
    [InlineData("0.144.5", "0.144.5")]
    public void ParseVersion_StripsTheRustPrefix(string tag, string expected)
    {
        Assert.Equal(expected, CodexManagedCli.ParseVersion(tag));
    }

    [Theory]
    [InlineData("linux", "x64", false, "x86_64-unknown-linux-musl")]
    [InlineData("linux", "arm64", false, "aarch64-unknown-linux-musl")]
    [InlineData("darwin", "arm64", false, "aarch64-apple-darwin")]
    [InlineData("win32", "x64", false, "x86_64-pc-windows-msvc")]
    public void TargetTriple_MapsOsAndArch_AndIsAlwaysMuslOnLinux(string os, string arch, bool musl, string expected)
    {
        Assert.Equal(expected, CodexManagedCli.TargetTriple(new ManagedCliPlatform(os, arch, musl)));
    }

    [Theory]
    [InlineData("linux", "codex-x86_64-unknown-linux-musl.tar.gz")]
    [InlineData("win32", "codex-x86_64-pc-windows-msvc.exe.tar.gz")]
    public void AssetName_AddsExeOnlyOnWindows(string os, string expected)
    {
        Assert.Equal(expected, CodexManagedCli.AssetName(new ManagedCliPlatform(os, "x64", false)));
    }

    [Fact]
    public void BuildPlan_Linux_ExtractsUrlDigestAndEntry_AsTarGz()
    {
        var plan = CodexManagedCli.BuildPlan(new ManagedCliPlatform("linux", "x64", false), Release);

        Assert.Equal("https://github.com/openai/codex/releases/download/rust-v0.144.5/codex-x86_64-unknown-linux-musl.tar.gz", plan.Url);
        Assert.Equal("1111aaaa", plan.ExpectedSha256); // the "sha256:" prefix is stripped
        Assert.Equal(ManagedCliArchiveFormat.TarGz, plan.ArchiveFormat);
        Assert.Equal("codex-x86_64-unknown-linux-musl", plan.ExecutableEntryName);
        Assert.Equal("codex", plan.ExecutableFileName);
        Assert.True(plan.NeedsExecutableBit);
    }

    [Fact]
    public void BuildPlan_Windows_NamesTheExeEntryAndFile_AndSkipsTheExecutableBit()
    {
        var plan = CodexManagedCli.BuildPlan(new ManagedCliPlatform("win32", "x64", false), Release);

        Assert.Equal("https://github.com/openai/codex/releases/download/rust-v0.144.5/codex-x86_64-pc-windows-msvc.exe.tar.gz", plan.Url);
        Assert.Equal("3333cccc", plan.ExpectedSha256);
        Assert.Equal("codex-x86_64-pc-windows-msvc.exe", plan.ExecutableEntryName);
        Assert.Equal("codex.exe", plan.ExecutableFileName);
        Assert.False(plan.NeedsExecutableBit);
    }

    [Fact]
    public void BuildPlan_RejectsAnUntrustedDownloadUrl()
    {
        // A spoofed release JSON pointing the download off GitHub must be refused, even though content stays digest-bound.
        const string release = """
            { "tag_name": "rust-v0.144.5", "assets": [
              { "name": "codex-x86_64-unknown-linux-musl.tar.gz", "browser_download_url": "https://evil.example.com/codex.tar.gz", "digest": "sha256:1111aaaa" } ] }
            """;

        var act = () => CodexManagedCli.BuildPlan(new ManagedCliPlatform("linux", "x64", false), release);

        var ex = Assert.Throws<InvalidOperationException>(act);
        Assert.Contains("untrusted", ex.Message);
    }

    [Fact]
    public void BuildPlan_PlatformWithoutAnAsset_Throws()
    {
        // arm64 windows is not in the fixture — a missing asset must fail loudly, not silently pick the wrong one.
        var act = () => CodexManagedCli.BuildPlan(new ManagedCliPlatform("win32", "arm64", false), Release);

        var ex = Assert.Throws<InvalidOperationException>(act);
        Assert.Contains("aarch64-pc-windows-msvc", ex.Message);
    }
}
