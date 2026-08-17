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

// AC-872: editing a wireframe one component at a time. A component is named by its line number — the format has
// no ids — and the parsed tree supplies the structure, so this never guesses at indentation. Every operation ends
// at one gate: the result is parsed again, and a change leaving more unreadable lines than it found is refused.
internal static class WireframeComponentEditor
{
    public static WireframeEdit Apply(string source, WireframeComponentEdit edit)
    {
        var parsed = WireframeParser.Parse(source);
        if (parsed.Root is not { } root)
        {
            return WireframeEdit.Refuse("This wireframe has no screen line to hang a component on — write the whole source with edit_wireframe first.");
        }

        var lines = source.ReplaceLineEndings("\n").Split('\n').ToList();
        var result = edit.Kind switch
        {
            WireframeEditKind.Add => _Add(root, lines, edit),
            WireframeEditKind.SetText => _SetText(root, lines, edit),
            WireframeEditKind.Remove => _Remove(root, lines, edit),
            _ => _Move(root, lines, edit),
        };

        if (result.Text is not { } text)
        {
            return result;
        }

        if (string.Equals(text, source, StringComparison.Ordinal))
        {
            return WireframeEdit.Refuse("That would leave the wireframe exactly as it is, so nothing was changed.");
        }

        // The one gate every operation passes through: a mis-aimed line number and a modifier the format does not
        // have both end here rather than in the operator's source box.
        var after = WireframeParser.Parse(text);
        return after.Root is null || after.Errors.Count > parsed.Errors.Count
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
                return "Deze bewerking is niet meer terug te vinden in het wireframe.";
            }

            lines.RemoveRange(at, patch.After.Count);
            lines.InsertRange(at, patch.Before);
        }

        reverted = string.Join("\n", lines);
        if (WireframeParser.Parse(reverted).Root is not null)
        {
            return null;
        }

        reverted = source;
        return "Terugdraaien zou geen leesbaar wireframe overlaten, dus er is niets veranderd.";
    }

    private static WireframeEdit _Add(WireframeNode root, List<string> lines, WireframeComponentEdit edit)
    {
        if (_Find(root, edit.Parent) is not { } parent)
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

    // Re-emits the one line through the writer with its text swapped, so the component keeps every modifier it had,
    // in the order and the quoting the operator wrote them in.
    private static WireframeEdit _SetText(WireframeNode root, List<string> lines, WireframeComponentEdit edit)
    {
        if (_Find(root, edit.Component) is not { } node)
        {
            return WireframeEdit.Refuse(_NoSuchComponent(edit.Component));
        }

        var at = node.Line - 1;
        var before = lines[at];
        node.Text = _Clean(edit.Text ?? "");
        lines[at] = WireframeWriter.Line(node, _IndentOf(before));

        var summary = $"set the {_Keyword(node.Kind)} on line {node.Line} to \"{node.Text}\"";
        return WireframeEdit.Change(string.Join("\n", lines), summary, new WireframePatch(at, _AnchorAbove(lines, at), [before], [lines[at]]));
    }

    private static WireframeEdit _Remove(WireframeNode root, List<string> lines, WireframeComponentEdit edit)
    {
        if (_Find(root, edit.Component) is not { } node)
        {
            return WireframeEdit.Refuse(_NoSuchComponent(edit.Component));
        }

        if (node == root)
        {
            return WireframeEdit.Refuse("The screen line is the wireframe itself, so it cannot be removed — edit_wireframe replaces the whole thing.");
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
    private static WireframeEdit _Move(WireframeNode root, List<string> lines, WireframeComponentEdit edit)
    {
        if (_Find(root, edit.Component) is not { } node)
        {
            return WireframeEdit.Refuse(_NoSuchComponent(edit.Component));
        }

        if (_Find(root, edit.Parent) is not { } parent)
        {
            return WireframeEdit.Refuse(_NoSuchComponent(edit.Parent));
        }

        if (node == root)
        {
            return WireframeEdit.Refuse("The screen line is the wireframe itself, so there is nowhere to move it to.");
        }

        if (!parent.IsContainer)
        {
            return WireframeEdit.Refuse($"A {_Keyword(parent)} carries no components of its own — name a container such as a row, column, group or list.");
        }

        if (node == parent || _Find(node, parent.Line) is not null)
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

    private static WireframeNode? _Find(WireframeNode node, int line) =>
        node.Line == line ? node : node.Children.Select(child => _Find(child, line)).FirstOrDefault(found => found is not null);

    private static int _LastLine(WireframeNode node) =>
        node.Children.Count == 0 ? node.Line : Math.Max(node.Line, node.Children.Max(_LastLine));

    private static int _IndentOf(string line) => line.Length - line.TrimStart(' ').Length;

    private static string _Keyword(WireframeNodeKind kind) => kind.ToString().ToLowerInvariant();

    private static string _Keyword(WireframeNode node) => _Keyword(node.Kind);

    private static string _Keywords() =>
        string.Join(", ", Enum.GetValues<WireframeNodeKind>().Select(_Keyword));

    private static string _Quoted(string? text) => string.IsNullOrEmpty(text) ? "" : $" \"{text}\"";

    // Text and modifiers go into the source verbatim, so anything that would end the line is folded away before it
    // can split one component into two.
    private static string _Clean(string value) =>
        new(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray());

    private static string _NoSuchComponent(int line) =>
        $"There is no component on line {line} — read_wireframe shows the source with a line number per component.";
}
