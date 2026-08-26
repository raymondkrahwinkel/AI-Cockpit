using Cockpit.Plugins.Abstractions.ManagedCli;

namespace Cockpit.Plugin.CliAgentProvider.Tests;

// The Codex managed-CLI descriptor (AC-20, AC-1107): version parsing, the target-triple/asset-name mapping, and the
// plan built from a GitHub release — including the three sibling assets (code-mode-host on every platform;
// command-runner and windows-sandbox-setup on Windows only). The fixture mirrors the real
// `api.github.com/repos/openai/codex` release shape (verified live against rust-v0.149.1), so these assert the
// provider-specific knowledge without a network.
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

    [Theory]
    [InlineData("code-mode-host", "linux", "codex-code-mode-host-x86_64-unknown-linux-musl.tar.gz")]
    [InlineData("code-mode-host", "win32", "codex-code-mode-host-x86_64-pc-windows-msvc.exe.tar.gz")]
    [InlineData("command-runner", "win32", "codex-command-runner-x86_64-pc-windows-msvc.exe.tar.gz")]
    [InlineData("windows-sandbox-setup", "win32", "codex-windows-sandbox-setup-x86_64-pc-windows-msvc.exe.tar.gz")]
    public void SiblingAssetName_AddsExeOnlyOnWindows(string label, string os, string expected)
    {
        Assert.Equal(expected, CodexManagedCli.SiblingAssetName(label, new ManagedCliPlatform(os, "x64", false)));
    }

    [Theory]
    [InlineData("code-mode-host", "linux", "codex-code-mode-host")]
    [InlineData("code-mode-host", "win32", "codex-code-mode-host.exe")]
    [InlineData("command-runner", "win32", "codex-command-runner.exe")]
    public void SiblingFileName_AddsExeOnlyOnWindows(string label, string os, string expected)
    {
        Assert.Equal(expected, CodexManagedCli.SiblingFileName(label, new ManagedCliPlatform(os, "x64", false)));
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
    public void BuildPlan_Windows_NamesTheExeEntryAndFile_AndSkipsTheExecutableBit()
    {
        var plan = CodexManagedCli.BuildPlan(new ManagedCliPlatform("win32", "x64", false), Release);

        Assert.Equal("https://github.com/openai/codex/releases/download/rust-v0.149.1/codex-x86_64-pc-windows-msvc.exe.tar.gz", plan.Url);
        Assert.Equal("3333cccc", plan.ExpectedSha256);
        Assert.Equal("codex-x86_64-pc-windows-msvc.exe", plan.ExecutableEntryName);
        Assert.Equal("codex.exe", plan.ExecutableFileName);
        Assert.False(plan.NeedsExecutableBit);

        // Windows gets all three siblings: code-mode-host (tool_mode: code_mode_only), command-runner and
        // windows-sandbox-setup (windows_sandbox_mode != disabled) — AC-1107.
        Assert.Equal(3, plan.AdditionalArtifacts.Count);

        var host = plan.AdditionalArtifacts.Single(a => a.FileName == "codex-code-mode-host.exe");
        Assert.Equal("https://github.com/openai/codex/releases/download/rust-v0.149.1/codex-code-mode-host-x86_64-pc-windows-msvc.exe.tar.gz", host.Url);
        Assert.Equal("5555eeee", host.ExpectedSha256);
        Assert.Equal("codex-code-mode-host-x86_64-pc-windows-msvc.exe", host.ArchiveEntryName);
        Assert.False(host.NeedsExecutableBit);

        var commandRunner = plan.AdditionalArtifacts.Single(a => a.FileName == "codex-command-runner.exe");
        Assert.Equal("https://github.com/openai/codex/releases/download/rust-v0.149.1/codex-command-runner-x86_64-pc-windows-msvc.exe.tar.gz", commandRunner.Url);
        Assert.Equal("6666ffff", commandRunner.ExpectedSha256);
        Assert.Equal("codex-command-runner-x86_64-pc-windows-msvc.exe", commandRunner.ArchiveEntryName);
        Assert.False(commandRunner.NeedsExecutableBit);

        var sandboxSetup = plan.AdditionalArtifacts.Single(a => a.FileName == "codex-windows-sandbox-setup.exe");
        Assert.Equal("https://github.com/openai/codex/releases/download/rust-v0.149.1/codex-windows-sandbox-setup-x86_64-pc-windows-msvc.exe.tar.gz", sandboxSetup.Url);
        Assert.Equal("77778888", sandboxSetup.ExpectedSha256);
        Assert.Equal("codex-windows-sandbox-setup-x86_64-pc-windows-msvc.exe", sandboxSetup.ArchiveEntryName);
        Assert.False(sandboxSetup.NeedsExecutableBit);
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

    [Fact]
    public void BuildPlan_PlatformWithoutAnAsset_Throws()
    {
        // arm64 windows is not in the fixture — a missing asset must fail loudly, not silently pick the wrong one.
        var act = () => CodexManagedCli.BuildPlan(new ManagedCliPlatform("win32", "arm64", false), Release);

        var ex = Assert.Throws<InvalidOperationException>(act);
        Assert.Contains("aarch64-pc-windows-msvc", ex.Message);
    }

    [Fact]
    public void BuildPlan_PrimaryPresentButCodeModeHostMissing_Throws()
    {
        // A recipe that promises a sibling binary must fail loudly if the release does not actually carry it — the
        // AC-1107 case this whole ticket is about, just caught at plan-build time instead of at runtime.
        const string release = """
            { "tag_name": "rust-v0.149.1", "assets": [
              { "name": "codex-x86_64-unknown-linux-musl.tar.gz", "browser_download_url": "https://github.com/openai/codex/releases/download/rust-v0.149.1/codex-x86_64-unknown-linux-musl.tar.gz", "digest": "sha256:1111aaaa" } ] }
            """;

        var act = () => CodexManagedCli.BuildPlan(new ManagedCliPlatform("linux", "x64", false), release);

        var ex = Assert.Throws<InvalidOperationException>(act);
        Assert.Contains("codex-code-mode-host-x86_64-unknown-linux-musl.tar.gz", ex.Message);
    }

    [Fact]
    public void BuildPlan_Windows_PrimaryAndCodeModeHostPresentButCommandRunnerMissing_Throws()
    {
        // Same as the code-mode-host case, for the two Windows-only siblings this release fix also covers.
        const string release = """
            { "tag_name": "rust-v0.149.1", "assets": [
              { "name": "codex-x86_64-pc-windows-msvc.exe.tar.gz", "browser_download_url": "https://github.com/openai/codex/releases/download/rust-v0.149.1/codex-x86_64-pc-windows-msvc.exe.tar.gz", "digest": "sha256:3333cccc" },
              { "name": "codex-code-mode-host-x86_64-pc-windows-msvc.exe.tar.gz", "browser_download_url": "https://github.com/openai/codex/releases/download/rust-v0.149.1/codex-code-mode-host-x86_64-pc-windows-msvc.exe.tar.gz", "digest": "sha256:5555eeee" } ] }
            """;

        var act = () => CodexManagedCli.BuildPlan(new ManagedCliPlatform("win32", "x64", false), release);

        var ex = Assert.Throws<InvalidOperationException>(act);
        Assert.Contains("codex-command-runner-x86_64-pc-windows-msvc.exe.tar.gz", ex.Message);
    }
}
