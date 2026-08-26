namespace Cockpit.Core.Configuration;

// Reader-facing product name lives here (AC-430), making a naming round a one-file edit without breaking install identifiers.
// Use it only where the product is named; running UI prose says "the cockpit" to avoid advertising copy.
// Namespaces, state folders, config keys, and repository identifiers stay stable for existing installations.
public static class CockpitProduct
{
    // The maker's half of the name — the word drawn at full strength in the title bar.
    public const string Brand = "Wispslate";

    // The product's own half — the faint word after the brand.
    public const string Product = "Cockpit";

    // Both halves, for the places that have a single string to put a name in.
    public const string DisplayName = $"{Brand} {Product}";
}
