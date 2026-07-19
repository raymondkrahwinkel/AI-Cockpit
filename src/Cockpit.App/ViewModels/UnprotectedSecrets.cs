using Cockpit.Core.Abstractions.Secrets;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Stands in for the real service where there is no container: the XAML previewer, and the unit tests that build
/// a view model without infrastructure. It reports "not encrypted" and protects nothing, which is the truth in
/// both cases — a design-time surface has no config file to protect.
/// </summary>
internal sealed class UnprotectedSecrets : ISecretProtectionService
{
    public Task<SecretProtectionStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new SecretProtectionStatus(Enabled: false, Unlocked: false, ShouldWarnUnprotected: false));

    public Task DismissUnprotectedWarningAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<bool> UnlockAsync(string password, CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task EnableAsync(string password, IProgress<SecretMigrationProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task DisableAsync(IProgress<SecretMigrationProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task ChangePasswordAsync(string currentPassword, string newPassword, IProgress<SecretMigrationProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task ResetForgottenPasswordAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
