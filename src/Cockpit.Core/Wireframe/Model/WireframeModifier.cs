namespace Cockpit.Core.Wireframe.Model;

// IsQuoted is kept so the round trip gives the source back unchanged: `value:"Raymond"` and `value:Raymond` mean
// the same thing but are not the same text, and the operator wrote one of the two.
public sealed record WireframeModifier(WireframeModifierName Name, string? Value = null, bool IsQuoted = false);
