# Cockpit.Terminal — fork attribution

This project is an in-tree fork of [`IvanJosipovic/SvcSystems.UI.Terminal`](https://github.com/IvanJosipovic/SvcSystems.UI.Terminal)
(published to NuGet as `SvcSystems.UI.Terminal`), forked at commit
`70b515056607c12a9cd1e365a85492e6623059cc` — the revision behind the previously-referenced
NuGet package version `1.0.1`. Original work Copyright (c) 2024 Ivan Josipovic, licensed under
the MIT License (see `LICENSE` in this directory, unmodified from upstream).

## Why forked

AI-Cockpit's TTY mode (`Cockpit.App/Views/ClaudeTtyView`) showed three rendering defects: the
last column of the terminal (e.g. a status-bar `%`) was clipped, running text looked subtly
misaligned, and text appeared to shift when a selection was made. All three trace to the same
coupling: the per-cell grid width used for the column count and for positioning every drawn
element (background, caret, selection) is a single measured glyph advance
(`TerminalControl.CalculateTextSize`), while the text itself was drawn per *style run*
(potentially many characters) through `FormattedText`, which shapes the whole run using the
platform text engine. For a real font that shaping does not guarantee every character lands on
exact multiples of the measured single-glyph advance, so a run's rightmost pixels can drift past
the nominal grid — clipping the last column and producing visibly uneven spacing.

## What changed vs. upstream

- Namespace `SvcSystems.UI.Terminal` → `Cockpit.Terminal`; NuGet-only concerns (package metadata,
  multi-targeting, `PropertyGenerator.Avalonia`, samples/desktop host projects, tests) dropped —
  only the renderer control itself is carried over, integrated as a plain in-solution
  `ProjectReference`.
- `TerminalSurface.Render` now draws each run one character-cell at a time, anchored at
  `startColumn + i` × the measured cell width, instead of handing the whole run's text to a single
  `FormattedText` positioned only at the run's start column. Every glyph is now pinned to the same
  grid the column count and all overlays (caret, selection, background) already used, so a run can
  no longer drift past its nominal width. See `TerminalControl.cs` (`GetOrCreateFormattedText`,
  `TerminalSurface.Render`) for the concrete change and its accompanying comment.

No other functional changes were made; the pty-hosting contract (the app owns the pty, this
control is a pure renderer fed via `Model.Feed`/`UserInput`/`SizeChanged`) is unchanged.
