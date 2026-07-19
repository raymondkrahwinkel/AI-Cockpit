namespace Cockpit.Core.Abstractions.Secrets;

/// <summary>What the cockpit knows about its own credential protection before anything is unlocked.</summary>
/// <param name="Enabled">Whether the operator turned encryption on.</param>
/// <param name="Unlocked">Whether the key for this session has been derived, so the settings can be read.</param>
/// <param name="ShouldWarnUnprotected">
/// Whether the awareness banner (AC-41) should show: encryption is off, the settings hold at least one credential
/// in the clear, and the operator has not dismissed the warning for this exact set of credential fields. Defaults
/// off, so a status built without it (a test, a design-time stand-in) simply does not nag.
/// </param>
public readonly record struct SecretProtectionStatus(bool Enabled, bool Unlocked, bool ShouldWarnUnprotected = false);

/// <summary>How far a migration has come, so the operator watches it happen instead of watching nothing happen.</summary>
/// <param name="Completed">Fields converted so far.</param>
/// <param name="Total">Fields to convert.</param>
public readonly record struct SecretMigrationProgress(int Completed, int Total);

/// <summary>
/// Turning credential encryption on and off, and unlocking it at startup.
/// <para>
/// Every operation that rewrites the config does so atomically and keeps a backup: a migration interrupted
/// halfway — a crash, the power going — must leave the operator with their credentials, not with half a file.
/// </para>
/// </summary>
public interface ISecretProtectionService
{
    Task<SecretProtectionStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Remembers that the operator dismissed the awareness banner (AC-41) for the credentials now in the file, so
    /// it does not nag again until a new credential is added. Bound to a fingerprint of the credential field paths
    /// — not their values — so rotating a key on an existing field does not bring the banner back, but adding a
    /// new one does. A no-op once encryption is on, since there is then nothing to warn about.
    /// </summary>
    Task DismissUnprotectedWarningAsync(CancellationToken cancellationToken = default);

    /// <summary>Derives the key from <paramref name="password"/> and, if it is the right one, unlocks the settings for this run. False means: wrong password.</summary>
    Task<bool> UnlockAsync(string password, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the in-memory key so the settings can no longer be read — the running-app equivalent of the startup
    /// lock (AC-5), used when the OS screen locks. Encryption stays on and nothing on disk changes: this only drops
    /// the derived key the process was holding, so the very next read hands back ciphertext again and a fresh
    /// <see cref="UnlockAsync"/> is what lets it in. A no-op when the app was not unlocked to begin with. Unlike
    /// <see cref="DisableAsync"/> it never writes the credentials back in the clear — it is a lock, not a teardown.
    /// </summary>
    void Relock();

    /// <summary>Encrypts every credential in the settings with a key derived from <paramref name="password"/>, and leaves the app unlocked.</summary>
    Task EnableAsync(string password, IProgress<SecretMigrationProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Writes every credential back in the clear. Requires an unlocked app — there is no way to decrypt without the key.</summary>
    Task DisableAsync(IProgress<SecretMigrationProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Re-encrypts every credential under a new password (and a fresh salt).</summary>
    Task ChangePasswordAsync(string currentPassword, string newPassword, IProgress<SecretMigrationProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Empties every credential and turns encryption off: the way back in for an operator who forgot their
    /// password. Their profiles, layout and shortcuts survive; the tokens have to be typed again. Without this
    /// a forgotten password would brick the app, and a promise with no way out is one people route around.
    /// </summary>
    Task ResetForgottenPasswordAsync(CancellationToken cancellationToken = default);
}
