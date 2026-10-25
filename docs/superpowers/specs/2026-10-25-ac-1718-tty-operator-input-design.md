# AC-1718 TTY Operator Input Design

## Intent

Record when the operator types in a TTY pane so session attention time includes TTY work, without recording input bytes, key codes, or lengths.

## Scope and constraints

- Only `Terminal.Input` after a successful PTY write is an operator action.
- `Terminal.Output`, `PromptSink`, and terminal-access `RegisterInput` never count.
- Input within 30 seconds of the last recorded TTY input is folded into the same moment.
- The timeline stores `TtyInput` plus its normal timestamp and pane metadata, never input content.
- The change stays in the TTY input path and reuses `SessionPanelHandle.OperatorActed`; it does not change retention or plugin abstractions.

## Approaches

1. **Selected: source-side debounce and existing event bridge.** Split terminal input from protocol output in `TtyView`, debounce successful operator writes there, raise `TtyInput` through `TtyViewModel`, and forward it from `SessionPanelHandle` to the existing recorder subscription. This preserves the source distinction and does not touch the log's retention path.
2. **Rejected: recorder-side filtering.** Passing every keystroke to `SessionTimelineRecorder` and filtering there would mix TTY-specific policy into the shared log and increase overlap with AC-1719.
3. **Rejected: reuse `MessageSent`.** Individual TTY input is not necessarily a submitted message, so that action kind would misdescribe the timeline.

## Verification

One L2 secrets theory covers typed input versus pushed input, immediate key batching, absence of the secret text, and reading the new enum value back from JSONL. One redundant existing view test is removed and the project baseline is lowered.
