# AC-486 UI progress

Base: cherry-picked `5926f2f0` (ProjectResource.SendsContent) from `feat/AC-486-instruction-content` onto
`0f4f39ab` (AC-485) — that commit's merge-base with this branch was exactly HEAD, so it applied cleanly.

## Done

1. `ProjectResourceRowViewModel`: added `SendsContent` (bound property), `ShowsSendsContentOption`
   (Role == Instructions), `OnRoleChanged` resets `SendsContent` to false when leaving Instructions,
   constructor + `ToDomain()` carry it.
2. `ProjectDialogViewModel.CreateAsync`: passes `resource.SendsContent` into the row constructor.
   `ToProject()` needed no change — it already projects through `row.ToDomain()`.
3. `ProjectDialog.axaml`: "Send along" checkbox next to "Tell sessions", visible only for Instructions;
   persistent hint text below (not tooltip-only) explaining sensitivity + snapshot-at-start semantics;
   also set as the checkbox's own ToolTip.
4. Screenshotter: reverted an initial attempt that folded the demo into the existing
   `project-editor-resources` scene (regressed AC-485's own machine-bound-hint visibility — caught by
   rendering and comparing). Added a dedicated scene `project-editor-instructions-send-along` instead,
   with its own new palette baseline (exactly one new file, confirmed via `git status`).
5. Tests added to `tests/Cockpit.Core.Tests/ViewModels/ProjectDialogResourceRowTests.cs` and
   `tests/Cockpit.App.ViewTests/ProjectDialogResourceRowTests.cs`. Per a mid-task instruction from the
   coordinator, both touched test files were converted in full from FluentAssertions to plain xUnit
   `Assert.*` (not just the new tests) — these two files no longer use `.Should()`.
6. Red-without-fix proven for: OnRoleChanged reset, ToDomain SendsContent mapping,
   ShowsSendsContentOption, default-false constructor value (temporarily broken, filtered test run,
   confirmed FAIL, reverted — no git operations used).

## Build/test status
`dotnet build Cockpit.slnx -warnaserror` — 0 warnings, 0 errors.
`dotnet test tests/Cockpit.Core.Tests` — 3132 passed.
`dotnet test tests/Cockpit.App.ViewTests` — 575 passed.

## Render
Confirmed via `--screenshot ... --scene project-editor-instructions-send-along --size 900x900` (window
itself is 1500 tall, clamped by DialogScreenClamp): Instructions row shows "Tell sessions" and "Send
along" side by side, both ticked, no crowding; hint text fully visible in two lines below the row.
