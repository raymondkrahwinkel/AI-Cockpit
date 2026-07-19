namespace Cockpit.Core.Secrets;

/// <summary>
/// Holds the key for as long as the app runs, and nowhere else.
/// <para>
/// Whether the cockpit is unlocked is a fact about the process, not about one object graph: every settings
/// store builds its own file access, and they must all agree. Hence <see cref="Shared"/> — one instance the
/// container hands out and the non-DI callers reach directly, rather than a key that exists in one branch of
/// the graph and not another. A test builds its own holder and leaves the shared one alone.
/// </para>
/// </summary>
public interface ISecretKeyHolder
{
    /// <summary>The protector for the unlocked session, or <see langword="null"/> when encryption is off (or the app is not unlocked yet).</summary>
    ISecretProtector? Protector { get; }

    /// <summary>Fields the plugins declared as secret, on top of the name rule.</summary>
    SecretFields Fields { get; }

    /// <summary>
    /// Raised when a save wrote at least one credential to disk in the clear (AC-41). The awareness banner
    /// listens for this so it reappears the moment a new credential is added while encryption is off — the one
    /// event that fires from the universal config write seam, wherever the credential came from.
    /// </summary>
    event EventHandler? UnprotectedSecretsWritten;

    /// <summary>Tells listeners a credential was just written in the clear. Called by the config write seam; carries no value, only the fact.</summary>
    void NoteUnprotectedSecretsWritten();
}

/// <inheritdoc cref="ISecretKeyHolder"/>
public sealed class SecretKeyHolder : ISecretKeyHolder
{
    /// <summary>The process-wide holder. See the interface docs for why this is not purely a container concern.</summary>
    public static SecretKeyHolder Shared { get; } = new();

    private readonly HashSet<string> _declared = new(StringComparer.OrdinalIgnoreCase);

    private SecretFields _fields = SecretFields.ByName;

    public ISecretProtector? Protector { get; private set; }

    public SecretFields Fields => _fields;

    /// <inheritdoc />
    public event EventHandler? UnprotectedSecretsWritten;

    /// <inheritdoc />
    public void NoteUnprotectedSecretsWritten() => UnprotectedSecretsWritten?.Invoke(this, EventArgs.Empty);

    /// <summary>The app is unlocked: from here on, the settings are read and written through <paramref name="protector"/>.</summary>
    public void Unlock(ISecretProtector protector) => Protector = protector;

    /// <summary>Encryption is off — the settings are read and written in the clear.</summary>
    public void Lock() => Protector = null;

    /// <summary>
    /// Adds the secret keys a plugin declared (<c>plugin.json</c>), so its own fields are protected too. Additive:
    /// each plugin declares its own, and the second one to load must not erase the first one's.
    /// </summary>
    public void Declare(IEnumerable<string> keys)
    {
        _declared.UnionWith(keys);
        _fields = new SecretFields(_declared);
    }
}
