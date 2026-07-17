using Cockpit.Plugins.Abstractions.ManagedCli;
using FluentAssertions;

namespace Cockpit.Plugin.CliAgentProvider.Tests;

/// <summary>
/// The Codex managed-CLI descriptor (AC-20): version parsing, the target-triple/asset-name mapping, and the plan
/// built from a GitHub release. The fixture mirrors the real <c>api.github.com/repos/openai/codex</c> release shape
/// (verified live against rust-v0.144.5), so these assert the provider-specific knowledge without a network.
/// </summary>
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
        CodexManagedCli.ParseVersion(tag).Should().Be(expected);
    }

    [Theory]
    [InlineData("linux", "x64", false, "x86_64-unknown-linux-musl")]
    [InlineData("linux", "arm64", false, "aarch64-unknown-linux-musl")]
    [InlineData("darwin", "arm64", false, "aarch64-apple-darwin")]
    [InlineData("win32", "x64", false, "x86_64-pc-windows-msvc")]
    public void TargetTriple_MapsOsAndArch_AndIsAlwaysMuslOnLinux(string os, string arch, bool musl, string expected)
    {
        CodexManagedCli.TargetTriple(new ManagedCliPlatform(os, arch, musl)).Should().Be(expected);
    }

    [Theory]
    [InlineData("linux", "codex-x86_64-unknown-linux-musl.tar.gz")]
    [InlineData("win32", "codex-x86_64-pc-windows-msvc.exe.tar.gz")]
    public void AssetName_AddsExeOnlyOnWindows(string os, string expected)
    {
        CodexManagedCli.AssetName(new ManagedCliPlatform(os, "x64", false)).Should().Be(expected);
    }

    [Fact]
    public void BuildPlan_Linux_ExtractsUrlDigestAndEntry_AsTarGz()
    {
        var plan = CodexManagedCli.BuildPlan(new ManagedCliPlatform("linux", "x64", false), Release);

        plan.Url.Should().Be("https://github.com/openai/codex/releases/download/rust-v0.144.5/codex-x86_64-unknown-linux-musl.tar.gz");
        plan.ExpectedSha256.Should().Be("1111aaaa"); // the "sha256:" prefix is stripped
        plan.ArchiveFormat.Should().Be(ManagedCliArchiveFormat.TarGz);
        plan.ExecutableEntryName.Should().Be("codex-x86_64-unknown-linux-musl");
        plan.ExecutableFileName.Should().Be("codex");
        plan.NeedsExecutableBit.Should().BeTrue();
    }

    [Fact]
    public void BuildPlan_Windows_NamesTheExeEntryAndFile_AndSkipsTheExecutableBit()
    {
        var plan = CodexManagedCli.BuildPlan(new ManagedCliPlatform("win32", "x64", false), Release);

        plan.Url.Should().Be("https://github.com/openai/codex/releases/download/rust-v0.144.5/codex-x86_64-pc-windows-msvc.exe.tar.gz");
        plan.ExpectedSha256.Should().Be("3333cccc");
        plan.ExecutableEntryName.Should().Be("codex-x86_64-pc-windows-msvc.exe");
        plan.ExecutableFileName.Should().Be("codex.exe");
        plan.NeedsExecutableBit.Should().BeFalse();
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

        act.Should().Throw<InvalidOperationException>().WithMessage("*untrusted*");
    }

    [Fact]
    public void BuildPlan_PlatformWithoutAnAsset_Throws()
    {
        // arm64 windows is not in the fixture — a missing asset must fail loudly, not silently pick the wrong one.
        var act = () => CodexManagedCli.BuildPlan(new ManagedCliPlatform("win32", "arm64", false), Release);

        act.Should().Throw<InvalidOperationException>().WithMessage("*aarch64-pc-windows-msvc*");
    }
}
