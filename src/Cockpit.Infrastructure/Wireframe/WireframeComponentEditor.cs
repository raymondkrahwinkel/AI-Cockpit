using Cockpit.Core.Abstractions.Wireframe;
using Cockpit.Core.Wireframe;
using Cockpit.Core.Wireframe.Model;

namespace Cockpit.Infrastructure.Wireframe;

// One contiguous line-level change: the lines at `At` that read `Before` now read `After`. Journaled so a revert can
// find its own lines back by content instead of by a line number a later edit has already moved. `Anchor` is the
// line that stood just above, the handle a pure insertion has when there is no `After` to search for.
internal readonly record struct WireframePatch(int At, string? Anchor, IReadOnlyList<string> Before, IReadOnlyList<string> After);

// One per-component edit's outcome: the new source, a readable summary for the activity strip, and the patches that
// make it undoable — or a refusal the agent can act on. `Text` is null exactly when `Refusal` is not.
internal readonly record struct WireframeEdit(string? Text, string Summary, string? Refusal, IReadOnlyList<WireframePatch> Patches)
{
    public static WireframeEdit Change(string text, string summary, params WireframePatch[] patches) =>
        new(text, summary, null, patches);

    public static WireframeEdit Refuse(string reason) => new(null, "", reason, []);
}

// AC-872: editing a wireframe one component at a time. A component is named by its stable id (AC-906) and the
// parsed tree supplies the structure, so this never guesses at indentation. Every operation ends at one gate: the
// result is parsed again, and a change leaving more unreadable lines than it found is refused.
internal static class WireframeComponentEditor
{
    public static WireframeEdit Apply(string source, WireframeComponentEdit edit)
    {
        var parsed = WireframeParser.Parse(source);
        var screens = parsed.Screens;
        if (screens.Count == 0 && edit.Kind is not (WireframeEditKind.AddScreen or WireframeEditKind.SetViewport))
        {
            return WireframeEdit.Refuse("This wireframe has no screen line to hang a component on — write the whole source with edit_wireframe first.");
        }

        var lines = source.ReplaceLineEndings("\n").Split('\n').ToList();
        var result = edit.Kind switch
        {
            WireframeEditKind.Add => _Add(screens, lines, edit),
            WireframeEditKind.AddScreen => _AddScreen(screens, lines, edit),
            WireframeEditKind.SetText => _SetText(screens, lines, edit),
            WireframeEditKind.Remove => _Remove(screens, lines, edit),
            WireframeEditKind.Move => _Move(screens, lines, edit),
            WireframeEditKind.ChangeType => _ChangeType(screens, lines, edit),
            WireframeEditKind.SetViewport => _SetViewport(lines, edit),
            _ => _SetModifier(screens, lines, edit),
        };

        if (result.Text is not { } text)
        {
            return result;
        }

        if (string.Equals(text, source, StringComparison.Ordinal))
        {
            return WireframeEdit.Refuse("That would leave the wireframe exactly as it is, so nothing was changed.");
        }

        // The one gate every operation passes through: a component keyword and a modifier the format does not have
        // both end here rather than in the operator's source box.
        var after = WireframeParser.Parse(text);
        return !after.HasScreens || after.Errors.Count > parsed.Errors.Count
            ? WireframeEdit.Refuse("That change would leave a line this wireframe cannot read, so nothing was changed — check the component keyword and its modifiers against the wireframe format.")
            : result;
    }

    // Puts back what one journaled edit replaced, against the source as it stands now: each patch's `After` is
    // searched for by content, nearest to where it landed, so an edit elsewhere in the document moving it down does
    // not put this one out of reach. Reverse order, so a move's insertion is undone before its removal is.
    public static string? Revert(string source, IReadOnlyList<WireframePatch> patches, out string reverted)
    {
        var lines = source.ReplaceLineEndings("\n").Split('\n').ToList();
        foreach (var patch in patches.Reverse())
        {
            if (patch.After.Count == 0)
            {
                lines.InsertRange(_InsertionPoint(lines, patch), patch.Before);
                continue;
            }

            var at = _Locate(lines, patch.After, patch.At);
            if (at < 0)
            {
                reverted = source;
                return "This edit can no longer be found in the wireframe.";
            }

            lines.RemoveRange(at, patch.After.Count);
            lines.InsertRange(at, patch.Before);
        }

        reverted = string.Join("\n", lines);
        if (WireframeParser.Parse(reverted).HasScreens)
        {
            return null;
        }

        reverted = source;
        return "Reverting this would leave an unreadable wireframe, so nothing was changed.";
    }

