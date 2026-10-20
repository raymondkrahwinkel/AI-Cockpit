# Cockpit UI design guide

This is the house style for Cockpit UI. It records the shipped UI on
`origin/epic/AC-1497`; it is not a proposal. The source of truth is
[`src/Cockpit.App/Styles/Theme.axaml`](src/Cockpit.App/Styles/Theme.axaml).

## Principles

- Quiet, flat, and compact: use a surface ladder, hairline borders, and one
  obvious action instead of decoration or large colour fields.
- Meaning is carried by the shared accent and status colours. A colour must
  answer a real state, not make a neutral control look important.
- Both palettes are first-class (`Dark` and `Light` in `Theme.axaml`, AC-860).
  A view names a semantic resource; the theme chooses its value.
- Keep resting UI calm. Reveal secondary row actions on hover/focus and reserve
  stronger treatment for a selected, active, or blocking state.
- Reuse the shared style and class before writing any setter. Theme behaviour
  includes hover, pressed, disabled, focus-visible, and both palettes.

## Tokens

Use a semantic brush with `DynamicResource` in views. Do not put a hex colour
in XAML. Colours and brushes live together in `Theme.axaml`; `*Color` is the
variant value and `*Brush` is what a view consumes.

| Role | Use this resource |
| --- | --- |
| Window, panel, secondary, inset, chrome | `CockpitWindowBgBrush`, `CockpitPanelBgBrush`, `CockpitSecondaryBgBrush`, `CockpitInsetBgBrush`, `CockpitChromeBgBrush` |
| Edges | `CockpitHairlineBrush`, `CockpitHairlineSoftBrush`, `CockpitHairlineHoverBrush` |
| Text ladder | `CockpitTextPrimaryBrush`, `CockpitTextSecondaryBrush`, `CockpitTextFaintBrush` |
| Text on fill | `CockpitTextOnAccentBrush`, `CockpitTextOnStatusBrush` |
| Links/paths | `CockpitClickablePathFgBrush` |
| Primary action | `CockpitAccentBrush`; interaction: `CockpitAccentHoverBrush`, `CockpitAccentPressedBrush` |
| Busy / capturing audio (cyan) | `CockpitStatusBusyBrush` |
| Waiting for the operator (amber) | `CockpitStatusWaitingBrush` |
| Done / available (green) | `CockpitStatusDoneBrush` |
| Background activity | `CockpitStatusBackgroundBrush` |
| Blocking failure (red) | `CockpitStatusErrorBrush` |
| Status fills/rings | the matching `CockpitStatus*ChipTintBrush`, `CockpitStatus*RingTintBrush`; waiting rows also have `CockpitStatusWaitingRowTintBrush` |
| Focus and selection | `CockpitFocusHairlineBrush`, `CockpitAccentSelectionBrush`, `CockpitAccentRingTintBrush` |

The base UI face is Inter (`Program.cs` and `Screenshotter.cs` use
`.WithInterFont()`). Use the embedded mono face only for command, tool, path,
code, or terminal text: `CockpitMonoFont` (Cascadia Mono with fallbacks).

| Item | Existing rule |
| --- | --- |
| Regular button | 12.5 px, padding `14,8` (`Style Selector="Button"`) |
| Compact button/toggle | 12 px, `MinHeight="28"`, padding `10,0` (`.Compact`) |
| Standard text/control sizes | inherit the theme; use a local size only when an existing component establishes it |
| Hairline | `CockpitHairlineThickness` (1) |
| Control/card radius | `CockpitControlRadius` (9) |
| Panel radius | `CockpitPanelRadius` (12) |
| Pill/chip radius | `CockpitPillRadius` (20) |
| Sub-navigation rail | `CockpitSubnavRailWidth` (184) |

There is no general spacing token in `Theme.axaml`; copy spacing from the
nearest existing component or mark a new rule **[open]** rather than inventing
a scale. The shared button template supplies the normal 34 px control height;
use `.Compact` for the existing 28 px dense row footprint.

```xml
<!-- Good: semantic, palette-safe. -->
<Border Background="{DynamicResource CockpitSecondaryBgBrush}"
        BorderBrush="{DynamicResource CockpitHairlineBrush}"
        CornerRadius="{DynamicResource CockpitControlRadius}" />

<!-- Never: Background="#1a1d24". -->
```

## Components

### Buttons

- Plain `Button` is the bordered secondary action. Use `Classes="Accent"` for
  the one primary action in a decision area; it is blue, semibold, and uses
  accent hover/pressed states.
- Use `Classes="Ghost"` for the cancelling/dismissing action beside a primary
  action. It keeps the full button footprint but has no resting chrome.
- Use `Classes="Subtle"` for compact chromeless icon actions, not a second text
  button hierarchy. It has 2 px padding.
- Use `Classes="Danger"` only for destructive action. Do not simulate danger
  with an ad-hoc red setter.
