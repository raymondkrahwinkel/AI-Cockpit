using Cockpit.Core.Secrets;
using Cockpit.Infrastructure.Configuration;
using FluentAssertions;

namespace Cockpit.Infrastructure.Tests.Security;

/// <summary>
/// The running-app lock (AC-5): <see cref="SecretProtectionService.Relock"/> is what an OS screen lock triggers. It
/// must take the key out of memory and leave the file exactly as it was — a lock, not the teardown that
/// <c>DisableAsync</c> is — so the same password opens it again afterwards.
/// </summary>
public sealed class SecretProtectionServiceRelockTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"cockpit-relock-{Guid.NewGuid():N}");

    private string ConfigPath => Path.Combine(_directory, "cockpit.json");

    public SecretProtectionServiceRelockTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Relock_ClearsTheKey_ButLeavesEncryptionOnAndTheFileOpenableAgain()
    {
        var holder = new SecretKeyHolder();
        var service = new SecretProtectionService(ConfigPath, holder);
        await service.EnableAsync("correct horse");

        holder.Protector.Should().NotBeNull("encryption was just turned on and the app is unlocked");
        (await service.GetStatusAsync()).Unlocked.Should().BeTrue();

        service.Relock();

        holder.Protector.Should().BeNull("a relock takes the derived key out of memory");
        var status = await service.GetStatusAsync();
        status.Unlocked.Should().BeFalse("the app is locked again");
        status.Enabled.Should().BeTrue("a relock is not a teardown — encryption stays on");

        // The on-disk ciphertext was untouched, so the very same password lets it back in.
        (await service.UnlockAsync("correct horse")).Should().BeTrue();
        holder.Protector.Should().NotBeNull();
    }

    [Fact]
    public void Relock_IsANoOp_WhenTheAppWasNeverUnlocked()
    {
        var holder = new SecretKeyHolder();
        var service = new SecretProtectionService(ConfigPath, holder);

        service.Relock();

        holder.Protector.Should().BeNull();
    }

    [Fact]
    public async Task WhileRelocked_TheConfigWriteSeamRefusesToWrite_RatherThanFallBackToTheClear()
    {
        var holder = new SecretKeyHolder();
        var service = new SecretProtectionService(ConfigPath, holder);
        await service.EnableAsync("correct horse");

        service.Relock();
        holder.IsLocked.Should().BeTrue("encryption stays on but the session key was cleared");

        // Every settings write goes through this one seam, which serializes the whole document. With no protector to
        // encrypt the secret sections and encryption still on, a write would land them on disk in the clear — so it is
        // refused, not silently written the way a bare null protector (encryption off) is allowed to be.
        var access = new CockpitConfigFileAccess(ConfigPath, holder);
        var write = async () => await access.UpdateAsync(_ => { }, CancellationToken.None);
        await write.Should().ThrowAsync<InvalidOperationException>().WithMessage("*locked*");

        // Unlocking clears the locked state and lets writes through again.
        (await service.UnlockAsync("correct horse")).Should().BeTrue();
        holder.IsLocked.Should().BeFalse();
        await access.UpdateAsync(_ => { }, CancellationToken.None);
    }
}