    private static WireframeEdit _Add(IReadOnlyList<WireframeNode> screens, List<string> lines, WireframeComponentEdit edit)
    {
        if (_Find(screens, edit.Parent) is not { } parent)
        {
            return WireframeEdit.Refuse(_NoSuchComponent(edit.Parent));
        }

        if (edit.Type is not { } written || !Enum.TryParse<WireframeNodeKind>(written.Trim(), ignoreCase: true, out var kind))
        {
            return WireframeEdit.Refuse($"\"{edit.Type}\" is not a component this format has — use one of: {_Keywords()}.");
        }

        if (!parent.IsContainer)
        {
            return WireframeEdit.Refuse($"A {_Keyword(parent)} carries no components of its own — name a container such as a row, column, group or list.");
        }

        var indent = parent.Children.Count > 0
            ? _IndentOf(lines[parent.Children[0].Line - 1])
            : _IndentOf(lines[parent.Line - 1]) + 2;

        var line = new string(' ', indent) + _Compose(kind, edit);
        var at = _ChildInsertionPoint(parent, edit.Position);
        lines.Insert(at, line);

        var summary = $"added {_Keyword(kind)}{_Quoted(edit.Text)}";
        return WireframeEdit.Change(string.Join("\n", lines), summary, new WireframePatch(at, _AnchorAbove(lines, at), [], [line]));
    }

    // AC-901: a whole screen beside the ones already there, at the left margin, with a blank line between it and its
    // neighbour — the canonical form the writer produces, so a document stays as readable as one written by hand.
    private static WireframeEdit _AddScreen(IReadOnlyList<WireframeNode> screens, List<string> lines, WireframeComponentEdit edit)
    {
        if (string.IsNullOrWhiteSpace(edit.Text))
        {
            return WireframeEdit.Refuse("Give the new screen a title — it is what names it in the overview.");
        }

        var line = $"screen {WireframeWriter.Quote(_Clean(edit.Text).Trim())}";
        var index = Math.Clamp(edit.Position ?? screens.Count, 0, screens.Count);
        var at = index == screens.Count ? lines.Count : screens[index].Line - 1;
        var block = index == screens.Count ? new List<string> { "", line } : [line, ""];
        lines.InsertRange(at, block);

        var summary = $"added screen \"{edit.Text.Trim()}\"";
        return WireframeEdit.Change(string.Join("\n", lines), summary, new WireframePatch(at, _AnchorAbove(lines, at), [], block));
    }

    // AC-915: the document's own viewport line, always at the very top — replaced in place if one is already there,
    // inserted with a blank line under it otherwise, the same shape _AddScreen writes a new screen block in.
    private static WireframeEdit _SetViewport(List<string> lines, WireframeComponentEdit edit)
    {
        var line = $"viewport {edit.Type}";
        var summary = $"set the viewport to {edit.Type}";

        if (lines.Count > 0 && _IsViewportLine(lines[0]))
        {
            var before = lines[0];
            lines[0] = line;
            return WireframeEdit.Change(string.Join("\n", lines), summary, new WireframePatch(0, null, [before], [line]));
        }

        var block = new List<string> { line, "" };
        lines.InsertRange(0, block);
        return WireframeEdit.Change(string.Join("\n", lines), summary, new WireframePatch(0, null, [], block));
    }

    private static bool _IsViewportLine(string line)
    {
        var trimmed = line.TrimStart(' ');
        return trimmed.StartsWith("viewport", StringComparison.Ordinal) && (trimmed.Length == 8 || trimmed[8] == ' ');
    }

