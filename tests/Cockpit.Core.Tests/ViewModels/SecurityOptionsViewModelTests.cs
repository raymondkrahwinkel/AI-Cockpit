using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Secrets;
using FluentAssertions;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The Security tab's awareness banner (AC-41): the view model turns the service's "should warn" into the bound
/// flag the banner reads, and dismissing both hides it and tells the service so it stays hidden.
/// </summary>
public class SecurityOptionsViewModelTests
{
    [Fact]
    public async Task RefreshAsync_MapsTheServicesWarning_OntoTheBanner()
    {
        var vm = new SecurityOptionsViewModel(new FakeProtection { Warn = true });

        await vm.RefreshAsync();

        vm.ShowUnprotectedBanner.Should().BeTrue();
    }

    [Fact]
    public async Task RefreshAsync_LeavesTheBannerDown_WhenTheServiceDoesNotWarn()
    {
        var vm = new SecurityOptionsViewModel(new FakeProtection { Warn = false });

        await vm.RefreshAsync();

        vm.ShowUnprotectedBanner.Should().BeFalse();
    }

    [Fact]
    public async Task DismissBanner_HidesItAtOnce_AndPersistsTheDismissal()
    {
        var protection = new FakeProtection { Warn = true };
        var vm = new SecurityOptionsViewModel(protection);
        await vm.RefreshAsync();

        await vm.DismissBannerCommand.ExecuteAsync(null);

        vm.ShowUnprotectedBanner.Should().BeFalse("the operator dismissed it");
        protection.DismissCalls.Should().Be(1, "the dismissal is persisted through the service, not just hidden");
    }

    private sealed class FakeProtection : ISecretProtectionService
    {
        public bool Warn { get; set; }

        public int DismissCalls { get; private set; }

        public Task<SecretProtectionStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SecretProtectionStatus(Enabled: false, Unlocked: false, ShouldWarnUnprotected: Warn));

        public Task DismissUnprotectedWarningAsync(CancellationToken cancellationToken = default)
        {
            DismissCalls++;
            Warn = false;

            return Task.CompletedTask;
        }

        public Task<bool> UnlockAsync(string password, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public void Relock()
        {
        }

        public Task EnableAsync(string password, IProgress<SecretMigrationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DisableAsync(IProgress<SecretMigrationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ChangePasswordAsync(string currentPassword, string newPassword, IProgress<SecretMigrationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ResetForgottenPasswordAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
