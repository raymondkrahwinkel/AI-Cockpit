namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// The <c>SecurityNotice</c> section of <c>cockpit.json</c>: what the operator has already been told and told us
/// not to tell them again. Today that is only the awareness banner's dismissal (AC-41).
/// <para>
/// Deliberately its own section, apart from the crypto <see cref="SecretProtectionEntry"/>: this has to stay
/// readable while encryption is off (that is exactly when the banner shows), and it carries no secret — the
/// <em>locations</em> of the credential fields, never their values. So a hand-edit that clears it costs nothing but
/// a banner the operator dismissed coming back once.
/// </para>
/// </summary>
internal sealed class SecurityNoticeEntry
{
    /// <summary>
    /// The credential field paths that were in the clear when the operator dismissed the banner, sorted. A later
    /// status re-nags only when a current plaintext path is <em>not</em> in this set — a genuinely new credential
    /// (review #7). Removing a field, or rotating the value at one already listed here, leaves the banner dismissed.
    /// Null or empty means never dismissed.
    /// <para>
    /// These are field <em>locations</em> (<c>McpServers[0].ApiKey</c>), not values, so they are safe to keep in
    /// the clear. They live in an array so the walker never mistakes them for credentials — it only encrypts values
    /// under a secret-named object key, and array elements have no such key.
    /// </para>
    /// </summary>
    public List<string>? DismissedPaths { get; set; }
}