    // Re-emits the one line through the writer with its text swapped, so the component keeps every modifier it had,
    // in the order and the quoting the operator wrote them in.
    private static WireframeEdit _SetText(IReadOnlyList<WireframeNode> screens, List<string> lines, WireframeComponentEdit edit)
    {
        if (_Find(screens, edit.Component) is not { } node)
        {
            return WireframeEdit.Refuse(_NoSuchComponent(edit.Component));
        }

        var at = node.Line - 1;
        var before = lines[at];
        var oldTitle = node.Text;
        node.Text = _Clean(edit.Text ?? "");
        lines[at] = WireframeWriter.Line(node, _IndentOf(before));

        var patches = new List<WireframePatch> { new(at, _AnchorAbove(lines, at), [before], [lines[at]]) };

        // AC-902 AC4: renaming a screen carries every `goto:` that pointed at its old title along with it, in the
        // same undoable step — the alternative is a wall of refusals the next time anyone touches that screen.
        if (screens.Contains(node) && !string.IsNullOrEmpty(oldTitle))
        {
            foreach (var screen in screens)
            {
                _RewriteGotoReferences(screen, oldTitle, node.Text, lines, patches);
            }
        }

        var movedFlows = patches.Count - 1;
        var summary = $"set the {_Keyword(node.Kind)} on line {node.Line} to \"{node.Text}\""
            + (movedFlows == 0 ? "" : $" — {movedFlows} flow{(movedFlows == 1 ? "" : "s")} to it followed");
        return WireframeEdit.Change(string.Join("\n", lines), summary, patches.ToArray());
    }

    private static void _RewriteGotoReferences(WireframeNode node, string from, string to, List<string> lines, List<WireframePatch> patches)
    {
        var index = node.Modifiers.FindIndex(modifier => modifier.Name == WireframeModifierName.Goto && modifier.Value == from);
        if (index >= 0)
        {
            var at = node.Line - 1;
            var before = lines[at];
            node.Modifiers[index] = node.Modifiers[index] with { Value = to, IsQuoted = true };
            lines[at] = WireframeWriter.Line(node, _IndentOf(before));
            patches.Add(new WireframePatch(at, _AnchorAbove(lines, at), [before], [lines[at]]));
        }

        foreach (var child in node.Children)
        {
            _RewriteGotoReferences(child, from, to, lines, patches);
        }
    }

    private static WireframeEdit _Remove(IReadOnlyList<WireframeNode> screens, List<string> lines, WireframeComponentEdit edit)
    {
        if (_Find(screens, edit.Component) is not { } node)
        {
            return WireframeEdit.Refuse(_NoSuchComponent(edit.Component));
        }

        // AC-901: a screen goes the way any other component does, as long as it is not the only one left — a
        // document without a screen is not a wireframe any more, and edit_wireframe is what replaces the whole thing.
        if (screens.Contains(node) && screens.Count == 1)
        {
            return WireframeEdit.Refuse("This is the wireframe's only screen, so it cannot be removed — add another screen first, or replace the whole source with edit_wireframe.");
        }

        // AC-902 AC5: unlike a rename, a removal has nowhere to move the flow to — stripping the goto: silently
        // would delete work nobody named, so this is refused instead, naming the screen and what still points at it.
        if (screens.Contains(node) && node.Text is { } title)
        {
            var referrers = screens.SelectMany(screen => _GotoReferencesTo(screen, title)).ToList();
            if (referrers.Count > 0)
            {
                var names = string.Join(", ", referrers.Select(referrer => $"{_Keyword(referrer)}{_Quoted(referrer.Text)}"));
                return WireframeEdit.Refuse($"{_Keyword(node)}{_Quoted(node.Text)} still has a flow pointing to it from {names} — clear those goto: modifiers first.");
            }
        }

        var at = node.Line - 1;
        var block = lines.GetRange(at, _LastLine(node) - node.Line + 1);
        var anchor = _AnchorAbove(lines, at);
        lines.RemoveRange(at, block.Count);

        var nested = block.Count - 1;
        var summary = nested == 0
            ? $"removed {_Keyword(node.Kind)}{_Quoted(node.Text)}"
            : $"removed {_Keyword(node.Kind)}{_Quoted(node.Text)} and the {nested} component{(nested == 1 ? "" : "s")} inside it";
        return WireframeEdit.Change(string.Join("\n", lines), summary, new WireframePatch(at, anchor, block, []));
    }

