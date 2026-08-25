namespace Cockpit.Core.Secrets;

// Which fields in the cockpit's settings hold a credential. One rule shared by the backup scrubber and the
// protector, so the two never drift apart. Works by name, not by value, since a plugin can name fields the
// rule would not recognise (`pat`, `credential`); those are declared by the plugin as `declared` keys.
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

    // The name rule alone — no plugin declarations. What the backup scrubber has always used.
    public static SecretFields ByName { get; } = new();

    private readonly HashSet<string> _declared = new(declared ?? [], StringComparer.OrdinalIgnoreCase);

    // Whether a field's name says it holds a credential, or a plugin declared that it does.
    public bool IsSecret(string name) =>
        _declared.Contains(name)
        || Names.Any(hint => name.Contains(hint, StringComparison.OrdinalIgnoreCase));
}
