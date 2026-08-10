using System.Security.Cryptography;
using Cockpit.Plugin.Depot.Secrets;

namespace Cockpit.Plugin.Depot.ProjectDefinition;

// Builds and unwraps a project's password envelope (AC-607). The data key is what every sensitive field is
// actually encrypted under; the password and the recovery code each only wrap a copy of it, so either one alone
// can recover every field a project has already encrypted.
public static class CockpitProjectPasswordEnvelopeFactory
{
    // Excludes 0/O/1/I/L — the characters most often misread or mistyped when copied by hand. Ungrouped: a
    // caller formats it for display (e.g. dashed 4-character blocks like "ABCD-EFGH-JKMN-...") if and when there
    // is a caller to show it; this factory only ever returns the raw string.
    private const string RecoveryAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int RecoveryCodeLength = 24;

    private const string PasswordWrapperPath = "PasswordEnvelope.Password.WrappedDataKey";
    private const string RecoveryWrapperPath = "PasswordEnvelope.Recovery.WrappedDataKey";

    private const int DataKeyBytes = 32;

    // AC-607 review finding 9: a deserialized envelope's Iterations is otherwise unbounded — a hostile envelope
    // could name a multi-billion-iteration PBKDF2 call as a DoS. 10 million is generously above DefaultIterations
    // (210,000) yet still bounds the CPU cost of one unwrap attempt.
    private const int MaxIterations = 10_000_000;

    public static (CockpitProjectPasswordEnvelope Envelope, byte[] DataKey, string RecoveryCode) Create(string password)
    {
        var dataKey = RandomNumberGenerator.GetBytes(DataKeyBytes);
        var recoveryCode = RandomNumberGenerator.GetString(RecoveryAlphabet, RecoveryCodeLength);

        var envelope = new CockpitProjectPasswordEnvelope
        {
            Password = _Wrap(dataKey, password, PasswordWrapperPath),
            Recovery = _Wrap(dataKey, recoveryCode, RecoveryWrapperPath),
        };

        return (envelope, dataKey, recoveryCode);
    }

    public static byte[]? TryUnwrapWithPassword(CockpitProjectPasswordEnvelope envelope, string password) =>
        _TryUnwrap(envelope, envelope.Password, password, PasswordWrapperPath);

    public static byte[]? TryUnwrapWithRecoveryCode(CockpitProjectPasswordEnvelope envelope, string recoveryCode) =>
        _TryUnwrap(envelope, envelope.Recovery, recoveryCode, RecoveryWrapperPath);

    // Re-wraps only Password, under a fresh salt, given the data key the caller already unwrapped some other way
    // (the old password or the recovery code). Recovery and every already-encrypted field value are untouched.
    public static CockpitProjectPasswordEnvelope ChangePassword(
        CockpitProjectPasswordEnvelope envelope, byte[] dataKey, string newPassword) => new()
    {
        Kdf = envelope.Kdf,
        Iterations = envelope.Iterations,
        Password = _Wrap(dataKey, newPassword, PasswordWrapperPath),
        Recovery = envelope.Recovery,
    };

    private static CockpitProjectKeyWrapper _Wrap(byte[] dataKey, string secret, string path)
    {
        var salt = ProjectSecretKey.NewSalt();
        var wrapperKey = ProjectSecretKey.Derive(secret, salt, ProjectSecretKey.DefaultIterations);

        return new CockpitProjectKeyWrapper
        {
            Salt = Convert.ToBase64String(salt),
            WrappedDataKey = new ProjectSecretProtector(wrapperKey).Protect(path, Convert.ToBase64String(dataKey)),
        };
    }

    // Null on a wrong secret, an unsupported/unknown Kdf, corrupted base64, or a malformed envelope off the wire
    // (missing wrapper/Salt/WrappedDataKey, non-positive or absurdly large Iterations) — every invalid-input case
    // collapses to null, never an exception. Missing/out-of-range data is checked up front (not exceptional).
    private static byte[]? _TryUnwrap(
        CockpitProjectPasswordEnvelope envelope, CockpitProjectKeyWrapper wrapper, string secret, string path)
    {
        if (wrapper is null || wrapper.Salt is null || wrapper.WrappedDataKey is null)
        {
            return null;
        }

        if (envelope.Iterations is < 1 or > MaxIterations)
        {
            return null;
        }

        try
        {
            var salt = Convert.FromBase64String(wrapper.Salt);
            var wrapperKey = ProjectSecretKey.Derive(secret, salt, envelope.Iterations, envelope.Kdf);
            var unwrapped = new ProjectSecretProtector(wrapperKey).Unprotect(path, wrapper.WrappedDataKey);

            return Convert.FromBase64String(unwrapped);
        }
        catch (Exception exception) when (exception is ProjectSecretProtectionException or NotSupportedException or FormatException)
        {
            return null;
        }
    }
}