    // A move is a removal and an insertion, journaled as both, so undoing it takes the block out of where it went
    // before putting it back where it was — the order the two patches are replayed in.
    private static WireframeEdit _Move(IReadOnlyList<WireframeNode> screens, List<string> lines, WireframeComponentEdit edit)
    {
        if (_Find(screens, edit.Component) is not { } node)
        {
            return WireframeEdit.Refuse(_NoSuchComponent(edit.Component));
        }

        if (_Find(screens, edit.Parent) is not { } parent)
        {
            return WireframeEdit.Refuse(_NoSuchComponent(edit.Parent));
        }

        if (screens.Contains(node))
        {
            return WireframeEdit.Refuse("A screen stands at the left margin of its own, so it cannot be moved into a container — add_screen and remove_component are how screens come and go.");
        }

        if (!parent.IsContainer)
        {
            return WireframeEdit.Refuse($"A {_Keyword(parent)} carries no components of its own — name a container such as a row, column, group or list.");
        }

        if (node == parent || _Find(node, edit.Parent) is not null)
        {
            return WireframeEdit.Refuse("A component cannot be moved inside itself — name a container it is not already part of.");
        }

        var from = node.Line - 1;
        var block = lines.GetRange(from, _LastLine(node) - node.Line + 1);
        var fromAnchor = _AnchorAbove(lines, from);

        var indent = parent.Children.FirstOrDefault(child => child != node) is { } sibling
            ? _IndentOf(lines[sibling.Line - 1])
            : _IndentOf(lines[parent.Line - 1]) + 2;
        var moved = _Reindent(block, indent - _IndentOf(block[0]));

        // Both points are read off the tree, so they are in the source's own line numbers; only the insertion needs
        // correcting for the lines the removal took out above it.
        var to = _ChildInsertionPoint(parent, edit.Position);
        lines.RemoveRange(from, block.Count);
        to = to > from ? to - block.Count : to;
        lines.InsertRange(to, moved);

        var summary = $"moved {_Keyword(node.Kind)}{_Quoted(node.Text)} into the {_Keyword(parent.Kind)} on line {parent.Line}";
        return WireframeEdit.Change(
            string.Join("\n", lines),
            summary,
            new WireframePatch(from, fromAnchor, block, []),
            new WireframePatch(to, _AnchorAbove(lines, to), [], moved));
    }

    // AC-905: the properties panel and set_component_modifier both land here — a flag or a value-bearing modifier
    // replaced in place if already on the line, appended if not, or dropped when the caller clears it. Applicability
    // comes from WireframeModifierRules, not a second copy of it.
    private static WireframeEdit _SetModifier(IReadOnlyList<WireframeNode> screens, List<string> lines, WireframeComponentEdit edit)
    {
        if (_Find(screens, edit.Component) is not { } node)
        {
            return WireframeEdit.Refuse(_NoSuchComponent(edit.Component));
        }

        if (edit.ModifierName is not { } name)
        {
            return WireframeEdit.Refuse("Name the modifier to set — one of: primary, selected, checked, disabled, w, h, align, value, goto.");
        }

        var parentKind = _ParentOf(screens, node)?.Kind;
        if (!WireframeModifierRules.Applies(node.Kind, parentKind, name))
        {
            return WireframeEdit.Refuse($"{_Keyword(name)} has no meaning on a {_Keyword(node)} here, so nothing was changed.");
        }

        var at = node.Line - 1;
        var before = lines[at];
        var index = node.Modifiers.FindIndex(modifier => modifier.Name == name);
        var setting = edit.Kind == WireframeEditKind.ToggleModifier ? edit.ModifierOn : !string.IsNullOrEmpty(edit.ModifierValue);

        if (setting)
        {
            var modifier = new WireframeModifier(
                name,
                edit.Kind == WireframeEditKind.ToggleModifier ? null : _Clean(edit.ModifierValue!),
                edit.ModifierQuoted);
            if (index >= 0)
            {
                node.Modifiers[index] = modifier;
            }
            else
            {
                node.Modifiers.Add(modifier);
            }
        }
        else if (index >= 0)
        {
            node.Modifiers.RemoveAt(index);
        }

        lines[at] = WireframeWriter.Line(node, _IndentOf(before));
        var summary = setting
            ? $"set {_Keyword(name)} on the {_Keyword(node)} on line {node.Line}"
            : $"cleared {_Keyword(name)} on the {_Keyword(node)} on line {node.Line}";
        return WireframeEdit.Change(string.Join("\n", lines), summary, new WireframePatch(at, _AnchorAbove(lines, at), [before], [lines[at]]));
    }