- Add `Compact` only where the existing dense control row needs its 28 px
  footprint. Do not set bespoke button padding, border, radius, or states.

```xml
<StackPanel Orientation="Horizontal" Spacing="8">
  <Button Classes="Ghost" Content="Cancel" />
  <Button Classes="Accent" Content="Save changes" />
</StackPanel>
```

### Badges, pills, cards, and rows

- Use `CockpitPillRadius` for word-like chips, counts, and state pills; do not
  turn ordinary cards into pills.
- Use an existing badge/pill class where one fits. Status uses its status brush
  and its matching tint/ring brush, not a new colour.
- Cards and rows stay flat: `CockpitSecondaryBgBrush`, a hairline, and
  `CockpitControlRadius` are the shared starting point. Preserve the row's
  hover/focus behaviour when it has actions.
- `Border.errorCard` is the error container: add `blocking`, `temporary`, or
  `informational`. It maps to error red, waiting amber, or a neutral hairline.

### Windows, tabs, and states

- Give a standalone dialog the shared chrome with
  `CockpitWindowChrome.Apply(...)` (`Controls/CockpitWindowChrome.cs` and
  `Plugins/PluginDialogHost.cs`); do not recreate title-bar/chrome styling.
- Use the themed `TabItem` treatment. Selection is a quiet surface change; do
  not add a competing coloured tab system.
- Empty states are plain, explanatory, and actionable only when there is a
  next step. Reuse the nearby empty-state composition; **[open]** there is no
  single global `empty` class in `Theme.axaml`.
- While work is in progress, retain the content/context and show a status or
  progress indication. Cyan means capturing/listening; blue is thinking;
  green is speaking/done in the assistant indicator styles.
- For failures, say what failed and provide a recovery action where one exists.
  Use `errorCard` severity rather than a decorative alert treatment.

## Patterns

- UI copy is English and sentence case: “Save changes”, not “Save Changes”.
  Prefer direct labels and a short reason over internal implementation terms.
- When organisation policy controls a setting, bind the existing
  `ManagedSettingConverter.Label` output so it says `Managed by …`; keep the
  control's editable state bound through `ManagedSettingConverter.Editable`.
- Keyboard users get the theme's focus-visible ring. Keep controls focusable,
  preserve standard key handling, and do not replace focus with hover-only UI.
- On narrow windows, keep the decisive action reachable, let text wrap or
  trim where the nearby component does, and use the applicable Screenshotter
  narrow/squeezed scene before changing layout.
- Status text describes the current fact, not an animation. Use amber only for
  work waiting on the user, cyan for busy/capturing, green for done, and red
  for an error.

```xml
<TextBlock Text="{Binding ManagedSettings,
  Converter={x:Static conv:ManagedSettingConverter.Label},
  ConverterParameter=Terminal.FontFamily}"
  Foreground="{DynamicResource CockpitTextSecondaryBrush}" />
```

## Never do this

- Never use hex/RGB colours, palette-specific colours, or a hard-coded brush in
  a view; use the `Cockpit*Brush` `DynamicResource` keys above.
- Never create a second style stack, copy Fluent templates, or make a local
  button style to obtain an existing state/class.
- Never override shared hover, pressed, disabled, or focus-visible behaviour
  without changing the shared theme and its baselines.
- Never use accent/status colour for decoration, nor amber as a generic warning
  when the state is not waiting for the user.
- Never hand-build dialog chrome, tab selection, an error card, or a managed
  setting marker when the shared implementation already exists.

## Check before review

1. Start with the named scene in `src/Cockpit.App/Screenshotter.cs`:
   `dotnet run --project src/Cockpit.App -- --screenshot <path>.png --scene <name> --theme Dark`.
   Repeat with `--theme Light` (AC-860); use `--size` for the relevant narrow
   window. Rendering is headless—never drive the operator's desktop.
2. Run the relevant view tests. Scene names are enumerated by
   `Screenshotter.SceneNames`; test baselines are under
   `tests/Cockpit.App.ViewTests/Baselines/`.
3. Inspect the paired `*.dark.palette.txt` and `*.light.palette.txt` baseline
   beside the scene. They name every painted theme colour/radius; re-record only
   with `COCKPIT_UPDATE_THEME_BASELINES=1` and review the diff.
4. Plugin scenes have equivalent palette baselines under
   `plugins-dev/*/Tests/Baselines/`. A changed palette baseline is evidence to
   explain, not a file to update blindly.

## References

- `src/Cockpit.App/Styles/Theme.axaml` — tokens, palettes, classes, and states.
- `src/Cockpit.App/Controls/CockpitWindowChrome.cs` — standalone dialog chrome.
- `src/Cockpit.App/Screenshotter.cs` — scene catalogue and `Light`/`Dark` render switch.
- `tests/Cockpit.App.ViewTests/Baselines/` — app scene palette baselines.
- `brand/README.md` — generated Wispslate mark/icon assets; edit the generator,
  not generated brand output.
