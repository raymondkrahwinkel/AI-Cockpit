using Cockpit.Plugins.Abstractions.ManagedCli;

namespace Cockpit.Plugin.CliAgentProvider.Tests;

// The Codex managed-CLI descriptor (AC-20, AC-1107): version parsing, asset-name mapping, and the plan built from
// a GitHub release, including the three sibling assets. The fixture mirrors the real release shape.
public class CodexManagedCliTests
{
    private const string Release = """
        {
          "tag_name": "rust-v0.149.1",
          "assets": [
            { "name": "codex-x86_64-unknown-linux-musl.tar.gz", "browser_download_url": "https://github.com/openai/codex/releases/download/rust-v0.149.1/codex-x86_64-unknown-linux-musl.tar.gz", "digest": "sha256:1111aaaa" },
            { "name": "codex-aarch64-apple-darwin.tar.gz",      "browser_download_url": "https://github.com/openai/codex/releases/download/rust-v0.149.1/codex-aarch64-apple-darwin.tar.gz",      "digest": "sha256:2222bbbb" },
            { "name": "codex-x86_64-pc-windows-msvc.exe.tar.gz","browser_download_url": "https://github.com/openai/codex/releases/download/rust-v0.149.1/codex-x86_64-pc-windows-msvc.exe.tar.gz","digest": "sha256:3333cccc" },
            { "name": "codex-code-mode-host-x86_64-unknown-linux-musl.tar.gz", "browser_download_url": "https://github.com/openai/codex/releases/download/rust-v0.149.1/codex-code-mode-host-x86_64-unknown-linux-musl.tar.gz", "digest": "sha256:4444dddd" },
            { "name": "codex-code-mode-host-x86_64-pc-windows-msvc.exe.tar.gz","browser_download_url": "https://github.com/openai/codex/releases/download/rust-v0.149.1/codex-code-mode-host-x86_64-pc-windows-msvc.exe.tar.gz","digest": "sha256:5555eeee" },
            { "name": "codex-command-runner-x86_64-pc-windows-msvc.exe.tar.gz","browser_download_url": "https://github.com/openai/codex/releases/download/rust-v0.149.1/codex-command-runner-x86_64-pc-windows-msvc.exe.tar.gz","digest": "sha256:6666ffff" },
            { "name": "codex-windows-sandbox-setup-x86_64-pc-windows-msvc.exe.tar.gz","browser_download_url": "https://github.com/openai/codex/releases/download/rust-v0.149.1/codex-windows-sandbox-setup-x86_64-pc-windows-msvc.exe.tar.gz","digest": "sha256:77778888" }
          ]
        }
        """;

    [Theory]
    [InlineData("linux", "x64", false, "x86_64-unknown-linux-musl")]
    [InlineData("linux", "arm64", false, "aarch64-unknown-linux-musl")]
    [InlineData("darwin", "arm64", false, "aarch64-apple-darwin")]
    [InlineData("win32", "x64", false, "x86_64-pc-windows-msvc")]
    public void TargetTriple_MapsOsAndArch_AndIsAlwaysMuslOnLinux(string os, string arch, bool musl, string expected)
    {
        Assert.Equal(expected, CodexManagedCli.TargetTriple(new ManagedCliPlatform(os, arch, musl)));
    }

    [Fact]
    public void BuildPlan_Linux_ExtractsUrlDigestAndEntry_AsTarGz()
    {
        var plan = CodexManagedCli.BuildPlan(new ManagedCliPlatform("linux", "x64", false), Release);

        Assert.Equal("https://github.com/openai/codex/releases/download/rust-v0.149.1/codex-x86_64-unknown-linux-musl.tar.gz", plan.Url);
        Assert.Equal("1111aaaa", plan.ExpectedSha256); // the "sha256:" prefix is stripped
        Assert.Equal(ManagedCliArchiveFormat.TarGz, plan.ArchiveFormat);
        Assert.Equal("codex-x86_64-unknown-linux-musl", plan.ExecutableEntryName);
        Assert.Equal("codex", plan.ExecutableFileName);
        Assert.True(plan.NeedsExecutableBit);

        // Linux gets only code-mode-host — command-runner/windows-sandbox-setup are Windows-only siblings.
        var host = Assert.Single(plan.AdditionalArtifacts);
        Assert.Equal("https://github.com/openai/codex/releases/download/rust-v0.149.1/codex-code-mode-host-x86_64-unknown-linux-musl.tar.gz", host.Url);
        Assert.Equal("4444dddd", host.ExpectedSha256);
        Assert.Equal("codex-code-mode-host", host.FileName);
        Assert.Equal(ManagedCliArchiveFormat.TarGz, host.ArchiveFormat);
        Assert.Equal("codex-code-mode-host-x86_64-unknown-linux-musl", host.ArchiveEntryName);
        Assert.True(host.NeedsExecutableBit);
    }

    [Fact]
    public void BuildPlan_RejectsAnUntrustedDownloadUrl()
    {
        // A spoofed release JSON pointing the download off GitHub must be refused, even though content stays digest-bound.
        const string release = """
            { "tag_name": "rust-v0.149.1", "assets": [
              { "name": "codex-x86_64-unknown-linux-musl.tar.gz", "browser_download_url": "https://evil.example.com/codex.tar.gz", "digest": "sha256:1111aaaa" } ] }
            """;

        var act = () => CodexManagedCli.BuildPlan(new ManagedCliPlatform("linux", "x64", false), release);

        var ex = Assert.Throws<InvalidOperationException>(act);
        Assert.Contains("untrusted", ex.Message);
    }
}
