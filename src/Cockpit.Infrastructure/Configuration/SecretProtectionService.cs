using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Secrets;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// Turns credential encryption on and off, and unlocks it at startup.
/// <para>
/// It works on the raw JSON rather than the typed config model on purpose: a migration must convert every
/// credential that is <em>in the file</em>, including the sections of plugins this build has never heard of and
/// any field a future version adds. Round-tripping through the typed model would silently drop what it does not
/// know about — and dropping a section during the one operation that rewrites every credential is exactly the
/// data loss this is supposed to prevent.
/// </para>
/// <para>
/// Every operation that rewrites the file (Enable/Disable/Reset/Dismiss/ChangePassword, and the startup sidecar
/// sweep) takes the shared <see cref="CockpitConfigWriteGate"/> — the same lock the typed settings stores use — so
/// a migration can never interleave with an ordinary save. The gate is non-reentrant, so each of these takes it
/// exactly once and never calls another gated method while holding it: <c>ChangePasswordAsync</c> in particular
/// re-encrypts in one gated pass rather than delegating to Disable+Enable, both because that would deadlock and
/// because Disable would put every credential back in the clear on disk for the width of the window between them.
/// </para>
/// </summary>
internal sealed class SecretProtectionService : ISecretProtectionService, ISingletonService
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private const string BackupSuffix = ".bak";

    private readonly string _configFilePath;
    private readonly SecretKeyHolder _keyHolder;

    /// <summary>
    /// Builds the protector for a derived key. A seam, not a constant, so a test can inject a protector whose
    /// ciphertext does not decrypt back — which is the only way to exercise the verify-before-publish abort, since
    /// the real AES-GCM protector always round-trips.
    /// </summary>
    private readonly Func<byte[], ISecretProtector> _protectorFactory;

    public SecretProtectionService()
        : this(CockpitConfigPath.Default, SecretKeyHolder.Shared)
    {
    }

    /// <summary>Test seam: an arbitrary config file, a holder that is not the process-wide one, and — for the
    /// round-trip-failure tests — a protector factory that stands in for the real AES-GCM one.</summary>
    internal SecretProtectionService(
        string configFilePath,
        SecretKeyHolder keyHolder,
        Func<byte[], ISecretProtector>? protectorFactory = null)
    {
        _configFilePath = configFilePath;
        _keyHolder = keyHolder;
        _protectorFactory = protectorFactory ?? (key => new SecretProtector(key));
    }

    public async Task<SecretProtectionStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);
        var enabled = document["Security"]?.Deserialize<SecretProtectionEntry>(SerializerOptions)?.Enabled ?? false;
        var unlocked = _keyHolder.Protector is not null;

        // The banner shows only while encryption is off, there is at least one credential in the clear, and at
        // least one of those credential fields is one the operator has not already dismissed. Keyed on additions,
        // not on any change: a new credential path re-nags, but removing or rotating one that was already there
        // does not — so the dismissal is a subset check, not an equality one (Raymond, 2026-07-19, review #7).
        var shouldWarn = false;
        if (!enabled)
        {
            var plaintextPaths = PlaintextSecretPaths(document);
            if (plaintextPaths.Count > 0)
            {
                var dismissed = document["SecurityNotice"]?.Deserialize<SecurityNoticeEntry>(SerializerOptions)?.DismissedPaths;
                var dismissedSet = dismissed is null ? null : new HashSet<string>(dismissed, StringComparer.Ordinal);
                shouldWarn = dismissedSet is null || plaintextPaths.Any(path => !dismissedSet.Contains(path));
            }
        }

        return new SecretProtectionStatus(enabled, unlocked, shouldWarn);
    }

    public async Task<bool> UnlockAsync(string password, CancellationToken cancellationToken = default)
    {
        var security = await ReadSecurityAsync(cancellationToken).ConfigureAwait(false);
        if (security is not { Enabled: true })
        {
            return true;
        }

        var protector = ProtectorFor(password, security);
        if (!VerifierMatches(protector, security))
        {
            return false;
        }

        _keyHolder.Unlock(protector);

        // Startup sweep (AC-41, Raymond 2026-07-19): a crash after a migration's Save but before its own scrub, or
        // an old recovery quarantine, can leave plaintext credentials in a sidecar while the live config is
        // ciphertext. This is the first moment the key is in hand on a normal start, so it is where they get closed.
        await ScrubPlaintextSidecarsAsync(protector, cancellationToken).ConfigureAwait(false);

        return true;
    }

    public void Relock() =>
        // The only reference to the derived key is the protector the holder is keeping; dropping it is what takes the
        // key out of memory (AC-5). No file is touched — encryption stays on, the config on disk is unchanged
        // ciphertext, and the next UnlockAsync derives the key afresh from the password. This is the running-app twin
        // of never having unlocked at startup.
        _keyHolder.Lock();

    public async Task EnableAsync(
        string password,
        IProgress<SecretMigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var gate = await CockpitConfigWriteGate.AcquireAsync(_configFilePath, cancellationToken).ConfigureAwait(false);

        var document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);
        var security = new SecretProtectionEntry
        {
            Enabled = true,
            Salt = Convert.ToBase64String(SecretKey.NewSalt()),
            Kdf = SecretKey.Pbkdf2Sha512,
            Iterations = SecretKey.DefaultIterations,
        };

        var protector = ProtectorFor(password, security);
        security.Verifier = protector.Protect(SecretProtectionEntry.VerifierPath, SecretProtectionEntry.VerifierPlaintext);

        // Encrypt what is there now — a value already encrypted (a half-migrated file from an interrupted run) is
        // left alone rather than encrypted twice — and prove each freshly encrypted field reads back, in the very
        // field it will live in, before the atomic swap below wipes the plaintext. A field that will not round-trip
        // aborts here with the file untouched, rather than publishing a config we can never read our way back into.
        ConvertSecrets(document, progress, (path, value) =>
            SecretProtector.IsProtected(value) ? value : ProtectVerified(protector, path, value));
        WriteSecurity(document, security);

        // Encryption on answers the banner; clear any dismissal so a later Disable starts from a clean slate.
        WriteSecurityNotice(document, notice: null);

        Save(document);
        _keyHolder.Unlock(protector);

        // The plaintext the atomic swap just replaced still lives in the .bak it kept, and in any .damaged-* copy
        // an earlier recovery quarantined. Scrub both now that the key is in hand — otherwise the at-rest plaintext
        // this whole feature exists to remove would simply move next door.
        ScrubPlaintextSidecars(protector);
    }

    public async Task DisableAsync(
        IProgress<SecretMigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_keyHolder.Protector is not { } protector)
        {
            throw new InvalidOperationException("The cockpit must be unlocked before encryption can be turned off.");
        }

        using var gate = await CockpitConfigWriteGate.AcquireAsync(_configFilePath, cancellationToken).ConfigureAwait(false);

        var document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);

        ConvertSecrets(document, progress, (path, value) =>
            SecretProtector.IsProtected(value) ? protector.Unprotect(path, value) : null);
        WriteSecurity(document, security: null);

        // A deliberate Disable exposes every credential again, so the banner should return at once (Raymond,
        // 2026-07-19): clear the dismissal for the set now back in the clear.
        WriteSecurityNotice(document, notice: null);

        Save(document);
        _keyHolder.Lock();
    }

    public async Task ChangePasswordAsync(
        string currentPassword,
        string newPassword,
        IProgress<SecretMigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // One gated pass, not Disable-then-Enable (review #1). Delegating would deadlock on the non-reentrant gate,
        // and — worse — Disable would write every credential back to the live cockpit.json in the clear for the
        // width of the window before Enable re-encrypted them. Here the decrypt-then-re-encrypt happens entirely in
        // memory and only the finished ciphertext is swapped in atomically, so the primary file goes straight from
        // old ciphertext to new ciphertext and is never readable in between.
        using var gate = await CockpitConfigWriteGate.AcquireAsync(_configFilePath, cancellationToken).ConfigureAwait(false);

        var document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);
        var current = document["Security"]?.Deserialize<SecretProtectionEntry>(SerializerOptions);
        if (current is not { Enabled: true })
        {
            throw new SecretProtectionException("Encryption is not on, so there is no password to change.");
        }

        var currentProtector = ProtectorFor(currentPassword, current);
        if (!VerifierMatches(currentProtector, current))
        {
            throw new SecretProtectionException("The current password is not correct.");
        }

        var next = new SecretProtectionEntry
        {
            Enabled = true,
            Salt = Convert.ToBase64String(SecretKey.NewSalt()),
            Kdf = SecretKey.Pbkdf2Sha512,
            Iterations = SecretKey.DefaultIterations,
        };
        var nextProtector = ProtectorFor(newPassword, next);
        next.Verifier = nextProtector.Protect(SecretProtectionEntry.VerifierPath, SecretProtectionEntry.VerifierPlaintext);

        // Decrypt each field with the old key (proving it, since a wrong current password should already have been
        // caught above) and re-encrypt with the new one, verified — a plain field from a half-migrated file is
        // simply encrypted. The plaintext exists only as a local here; it never reaches the document that is saved.
        ConvertSecrets(document, progress, (path, value) =>
        {
            var plaintext = SecretProtector.IsProtected(value) ? currentProtector.Unprotect(path, value) : value;

            return ProtectVerified(nextProtector, path, plaintext);
        });
        WriteSecurity(document, next);

        Save(document);
        _keyHolder.Unlock(nextProtector);
        ScrubPlaintextSidecars(nextProtector);
    }

    public async Task ResetForgottenPasswordAsync(CancellationToken cancellationToken = default)
    {
        using var gate = await CockpitConfigWriteGate.AcquireAsync(_configFilePath, cancellationToken).ConfigureAwait(false);

        var document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);

        // Every credential goes; everything else the operator configured stays. The backup the atomic write keeps
        // still holds the ciphertext, so this is not quite the end of the world if the password resurfaces.
        SecretJsonWalker.Transform(document, _keyHolder.Fields, (_, _) => string.Empty);
        WriteSecurity(document, security: null);
        WriteSecurityNotice(document, notice: null);

        Save(document);
        _keyHolder.Lock();
    }

    public async Task DismissUnprotectedWarningAsync(CancellationToken cancellationToken = default)
    {
        using var gate = await CockpitConfigWriteGate.AcquireAsync(_configFilePath, cancellationToken).ConfigureAwait(false);

        var document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);

        // Nothing to silence once encryption is on — and re-reading under the gate is what keeps a dismissal from
        // landing on top of a set of credentials that changed between the banner showing and the click.
        if (document["Security"]?.Deserialize<SecretProtectionEntry>(SerializerOptions)?.Enabled == true)
        {
            return;
        }

        var plaintextPaths = PlaintextSecretPaths(document);
        if (plaintextPaths.Count == 0)
        {
            return;
        }

        // Store the field paths themselves, sorted, so a later status can tell a genuinely new credential (a path
        // not in this set) from a removal or a rotation of one already here (review #7). The paths are field
        // locations, not values, so they are safe to keep in the clear while encryption is off.
        WriteSecurityNotice(document, new SecurityNoticeEntry
        {
            DismissedPaths = [.. plaintextPaths.Distinct().OrderBy(path => path, StringComparer.Ordinal)],
        });
        Save(document);
    }

    /// <summary>
    /// Rewrites every credential in the document, reporting progress. All-or-nothing: one field that will not
    /// convert (a wrong key, an altered value) aborts the whole migration with the file untouched, rather than
    /// leaving a config half in the clear and half unreadable with no way to tell which fields are which.
    /// </summary>
    private void ConvertSecrets(JsonNode document, IProgress<SecretMigrationProgress>? progress, Func<string, string, string?> transform)
    {
        // Counting pass first: a progress bar that does not know its total cannot show progress. It runs on a
        // clone and writes each value back as it found it, so it counts the credential fields without touching
        // the document the migration is about to rewrite.
        var total = SecretJsonWalker.Transform(document.DeepClone(), _keyHolder.Fields, (_, value) => value).Count;

        var completed = 0;
        progress?.Report(new SecretMigrationProgress(completed, total));

        SecretJsonWalker.Transform(document, _keyHolder.Fields, (path, value) =>
        {
            var converted = transform(path, value);
            progress?.Report(new SecretMigrationProgress(++completed, total));

            return converted;
        });
    }

    /// <summary>
    /// The credential fields whose value is still in the clear — the set the awareness banner is about. Runs on a
    /// clone because the walker rewrites in place and this only wants to read the shape; a value already encrypted
    /// is not counted, so a half-migrated file does not keep the banner up over ciphertext.
    /// </summary>
    private IReadOnlyList<string> PlaintextSecretPaths(JsonNode document) =>
        SecretJsonWalker.Transform(
            document.DeepClone(),
            _keyHolder.Fields,
            (_, value) => SecretProtector.IsProtected(value) ? null : value);

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> and proves it reads back in the same field before returning the
    /// ciphertext — the verify-before-publish guard. A protector whose ciphertext will not decrypt to what it just
    /// encrypted throws here, before anything is saved, so the on-disk file is left exactly as it was.
    /// </summary>
    private static string ProtectVerified(ISecretProtector protector, string path, string plaintext)
    {
        var ciphertext = protector.Protect(path, plaintext);
        if (protector.Unprotect(path, ciphertext) != plaintext)
        {
            throw new SecretProtectionException(
                $"The credential at '{path}' did not survive a round-trip through encryption; your settings were left untouched.");
        }

        return ciphertext;
    }

    /// <summary>
    /// Whether <paramref name="protector"/> is built from the right password: decrypting the known verifier string
    /// proves the key without touching — and risking mangling — the operator's actual credentials. A value that
    /// will not decrypt (the wrong password) is a false, not a throw.
    /// </summary>
    private static bool VerifierMatches(ISecretProtector protector, SecretProtectionEntry security)
    {
        try
        {
            return protector.Unprotect(SecretProtectionEntry.VerifierPath, security.Verifier)
                == SecretProtectionEntry.VerifierPlaintext;
        }
        catch (SecretProtectionException)
        {
            return false;
        }
    }

    private ISecretProtector ProtectorFor(string password, SecretProtectionEntry security) =>
        _protectorFactory(SecretKey.Derive(password, Convert.FromBase64String(security.Salt), security.Iterations, security.Kdf));

    private async Task<JsonNode> ReadDocumentAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_configFilePath))
        {
            return new JsonObject();
        }

        // The same retry-read the settings stores use (review #9): File.Replace holds the file for the length of a
        // swap, so a read that lands in that window gets a sharing violation rather than either version. Waiting the
        // writer out — rather than reading ungated — is what keeps a status probe from throwing when a save happens
        // to be publishing at the same moment. It is the fix for the 2026-07-15 incident, applied here too.
        var json = await CockpitConfigFileAccess.ReadWhenNotBeingReplacedAsync(_configFilePath, cancellationToken).ConfigureAwait(false);

        return JsonNode.Parse(json) ?? new JsonObject();
    }

    private async Task<SecretProtectionEntry?> ReadSecurityAsync(CancellationToken cancellationToken)
    {
        var document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);

        return document["Security"]?.Deserialize<SecretProtectionEntry>(SerializerOptions);
    }

    private static void WriteSecurity(JsonNode document, SecretProtectionEntry? security)
    {
        if (document is not JsonObject json)
        {
            return;
        }

        if (security is null)
        {
            json.Remove("Security");

            return;
        }

        json["Security"] = JsonSerializer.SerializeToNode(security, SerializerOptions);
    }

    private static void WriteSecurityNotice(JsonNode document, SecurityNoticeEntry? notice)
    {
        if (document is not JsonObject json)
        {
            return;
        }

        if (notice?.DismissedPaths is not { Count: > 0 })
        {
            json.Remove("SecurityNotice");

            return;
        }

        json["SecurityNotice"] = JsonSerializer.SerializeToNode(notice, SerializerOptions);
    }

    private async Task ScrubPlaintextSidecarsAsync(ISecretProtector protector, CancellationToken cancellationToken)
    {
        using var gate = await CockpitConfigWriteGate.AcquireAsync(_configFilePath, cancellationToken).ConfigureAwait(false);

        ScrubPlaintextSidecars(protector);
    }

    /// <summary>
    /// Re-encrypts any plaintext credential left in a sidecar, or removes a copy too damaged to read. Assumes the
    /// write gate is already held (its callers take it), so it never races the atomic swap that also writes .bak.
    /// </summary>
    private void ScrubPlaintextSidecars(ISecretProtector protector)
    {
        // The .bak the atomic swap keeps is always valid JSON — a copy of a good config — so re-encrypt it in place.
        ScrubSidecar(_configFilePath + BackupSuffix, protector, deleteIfUnreadable: false);

        // Older recovery quarantines (cockpit.json.damaged-<timestamp>) can hold plaintext too (Raymond,
        // 2026-07-19). Re-encrypt what parses; delete what does not — a copy we cannot even read is worthless as
        // recovery and is only somewhere for a plaintext token to sit.
        var directory = Path.GetDirectoryName(_configFilePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        foreach (var damaged in Directory.EnumerateFiles(directory, $"{Path.GetFileName(_configFilePath)}.damaged-*"))
        {
            ScrubSidecar(damaged, protector, deleteIfUnreadable: true);
        }
    }

    private void ScrubSidecar(string path, ISecretProtector protector, bool deleteIfUnreadable)
    {
        if (!File.Exists(path))
        {
            return;
        }

        JsonNode? document;
        try
        {
            document = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            document = null;
        }

        if (document is null)
        {
            if (deleteIfUnreadable)
            {
                TryDelete(path);
            }

            return;
        }

        var rewritten = SecretJsonWalker.Transform(document, _keyHolder.Fields,
            (fieldPath, value) => SecretProtector.IsProtected(value) ? null : protector.Protect(fieldPath, value));

        if (rewritten.Count > 0)
        {
            CockpitConfigPath.WriteAllTextPrivate(path, document.ToJsonString(SerializerOptions));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A locked leftover is untidy, not dangerous — the next start sweeps it.
        }
    }

    private void Save(JsonNode document) =>
        CockpitConfigPath.ReplaceAtomicallyPrivate(_configFilePath, document.ToJsonString(SerializerOptions));
}
