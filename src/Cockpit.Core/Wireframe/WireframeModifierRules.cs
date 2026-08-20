using Cockpit.Core.Wireframe.Model;

namespace Cockpit.Core.Wireframe;

// AC-905: which modifier means something on which component, straight off the table in docs/wireframe-format.md —
// one place the operator's properties panel and the editor's own refusal both read, so neither can drift from the
// other or from the doc.
public static class WireframeModifierRules
{
    // Whether `name` says anything on a component of `kind`, sitting inside `parentKind` (null for the screen line,
    // which has no parent). `w:`/`h:` are the only two the doc gates by parent rather than by the component's own
    // kind — a weight only means something to whatever is laying the child out.
    public static bool Applies(WireframeNodeKind kind, WireframeNodeKind? parentKind, WireframeModifierName name) => name switch
    {
        WireframeModifierName.Primary => kind is WireframeNodeKind.Button or WireframeNodeKind.Badge,
        WireframeModifierName.Selected => kind is WireframeNodeKind.Item or WireframeNodeKind.Tab,
        WireframeModifierName.Checked => kind is WireframeNodeKind.Checkbox or WireframeNodeKind.Radio or WireframeNodeKind.Toggle,
        // AC-914: a state has nothing of its own to dim or align — it stands in for a container's content, drawn
        // exactly where that container already is.
        WireframeModifierName.Disabled => kind is not WireframeNodeKind.State,
        WireframeModifierName.Align => kind is not WireframeNodeKind.State,
        // AC-907: a requirement about behaviour, not about drawing — applies to any component, screen line included.
        WireframeModifierName.Note => true,
        // AC-914: the one modifier a state carries — the container in its own screen it replaces the content of.
        WireframeModifierName.Replaces => kind is WireframeNodeKind.State,
        WireframeModifierName.Value => kind is WireframeNodeKind.Input or WireframeNodeKind.Textarea or WireframeNodeKind.Search
            or WireframeNodeKind.Select or WireframeNodeKind.Badge or WireframeNodeKind.Slider or WireframeNodeKind.Progress
            or WireframeNodeKind.Pagination,
        // AC-902: components that can plausibly be clicked, sitting inside a screen — not a whole screen or column,
        // and not an input, which has nowhere to go while it is being filled in.
        WireframeModifierName.Goto => kind is WireframeNodeKind.Button or WireframeNodeKind.Item or WireframeNodeKind.Label
            or WireframeNodeKind.Card or WireframeNodeKind.Image or WireframeNodeKind.Icon or WireframeNodeKind.Avatar
            or WireframeNodeKind.Badge or WireframeNodeKind.Row,
        WireframeModifierName.W => parentKind is WireframeNodeKind.Row or WireframeNodeKind.Header or WireframeNodeKind.Footer,
        // AC-914: a state sits directly under a screen too, but it has no height of its own to weight against its
        // siblings — it is never laid out, only swapped in for a container's content.
        WireframeModifierName.H => kind is not WireframeNodeKind.State && parentKind is WireframeNodeKind.Column or WireframeNodeKind.Group
            or WireframeNodeKind.Card or WireframeNodeKind.Screen or WireframeNodeKind.Nav or WireframeNodeKind.Sidebar
            or WireframeNodeKind.Main or WireframeNodeKind.Tab,
        _ => false,
    };

    // `value:` takes free text on most of its components, but a number on the three that measure a fill or a page —
    // the properties panel uses this to decide between a text box and a 0–100 spinner.
    public static bool ValueIsNumeric(WireframeNodeKind kind) =>
        kind is WireframeNodeKind.Slider or WireframeNodeKind.Progress or WireframeNodeKind.Pagination;
}
