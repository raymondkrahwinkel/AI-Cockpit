using NSubstitute;
using Cockpit.App.Services;
using Cockpit.App.ViewModels.Onboarding;
using Cockpit.Core.Abstractions.Backup;
using Cockpit.Core.Backup;
using Cockpit.Core.Layout;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Layout;

namespace Cockpit.Core.Tests.Onboarding;

/// <summary>
/// AC-1280: a restore that failed from the wizard has to leave this machine exactly as it found it. Against the
/// real <see cref="FirstRunWizardStateStore"/> over a real config file, because "nothing was changed" is a claim
/// about that file — a substituted store can only show that nothing was asked of it.
/// </summary>
public sealed class Ac1280FailedRestoreLeavesTheConfigTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"cockpit-ac1280-{Guid.NewGuid():N}");

    private string ConfigPath => Path.Combine(_directory, "cockpit.json");

    public Ac1280FailedRestoreLeavesTheConfigTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task AFailedRestore_LeavesCockpitJsonByteForByteAsItWas_AndTheWizardUnfinished()
    {
        await new LayoutSettingsStore(ConfigPath).SaveAsync(new LayoutSettings { SingleSessionLayout = true });
        var before = await File.ReadAllBytesAsync(ConfigPath);

        var stateStore = new FirstRunWizardStateStore(ConfigPath);
        var step = new RestoreStepViewModel(new _RefusesEveryArchive(), stateStore, Substitute.For<IAppRestartService>());

        await step.RestoreAsync("nowhere.zip");

        Assert.False(step.TookOver);
        Assert.NotEmpty(step.Status);
        Assert.Equal(before, await File.ReadAllBytesAsync(ConfigPath));
        Assert.Null(await stateStore.GetCompletedVersionAsync());
    }

    private sealed class _RefusesEveryArchive : IBackupService
    {
        public Task<BackupManifest> WriteAsync(string archivePath, BackupOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This test only restores.");

        public Task<BackupManifest> ReadManifestAsync(string archivePath, CancellationToken cancellationToken = default) =>
            throw new FileNotFoundException($"Could not find file '{archivePath}'.");

        public Task<RestoreReport> RestoreAsync(
            string archivePath,
            RestoreOptions options,
            IProgress<RestoreProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The manifest is read first, and this archive does not exist.");
    }
}