    // AC-905: keeps the line's place, id, text and modifiers — only the keyword changes. Refused rather than
    // silently dropping children a widget cannot carry; the operator moves or removes them first.
    private static WireframeEdit _ChangeType(IReadOnlyList<WireframeNode> screens, List<string> lines, WireframeComponentEdit edit)
    {
        if (_Find(screens, edit.Component) is not { } node)
        {
            return WireframeEdit.Refuse(_NoSuchComponent(edit.Component));
        }

        if (screens.Contains(node))
        {
            return WireframeEdit.Refuse("A screen line is a screen of this wireframe, so its type cannot be changed — remove it instead, or add another screen beside it.");
        }

        if (edit.Type is not { } written || !Enum.TryParse<WireframeNodeKind>(written.Trim(), ignoreCase: true, out var kind))
        {
            return WireframeEdit.Refuse($"\"{edit.Type}\" is not a component this format has — use one of: {_Keywords()}.");
        }

        if (!new WireframeNode(kind, 0).IsContainer && node.Children.Count > 0)
        {
            return WireframeEdit.Refuse(
                $"A {_Keyword(kind)} carries no components of its own, but {_Keyword(node)}{_Quoted(node.Text)} has {node.Children.Count} inside it — move or remove them first.");
        }

        var at = node.Line - 1;
        var before = lines[at];
        var rewritten = new WireframeNode(kind, node.Line, node.Text) { Id = node.Id };
        rewritten.Modifiers.AddRange(node.Modifiers);
        lines[at] = WireframeWriter.Line(rewritten, _IndentOf(before));

        var summary = $"changed the {_Keyword(node)}{_Quoted(node.Text)} on line {node.Line} to a {_Keyword(kind)}";
        return WireframeEdit.Change(string.Join("\n", lines), summary, new WireframePatch(at, _AnchorAbove(lines, at), [before], [lines[at]]));
    }

    // Where a new child of `parent` starts, as a 0-based line index: before the child at `position`, or after
    // everything already inside the last one. A parent with no children yet takes it on the line straight below.
    private static int _ChildInsertionPoint(WireframeNode parent, int? position)
    {
        if (parent.Children.Count == 0)
        {
            return parent.Line;
        }

        var index = Math.Clamp(position ?? parent.Children.Count, 0, parent.Children.Count);
        return index == parent.Children.Count
            ? _LastLine(parent.Children[^1])
            : parent.Children[index].Line - 1;
    }

    // A pure insertion has no `After` to search for, so it goes back below the line it used to sit under. Falling
    // back on the recorded index keeps a revert at the very top of the document working.
    private static int _InsertionPoint(List<string> lines, WireframePatch patch)
    {
        if (patch.Anchor is null)
        {
            return Math.Clamp(patch.At, 0, lines.Count);
        }

        var above = _Locate(lines, [patch.Anchor], patch.At - 1);
        return above < 0 ? Math.Clamp(patch.At, 0, lines.Count) : above + 1;
    }

