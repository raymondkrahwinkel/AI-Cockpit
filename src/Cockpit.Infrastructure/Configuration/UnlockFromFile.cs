using System.Text;
using Cockpit.Core.Abstractions.Secrets;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Configuration;

public enum UnlockFromFileResult
{
    Unlocked,
    NotNeeded,
    Refused,
}

// `Reason` is set only for `Refused`, and is always an operator sentence — never the password, and never a raw
// exception message, so it is safe to put in a log or on stderr.
public readonly record struct UnlockFromFileOutcome(UnlockFromFileResult Result, string? Reason = null)
{
    public static readonly UnlockFromFileOutcome Unlocked = new(UnlockFromFileResult.Unlocked);
    public static readonly UnlockFromFileOutcome NotNeeded = new(UnlockFromFileResult.NotNeeded);

    public static UnlockFromFileOutcome Refused(string reason) => new(UnlockFromFileResult.Refused, reason);
}

// AC-1354: unlocking credential encryption with no UI, for a headless start with nobody to click through
// `UnlockWindow`. Fail-closed: anything short of a verified-correct password is `Refused`, never a silent
// continue with the credentials still locked. No env-var fallback for the password itself (AC-1351 review M4).
public static class UnlockFromFile
{
    public const string Variable = "COCKPIT_UNLOCK_PASSWORD_FILE";

    public static async Task<UnlockFromFileOutcome> RunAsync(
        ISecretProtectionService protection,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _RunAsync(protection, logger, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Always — unlocked, not needed or refused — so a child process (an agent session's shell) never
            // sees the path to the secret.
            ProcessEnvironment.Remove(Variable);
        }
    }

    private static async Task<UnlockFromFileOutcome> _RunAsync(
        ISecretProtectionService protection, ILogger logger, CancellationToken cancellationToken)
    {
        var status = await protection.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var path = Environment.GetEnvironmentVariable(Variable);

        if (!status.Enabled)
        {
            if (!string.IsNullOrEmpty(path))
            {
                logger.LogWarning("Credential encryption is off; the unlock password file is not used.");
            }

            return UnlockFromFileOutcome.NotNeeded;
        }

        if (string.IsNullOrEmpty(path))
        {
            return UnlockFromFileOutcome.Refused($"{Variable} is not set.");
        }

        var password = await _ReadPasswordAsync(path, cancellationToken);
        if (password is null)
        {
            return UnlockFromFileOutcome.Refused($"Could not read the unlock password file at '{path}'.");
        }

        if (password.Length == 0)
        {
            return UnlockFromFileOutcome.Refused($"The unlock password file at '{path}' is empty.");
        }

        if (!await protection.UnlockAsync(password, cancellationToken).ConfigureAwait(false))
        {
            return UnlockFromFileOutcome.Refused("The password in the unlock password file is not correct.");
        }

        logger.LogInformation("Unlocked the credentials from the password file.");

        return UnlockFromFileOutcome.Unlocked;
    }

    // Null on any read failure (missing file/directory, a path that is a directory, no permission) — the kind of
    // error is not the caller's concern. Reads bytes, not `File.ReadAllTextAsync`, so the buffer can be cleared.
    // ponytail: the resulting `string` itself is not wiped — that needs a pinned char[]/SecureString, worth adding once there is an actual memory-dump threat model, not before.
    private static async Task<string?> _ReadPasswordAsync(string path, CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Fail-closed covers every read failure, not just the usual IOException/UnauthorizedAccessException —
            // a malformed path can throw ArgumentException or NotSupportedException before any I/O even starts.
            return null;
        }

        try
        {
            return _StripTrailingNewline(Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    // Exactly one trailing newline is stripped — the one a Docker secret written with `echo` carries — and
    // nothing else: interior or leading whitespace could be part of the password.
    private static string _StripTrailingNewline(string raw)
    {
        if (raw.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return raw[..^2];
        }

        return raw.EndsWith('\n') ? raw[..^1] : raw;
    }
}
