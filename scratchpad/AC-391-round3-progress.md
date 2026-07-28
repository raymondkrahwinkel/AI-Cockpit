# AC-391 round 3 progress (review round 2, verdict BLOCKED)

Branch: feat/AC-391-agent-coordinator-presence, HEAD efe53958 at start.

## Plan
- MF-1: add background-thread marshalling test that fails without Dispatcher.UIThread.Invoke, then simplify gateway line 36 (remove CheckAccess branch, or keep marshal only path per MF-1 instructions: "vervang je regel 36 door een kale directe aanroep" -- wait re-read)
- MF-2: fix stale docstring in IWorkspaceAgentGateway.cs:12 + sweep for more stale comments
- S-1: make GetWorkspaceSnapshot async (Task-returning), ListAgents async, follow SessionLabelSink pattern
- S-2: add embedded Forget teardown test (_TeardownEmbeddedSessionAsync ~line 5806)
- S-3: fix DependencyInjection.cs:55-57 comment (don't change behavior)
- Mutation testing for all touched guards
- Build with -warnaserror, run 3 test projects, report literal numbers

## Status: Implementation done, now building/testing

- MF-2: fixed IWorkspaceAgentGateway.cs WorkspaceId param doc (line 12 area). Swept all AC-391 touched files
  (WorkspaceAgentGateway.cs, IWorkspaceAgentGateway.cs, IWorkspaceAgentCoordinator.cs, WorkspaceAgentCoordinator.cs,
  AgentsMcpTools.cs, CockpitViewModel.cs agent-coordinator bits, DependencyInjection.cs, CHANGELOG.md, both test
  files) for other stale "partition"/"fallback only for tests" style claims. Only the one line was stale.
- S-1: IWorkspaceAgentGateway.GetWorkspaceSnapshot -> GetWorkspaceSnapshotAsync returning Task<WorkspaceAgentSnapshot?>.
  WorkspaceAgentGateway follows SessionLabelSink pattern (CheckAccess ? Task.FromResult : InvokeAsync(...).GetTask()).
  AgentsMcpTools.ListAgents -> ListAgentsAsync (Task<string>), matches VerifyMcpTools.VerifyAsync naming/attribute
  convention (attribute Name stays "list_agents"). Updated both test files to async/await.
- MF-1: added GetWorkspaceSnapshotAsync_CalledFromBackgroundThreadsWhileTheUiThreadChurnsSessions_NeverThrows in
  WorkspaceAgentGatewayTests.cs. Real background Task.Run readers (never via Dispatcher.Invoke) hammering the
  gateway while the UI thread churns Add/Remove of a sibling session 20,000 times inside one Dispatcher.UIThread.Invoke.
- S-2: added CloseEmbeddedSession_ForgetsThePaneFromTheAgentCoordinator next to the existing grid-side test, using
  cockpit.Embed(...) + embedded.CloseAsync().
- S-3: rewrote the DependencyInjection.cs cockpit-agents comment to say the "opt-out, mounted by default" framing
  only holds for a null MCP selection; a profile with an explicit (even empty) EnabledMcpServerNames excludes it
  silently. Behavior unchanged. (Had to fix a self-inflicted duplicate services.AddSingleton line from a bad Edit -
  confirmed fixed via Read.)

## DONE

Mutation testing:
- MF-1: removed the CheckAccess/InvokeAsync marshal in WorkspaceAgentGateway.GetWorkspaceSnapshotAsync (bare
  Task.FromResult(_GetWorkspaceSnapshot(paneId))). New test failed reliably 3/3 runs, ~150-170ms each, with
  System.InvalidOperationException: "Collection was modified; enumeration operation may not execute." (stack via
  CockpitViewModel.FindSession -> WorkspaceAgentGateway._GetWorkspaceSnapshot). Restored the fix by hand (matches
  original diff exactly - confirmed via git diff --stat showing WorkspaceAgentGateway.cs still shows only the
  intended net diff, no leftover).
- S-2: removed `_agentCoordinator?.Forget(session.PaneId);` from _TeardownEmbeddedSessionAsync. New test
  CloseEmbeddedSession_ForgetsThePaneFromTheAgentCoordinator failed with NSubstitute.Exceptions.ReceivedCallsException
  ("Expected to receive exactly 1 call matching: Forget(...) Actually received no matching calls."). Restored by
  hand. git diff --stat confirms CockpitViewModel.cs has ZERO diff after restore (mutation + restore cancelled out
  exactly).

Final build: `dotnet build Cockpit.slnx -c Release -warnaserror` -> Build succeeded, 0 Warning(s), 0 Error(s).

Final test runs (dotnet test <project> -c Release --no-build):
- tests/Cockpit.Infrastructure.Tests: Passed 447, Failed 0, Skipped 0, Total 447
- tests/Cockpit.App.ViewTests: Passed 320, Failed 0, Skipped 0, Total 320 (was 318 baseline + 2 new tests)
- tests/Cockpit.Core.Tests: Passed 2655, Failed 0, Skipped 0, Total 2655

Stability check: ran WorkspaceAgentGatewayTests filter 5x in a row with the fix in place -> 11/11 passed every time,
~280ms each (no flakiness).

No FluentAssertions in the two changed test files (grep confirmed empty). git status shows only the 6 intended
source/test files modified, plus untracked scratchpad/ (this progress file).

Ready to report to parent.
