namespace Cockpit.Core.Abstractions.Sessions;

// What the operator did to a session (AC-1717): only the kind, never what was sent or answered.
public enum SessionOperatorAction
{
    MessageSent,
    PermissionAnswered,
    QuestionAnswered,
    TtyInput,
}
