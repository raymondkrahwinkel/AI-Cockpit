# AC-252 Productivity Overview Design

## Intent

Add the second Productivity workspace, **Overzicht**, so the operator can inspect their time, agent time, usage and cost over a selectable range without duplicating the host-owned timeline or AC-1731's attention calculation.

## Scope

- The first viewport follows mockup v3 `08-overzicht.png`: the same heading, range controls, five tiles, daily chart, project summary, session table and own-entry table.
- The `Jouw tijd | + agent zonder jou` switch starts on `Jouw tijd`; the first tile changes from 31u00 to 43u10 on the fixed journey data.
- Tokens and reported cost appear per session. Usage with tokens and no reported price says `geen prijs`, never `$0`.
- Tickets from the timeline appear below the own-entry table, outside the mockup's first viewport.
- `Eigen bereik` accepts inclusive local dates and refreshes the same overview for that range.
- The side menu contains exactly `Uren` and `Overzicht`. AC-1723 owns changes inside the Uren workspace; a rebase keeps its menu work.

## Reuse and boundaries

- Backend data comes from `ICockpitTimelineReader.QueryAsync`, `ICockpitTimeEntries.ListAsync` and `ICockpitHost.GetProjectsAsync`.
- All time totals come from `AttentionTime.Compute`; the overview adds no second attention algorithm.
- The UI links `plugins-dev/_shared/UI/SharedCharts.cs` and uses `RangePicker`, `GroupedBarChartControl`, `HorizontalBarChartControl` and `StatTile`.
- Lavender uses `MeasuredTimeCard.You`; project colors use `MeasuredTimeCard.ProjectBrush`.
- No new dependency, host API, test method or production write is introduced.

## Approaches

1. **Mockup-only:** smallest UI, but drops the ticket and token scope from AC-252.
2. **Full scope above the fold:** complete, but no longer compares directly with the approved mockup.
3. **Approved:** preserve mockup v3 in the first viewport, enrich the session rows with usage/cost, and place tickets below the own-entry table.

Zyra approved approach 3 through `cockpit-assistant` on 2026-10-25.

## Failure behaviour

- Empty ranges show `geen data` or an empty-state row and do not throw.
- A session with tokens and zero reported cost shows `geen prijs`.
- Invalid custom dates leave the last valid range in place and show inline feedback.
- Older day aggregates and raw timeline data are consumed as returned by the timeline contract and are not combined a second time.
