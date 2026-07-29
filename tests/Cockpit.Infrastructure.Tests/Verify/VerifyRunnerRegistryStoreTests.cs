using Cockpit.Core.Verify;
using Cockpit.Infrastructure.Verify;

namespace Cockpit.Infrastructure.Tests.Verify;

/// <summary>
/// The verify-runner registry's persistence to <c>cockpit.json</c> (AC-86), against a real temporary config file
/// rather than the operator's own. The registry is the source of truth for which command the verify loop may run,
/// so surviving a restart — a fresh store instance reading what an earlier one wrote — is the property that matters.
/// </summary>
public sealed class VerifyRunnerRegistryStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
    private readonly string _configPath;

    public VerifyRunnerRegistryStoreTests()
    {
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "cockpit.json");
    }

    [Fact]
    public async Task ListAsync_NoConfigFile_IsEmpty()
    {
        var store = new VerifyRunnerRegistryStore(_configPath);

        Assert.Empty((await store.ListAsync()));
    }

    [Fact]
    public async Task SaveAsync_ThenListFromAFreshStore_SurvivesTheRestart()
    {
        var runner = _Runner("Cockpit", "/projects/cockpit");
        await new VerifyRunnerRegistryStore(_configPath).SaveAsync(runner);

        var reloaded = await new VerifyRunnerRegistryStore(_configPath).ListAsync();

        Assert.Equivalent(runner, Assert.Single(reloaded));
    }

    [Fact]
    public async Task SaveAsync_SameLabelTwice_ReplacesRatherThanDuplicates()
    {
        var store = new VerifyRunnerRegistryStore(_configPath);
        await store.SaveAsync(_Runner("Cockpit", "/projects/cockpit") with { Command = "dotnet" });
        await store.SaveAsync(_Runner("Cockpit", "/projects/cockpit") with { Command = "pwsh" });

        var runners = await store.ListAsync();

        Assert.Equal("pwsh", Assert.Single(runners).Command);
    }

    [Fact]
    public async Task RemoveAsync_DropsOnlyTheMatchingRunner()
    {
        var store = new VerifyRunnerRegistryStore(_configPath);
        await store.SaveAsync(_Runner("Cockpit", "/projects/cockpit"));
        await store.SaveAsync(_Runner("StartPage", "/projects/startpage"));

        await store.RemoveAsync("Cockpit");

        Assert.Equal("StartPage", Assert.Single((await store.ListAsync())).Label);
    }

    private static VerifyRunner _Runner(string label, string workingDirectory) => new(
        Label: label,
        WorkingDirectory: workingDirectory,
        Command: "dotnet",
        Arguments: ["run", "--project", "src/Cockpit.App", "--", "--screenshot", "out.png", "--scene", "session", "--snapshot", "out.txt"],
        SnapshotPath: Path.Combine(workingDirectory, "out.txt"),
        ScreenshotPath: Path.Combine(workingDirectory, "out.png"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
