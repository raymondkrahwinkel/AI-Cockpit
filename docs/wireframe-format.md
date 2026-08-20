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

## Screens

*AC-901.* A document holds **one or more** screens: every `screen` line at the left margin starts a new one, and
everything indented under it belongs to that screen. That is how a wireframe says what a site is — the layout of the
pages *within* it — rather than one page per file. Blank lines are ignored, and the canonical form the writer
produces puts one blank line between screens.

```
screen "Aanmelden"
  input "E-mailadres"
  button "Aanmelden" primary

screen "Wachtwoord vergeten"
  input "E-mailadres"
  button "Verstuur" primary
```

Ids are unique across the whole document, not per screen, so a component you name is never one of the same name on
another screen. A source written before this — a single screen with everything under it — is exactly what it always
was and needs no change.

Two screens may share a title; `goto:` naming one is then ambiguous, and refused the same way a duplicate id is
(above). Renaming a screen — from the properties panel, inline, or with `set_component_text` — carries every
`goto:` that pointed at the old title to the new one, in the same undoable step. Removing a screen that a `goto:`
still points at is refused instead: there is nowhere for the flow to move to, so the source stays as it was until
those `goto:` modifiers are cleared or repointed.

The window shows a document in two ways. The **overview** lays every screen out as a board with its name above it;
**zoomed in**, one screen fills the canvas. Double-click a board to step into it, the *Overzicht* button to come back
out, and the zoom level does the same thing: past the level at which one board fills the window you are inside that
screen, and back below the level at which the whole overview fits you are looking at the set again. A document with a
single screen is always shown zoomed in.

## Containers

| Keyword | What it lays out |
| --- | --- |
| `screen` | One screen of the document. Its text is the screen title. At the left margin; a document may hold several. |
| `row` | Children side by side, left to right. |
| `column` | Children stacked, top to bottom. |
| `group` | A framed block; its text is the caption above the frame's contents. |
| `header` | A band across the top. Its text is the product or page name on the left; its children sit side by side beside it. |
| `footer` | The same band at the bottom, its text set smaller. |
| `sidebar` | A side region, children stacked, tinted and ruled off from what it sits beside. Its text is a caption above them. |
| `main` | The content region beside a `sidebar`. It draws nothing of its own — it says which part of the screen is the content. |
| `card` | A tile. Its text is the title *inside* the frame, which is the whole visual difference with `group`. |
| `modal` | A dialog. Written directly under `screen` it covers the screen, dimmed behind it, rather than taking a band of its own; anywhere else it is drawn where it stands. Its text is the dialog title. |
| `tabs` | A tab strip. Its `tab` children each become a tab; the one marked `selected` is the open one, otherwise the first. |
| `tab` | One tab's contents, stacked. |
| `nav` | A menu rail. Its `item` children are the entries. |
| `menu` | A dropdown, drawn open. Its text is the trigger above the panel; its `item` children are the entries. |
| `breadcrumb` | A trail of `item` children with `›` between them. |
| `stepper` | Numbered steps from its `item` children. The `selected` one is where you are; everything up to it is drawn as done. |
| `list` | A list box. `item` children are its rows; without any, it draws placeholder rows so it still reads as a list. |
| `table` | A table. `item` children are its column headings; without any, the header band is drawn blank. |

## Widgets

