# The wireframe format

*AC-871, sub of AC-864. This is the language an agent writes and the operator reads in the source box — not the
surface around it. The parser, writer and renderer live in `plugins-dev/Cockpit.Plugin.Diagram/Wireframe/`.*

A wireframe is plain text: one component per line, nesting by indentation, no coordinates. Placement comes from
Avalonia's own layout, the same contract Mermaid has on the diagram surface — which is why an inserted field
pushes what is under it instead of leaving a hole, and why writing a screen is one call rather than forty.

Nothing in the format is executable. There is no expression, no binding, no type name, no include: a document an
agent hands over can be read wrongly, but it cannot be run.

## A line

```
type "text" modifier modifier:value #id
```

- **type** — one keyword, lowercase, from the tables below.
- **text** — optional, always double-quoted, and it comes directly after the type. A backslash escapes the next
  character, so `label "Zeg \"hallo\""` is one label with quotes in it.
- **modifiers** — zero or more, in any order. A flag stands alone (`primary`); the rest take a value after a colon
  (`w:2`, `align:right`, `value:"Raymond"`). A value only needs quotes when it contains a space, and quoting is
  preserved when the text is written back.
- **id** — optional, at most one, written last (`#save-btn`). See below.

## Ids

*AC-906.* An id is a component's name, and it is the only handle that survives editing: a line number moves the
moment anyone inserts a line above it, so an agent that read line 12 and then writes to line 12 can hit a different
component entirely. An id does not move. It is written as `#` followed by letters, digits, `-` and `_`, and the
same id may not appear twice in one document — that line is refused, because an ambiguous name is worse than none.

You do not have to write ids yourself. A wireframe nobody points at stays plain; ids appear the moment something
needs to name a component — an agent reading the surface, or you clicking a component on it — and the ones minted
that way are `#c1`, `#c2`, … You can rename them to something you recognise (`#save-btn`), and they are saved with
the file, so a note or a flow can be hung on a component rather than on a line.

## Indentation

Indent with spaces to nest a component under the one above it. Two spaces per level is the canonical form — it is
what the writer produces — but any consistent step works, so four spaces is fine too. A line that steps back out
has to line up exactly with a level that is still open; one that lines up with nothing is an error rather than a
guess. Tabs are refused.

A document is one `screen` at the left margin with everything else under it. Blank lines are ignored.

## Containers

| Keyword | What it lays out |
| --- | --- |
| `screen` | The whole thing. Its text is the screen title. Exactly one per document, at the left margin. |
| `row` | Children side by side, left to right. |
| `column` | Children stacked, top to bottom. |
| `group` | A framed block; its text is the caption above the frame's contents. |
| `tabs` | A tab strip. Its `tab` children each become a tab; the one marked `selected` is the open one, otherwise the first. |
| `tab` | One tab's contents, stacked. |
| `nav` | A menu rail. Its `item` children are the entries. |
| `list` | A list box. `item` children are its rows; without any, it draws placeholder rows so it still reads as a list. |
| `table` | A table. `item` children are its column headings; without any, the header band is drawn blank. |

## Widgets

| Keyword | Drawn as | Text means |
| --- | --- | --- |
| `label` | Plain text | the text |
| `button` | A button | its caption |
| `input` | A labelled field box | the field label; `value:` fills the box |
| `select` | A field box with a chevron | the field label; `value:` fills the box |
| `checkbox` | A square plus text | the text beside it |
| `radio` | A circle plus text | the text beside it |
| `item` | A row inside a `nav`, `list` or `table` | the row's text |
| `image` | A crossed placeholder box | an optional caption in the middle |
| `divider` | A horizontal rule | — |
| `space` | Empty room | — |

A widget carries no children; an indented line under one is an error, because that is nearly always a mis-indent.

## Modifiers

| Modifier | Applies to | Effect |
| --- | --- | --- |
| `primary` | `button` | Drawn filled instead of outlined. |
| `selected` | `item`, `tab` | The highlighted entry, or the open tab. |
| `checked` | `checkbox`, `radio` | Drawn filled. |
| `disabled` | anything | Drawn dimmed. |
| `w:N` | a child of a `row` | Flex **weight**, never pixels: `w:1` beside `w:3` gives a quarter and three quarters of the row. A child without `w:` takes the width it needs. |
| `h:N` | a child of a `column`, `group`, `screen`, `nav`, `tab` | The same, vertically. Use it on the one thing that should absorb the leftover height. |
| `align:` | anything | `left`, `center` or `right`. |
| `value:` | `input`, `select` | The value shown inside the box. |

There is no size in pixels, no colour and no font here, on purpose. A wireframe is drawn in greys with one font so
it reads as a sketch; the moment it can carry a product colour it starts making promises it cannot keep.

## When a line cannot be read

The parser never fails silently and never throws. It returns whatever it could read plus a list of errors, each
with the line number it belongs to: an unknown keyword, an unknown modifier, a weight that is not a positive
number, an unclosed quote, indentation that lines up with nothing. Only the bad line is dropped — the rest of the
screen still parses and still renders.

## Example

```
screen "Instellingen"
  row h:1
    column w:1
      nav
        item "Algemeen" selected
        item "Account"
    column w:3
      group "Profiel"
        input "Profielnaam" value:"Raymond"
        input "E-mailadres"
      group "Meldingen"
        checkbox "Bureaubladmelding" checked
        select "Waarschuwingsgeluid"
      space
      row align:right
        button "Annuleren"
        button "Opslaan" primary #save-btn
```

## Round trip

Source → tree → controls → source gives the same text back, character for character, for any source written in
the canonical form (which puts the id last on the line). Every rendered control carries the node it came from
(`WireframeSource.Node`), so a control on screen always knows which component it stands for — including a tab that
is currently hidden, which is rendered rather than skipped precisely so no line loses its control.