    // Every place `run` reads back exactly, the one nearest `near` winning — two identical components are a real
    // possibility in a wireframe, so which one an edit touched is decided by where it was, not by which came first.
    private static int _Locate(List<string> lines, IReadOnlyList<string> run, int near)
    {
        var best = -1;
        for (var start = 0; start + run.Count <= lines.Count; start++)
        {
            var matches = true;
            for (var offset = 0; offset < run.Count && matches; offset++)
            {
                matches = string.Equals(lines[start + offset], run[offset], StringComparison.Ordinal);
            }

            if (matches && (best < 0 || Math.Abs(start - near) < Math.Abs(best - near)))
            {
                best = start;
            }
        }

        return best;
    }

    private static string? _AnchorAbove(List<string> lines, int at) => at > 0 && at <= lines.Count ? lines[at - 1] : null;

    private static List<string> _Reindent(List<string> block, int delta) =>
        delta == 0
            ? block
            : block.Select(line => delta > 0
                ? new string(' ', delta) + line
                : line[Math.Min(_IndentOf(line), -delta)..]).ToList();

    private static string _Compose(WireframeNodeKind kind, WireframeComponentEdit edit)
    {
        var line = _Keyword(kind);
        if (!string.IsNullOrEmpty(edit.Text))
        {
            line += " " + WireframeWriter.Quote(_Clean(edit.Text));
        }

        return string.IsNullOrWhiteSpace(edit.Modifiers) ? line : $"{line} {_Clean(edit.Modifiers).Trim()}";
    }

    private static IEnumerable<WireframeNode> _GotoReferencesTo(WireframeNode node, string title)
    {
        if (node.ValueOf(WireframeModifierName.Goto) == title)
        {
            yield return node;
        }

        foreach (var found in node.Children.SelectMany(child => _GotoReferencesTo(child, title)))
        {
            yield return found;
        }
    }

    private static WireframeNode? _Find(IReadOnlyList<WireframeNode> screens, string id) =>
        screens.Select(screen => _Find(screen, id)).FirstOrDefault(found => found is not null);

    private static WireframeNode? _Find(WireframeNode node, string id) =>
        node.Id == id ? node : node.Children.Select(child => _Find(child, id)).FirstOrDefault(found => found is not null);

    // AC-905: null for a screen itself — a screen line has no parent, and `w:`/`h:` never apply to it either.
    private static WireframeNode? _ParentOf(IReadOnlyList<WireframeNode> screens, WireframeNode target) =>
        screens.Select(screen => _ParentOf(screen, target)).FirstOrDefault(found => found is not null);

    private static WireframeNode? _ParentOf(WireframeNode node, WireframeNode target) =>
        node.Children.Contains(target) ? node : node.Children.Select(child => _ParentOf(child, target)).FirstOrDefault(found => found is not null);

    private static int _LastLine(WireframeNode node) =>
        node.Children.Count == 0 ? node.Line : Math.Max(node.Line, node.Children.Max(_LastLine));

    private static int _IndentOf(string line) => line.Length - line.TrimStart(' ').Length;

    private static string _Keyword(WireframeNodeKind kind) => kind.ToString().ToLowerInvariant();

    private static string _Keyword(WireframeNode node) => _Keyword(node.Kind);

    private static string _Keyword(WireframeModifierName name) => name.ToString().ToLowerInvariant();

    private static string _Keywords() =>
        string.Join(", ", Enum.GetValues<WireframeNodeKind>().Select(_Keyword));

    private static string _Quoted(string? text) => string.IsNullOrEmpty(text) ? "" : $" \"{text}\"";

    // Text and modifiers go into the source verbatim, so anything that would end the line is folded away before it
    // can split one component into two.
    private static string _Clean(string value) =>
        new(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray());

    // AC-906: an id that has gone is a refusal, never a near miss silently applied to whatever moved into its place.
    private static string _NoSuchComponent(string id) =>
        string.IsNullOrEmpty(id)
            ? "Name the component you mean by its id — read_wireframe lists one per component."
            : $"This wireframe has no component with id \"{id}\" — it may have been removed. Read it again for the ids as they now stand.";
}
