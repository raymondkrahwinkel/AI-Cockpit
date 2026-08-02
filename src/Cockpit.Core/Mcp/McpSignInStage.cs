namespace Cockpit.Core.Mcp;

// How far a sign-in got before it stopped (AC-457). The cockpit cannot say *what* refused without risking
// request or response material in the UI (Iron Law #8), but it can say *where* it stopped — and that is the
// difference between sending the operator to a browser window and telling them one was never opened.
//
// Each value is named for what the cockpit did, not for what it hopes happened on the other side. Handing a URL to
// the system browser is the last thing this process can observe: whether a window then appeared is not knowable
// from here, and a stage that asserted it would be the same defect one layer down.
public enum McpSignInStage
{
    // The sign-in was never handed to a browser. Nothing interactive was asked for, or discovery, registration, the
    // loopback listener or the hand-off itself refused first. The default, so a path that reports nothing claims
    // nothing.
    NoBrowserLaunched,

    // The sign-in was handed to the system browser, and nothing ever arrived at the loopback redirect.
    BrowserRequested,

    // A redirect arrived, usable or not: a refusal carries one just as a code does, and the operator's browser tab
    // was answered either way. What followed it produced no credential this cockpit could use.
    AuthorizationReturned,
}
