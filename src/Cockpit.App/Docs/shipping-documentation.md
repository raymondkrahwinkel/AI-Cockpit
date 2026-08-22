---
title: Shipping documentation
category: extending
order: 10
summary: How a plugin carries its own pages, and how it points at one from its own UI.
icon: 📗
---

Your plugin can carry the pages you are reading right now. They travel inside your assembly, so they work
with no connection and can never describe a version the operator does not have.

## There is no API for shipping pages {#shipping}

Put markdown in a `Docs` folder beside your project file and reference the SDK. Its MSBuild targets embed
everything under that folder; nothing is registered, and nothing is declared.

```
Cockpit.Plugin.Acme/
  plugin.json
  Docs/
    setup.md
    images/gateway-intents.png
```

A plugin that ships no documentation is unaffected in every way — no member became required, and no
constructor changed shape.

If you embed the files by hand instead, you must write `WithCulture="false"` on the item. MSBuild reads
`setup.nl.md` as a culture-specific resource and routes it into an `nl` satellite assembly where the app
never looks, so the translation disappears from a build that reports success.

## Front matter {#front-matter}

Every file opens with a `---` block. Only `title` is worth always writing.

| Key | Meaning |
| --- | --- |
| `title` | What the navigation and the page header show. Defaults to the file name. |
| `order` | Position within your own branch, ascending. |
| `summary` | One line on the overview card. |
| `icon` | A single emoji beside the title. |
| `category` | Ignored for a plugin. Your pages always land under **Plugins**; the four top-level categories belong to the app. |

Unknown keys are ignored, so a key added later will not break a file written today.

## Why you write section ids by hand {#section-ids}

A heading is only linkable when it declares one:

```markdown
## The Message Content Intent {#message-content-intent}
```

Ids are deliberately not derived from the heading text. An anchor that follows the wording breaks the moment
somebody rewrites the heading — silently, everywhere that linked to it — and it would break again in every
translation, so a link would work in exactly one language. Write the id once and the heading can be reworded
freely.

## The `?`, and why the SDK draws it {#help-hint}

Ask the host for the mark rather than drawing your own:

```csharp
// Behind a field label or in a section heading: a bare mark.
row.Children.Add(host.CreateHelpHint("setup", "bot-token"));

// In a sentence or an error message, where a floating mark has nothing to sit beside.
panel.Children.Add(host.CreateHelpHint("setup", "bot-token", "Why is this needed?"));
```

Twenty-seven plugins each drawing their own question mark is twenty-seven icons, sizes and behaviours for the
same promise. Here you name the target and get the app's own affordance: hovering says where it goes, clicking
opens this window on that section, and arriving mid-article shows the banner that says where you came from.

**It hides itself when its target does not exist.** A question mark that opens nothing is worse than no
question mark, so you can hand one over unconditionally — an uninstalled plugin or a renamed section leaves no
mark behind rather than a promise that breaks when taken up.

`OpenHelp` makes the same jump from a control you drew yourself, and `HasHelp` answers whether a reference is
worth writing at all — for deciding how to word an error message, say.

### How the article name is resolved {#resolution}

The article is looked up against your own pages first and then as written. So `"setup"` means your page and
`"core-concepts#plugin"` reaches one of the app's, without you repeating your own plugin id — which would be a
second place your name is written down, free to drift from the manifest.

All three members need `abstractionsVersion` 2 and `minHostVersion` `0.28.0`. Shipping pages on its own needs
neither: an older host simply does not read them.

## Pictures {#pictures}

Reference them with a relative path, and they resolve to a resource shipped beside the page:

```markdown
![The switch is at the bottom of the Bot page](images/gateway-intents.png)
```

An `https://` reference is refused rather than fetched, and the reader is shown that it was. A picture pulled
from your server would tell you the operator's address the moment they opened the page, without them doing
anything at all. Ship it in the package or do not use it.

A `foo.dark.png` beside `foo.png` is used in the dark theme when it exists. Optional — one image is the
ordinary case.

Documentation is downloaded and stored with every install of your plugin, so a screenshot budget is a real
one. Crop to the control being described rather than the whole window.

## Translations {#translations}

`setup.md` is the default language and stays valid forever. A translation sits beside it as `setup.nl.md` —
the language code is its own dot-separated segment, because article names contain dashes of their own and
`getting-started-en.md` would be ambiguous.

The article id stays `setup` in every language, and so do the section ids. That is what lets one deep link
land in the same place whichever language is shown. A page with no translation falls back to the default one
and says so on the page.

Most plugins ship one language and never think about any of this.
