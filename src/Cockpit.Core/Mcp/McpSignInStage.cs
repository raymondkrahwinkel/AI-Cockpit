namespace Cockpit.Core.Mcp;

// AC-457: how far a sign-in got before it stopped. The cockpit cannot say *what* refused without risking
// request/response material in the UI (Iron Law #8), so it says *where* — named for what the cockpit did, not
// for what it hopes happened after the browser hand-off, which it can no longer observe.
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
