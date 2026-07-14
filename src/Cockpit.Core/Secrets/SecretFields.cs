namespace Cockpit.Core.Secrets;

/// <summary>
/// Which fields in the cockpit's settings hold a credential.
/// <para>
/// One rule, two users: the backup scrubber empties these fields, and the protector encrypts them. Two lists
/// would drift, and the drift is invisible — a field the protector encrypts but the scrubber misses ships a
/// token in a backup that claims to carry none.
/// </para>
/// <para>
/// It works by name, not by value: a value that merely <em>looks</em> like a key is a guess, and a guess in
/// either direction is a mistake you cannot see — one leaks, the other quietly mangles something you needed.
/// A plugin can name fields the rule would not recognise (<c>pat</c>, <c>credential</c>); those are declared
/// by the plugin itself and passed in as <paramref name="declared"/> keys.
/// </para>
/// </summary>
public sealed class SecretFields(IEnumerable<string>? declared = null)
{
    private static readonly string[] Names =
    [
        "token",
        "apikey",
        "api_key",
        "secret",
        "password",
        "webhook",
    ];

    /// <summary>The name rule alone — no plugin declarations. What the backup scrubber has always used.</summary>
    public static SecretFields ByName { get; } = new();

    private readonly HashSet<string> _declared = new(declared ?? [], StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a field's name says it holds a credential, or a plugin declared that it does.</summary>
    public bool IsSecret(string name) =>
        _declared.Contains(name)
        || Names.Any(hint => name.Contains(hint, StringComparison.OrdinalIgnoreCase));
}