| Keyword | Drawn as | Text means |
| --- | --- | --- |
| `label` | Plain text | the text — and without any, two placeholder lines, which is how a screen is sketched before its copy exists |
| `button` | A button | its caption |
| `input` | A labelled field box | the field label; `value:` fills the box |
| `textarea` | A tall field box | the field label; `value:` fills it, and without one it draws placeholder lines |
| `search` | A field box with a magnifier | what the box says when it is empty; `value:` is what has been typed |
| `select` | A field box with a chevron | the field label; `value:` fills the box |
| `checkbox` | A square plus text | the text beside it |
| `radio` | A circle plus text | the text beside it |
| `toggle` | A switch plus text | the text beside it; `checked` throws it |
| `slider` | A track with a knob | an optional label above; `value:` 0–100 is where the knob sits |
| `item` | A row inside a `nav`, `menu`, `list`, `table`, `breadcrumb` or `stepper` | the row's text, or a placeholder line without any |
| `image` | A crossed placeholder box | an optional caption in the middle |
| `avatar` | A circle | an optional name beside it |
| `icon` | A small filled square | an optional label beside it |
| `badge` | A pill | `value:` if there is one, otherwise the text; `primary` draws it in the accent |
| `progress` | A bar | an optional label above; `value:` 0–100 is how full it is |
| `pagination` | Page boxes between arrows | — ; `value:` is the page you are on |
| `divider` | A horizontal rule | — |
| `space` | Empty room | — |

A widget carries no children; an indented line under one is an error, because that is nearly always a mis-indent.

The vocabulary stops here on purpose. It covers what an ordinary web or app screen is made of; anything rarer is
better said with the components that are here than by growing the list until nobody can hold it in their head.

## Modifiers

| Modifier | Applies to | Effect |
| --- | --- | --- |
| `primary` | `button`, `badge` | Drawn in the accent instead of outlined — the one thing the screen wants you to do. |
| `selected` | `item`, `tab` | The highlighted entry, the open tab, or the step you are on. |
| `checked` | `checkbox`, `radio`, `toggle` | Drawn filled, or thrown. |
| `disabled` | anything | Drawn dimmed. |
| `w:N` | a child of a `row`, `header` or `footer` | Flex **weight**, never pixels: `w:1` beside `w:3` gives a quarter and three quarters of the row. A child without `w:` takes the width it needs. |
| `h:N` | a child of a `column`, `group`, `card`, `screen`, `nav`, `sidebar`, `main`, `tab` | The same, vertically. Use it on the one thing that should absorb the leftover height. |
| `align:` | anything | `left`, `center` or `right`. |
| `value:` | `input`, `textarea`, `search`, `select`, `badge` | The value shown inside the box or the pill. |
| `value:N` | `slider`, `progress` | How full, 0–100. |
| `value:N` | `pagination` | The page you are on. |
| `goto:"Screen"` | `button`, `item`, `label`, `card`, `image`, `icon`, `avatar`, `badge`, `row` | A flow to another screen by its title. Drawn as an arrow between boards in the overview, and as a clickable marker zoomed in. A title that names no screen, or more than one, is a parse error with a line number rather than a silent no-op. |

There is no size in pixels and no font here, on purpose, and there is exactly one colour: the accent that `primary`
draws in. Everything else is grey, so the drawing reads as a sketch instead of making promises about a product that
does not exist yet.

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

And a screen that uses the rest of the vocabulary — a catalogue with a header, a filter sidebar, a card grid,
pagination and a dialog over it:

```
screen "Catalogue"
  header "Northwind"
    search "Search products" w:3
    icon "Cart"
    badge value:"3" primary
    avatar "Raymond"
  breadcrumb
    item "Home"
    item "Bikes" selected
  row h:1
    sidebar "Filters" w:1
      checkbox "In stock" checked
      toggle "Free shipping" checked
      slider "Price" value:60
    main w:4
      row h:1
        card "Trailhead 5" w:1
          image
          label "1.299"
          progress "In stock" value:70
          button "Add to cart" primary
        card w:1
          image
          label
      pagination value:2 align:center
  modal "Add to cart"
    stepper
      item "Options" selected
      item "Payment"
    textarea "Note for the courier"
    row align:right
      button "Cancel"
      button "Continue" primary
```

## Round trip

Source → tree → controls → source gives the same text back, character for character, for any source written in
the canonical form (which puts the id last on the line and one blank line between screens). Every rendered control carries the node it came from
(`WireframeSource.Node`), so a control on screen always knows which component it stands for — including a tab that
is currently hidden, which is rendered rather than skipped precisely so no line loses its control.
