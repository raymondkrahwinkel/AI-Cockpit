using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Mcp;

// Notes how far an authorization got, for the one caller that has to tell "the sign-in was never handed to a
// browser" from "it was, and nothing came back" (AC-457). It carries a stage and nothing else — no exception, no
// URL, no authorization code — so there is nothing leakable on the seam back to the coordinator (Iron Law #8).
internal sealed class McpSignInStageRecorder
{
    private int _reached;

    // The furthest point any authorization attempt on these options got.
    public McpSignInStage Reached => (McpSignInStage)Volatile.Read(ref _reached);

    // Notes that `stage` was reached, keeping the furthest one.
    // Furthest rather than last, so a second attempt that gets less far cannot erase what the operator already
    // watched happen. Deliberately a plain read-then-write rather than a compare-and-swap: a lost update can only
    // leave the stage lower than it was, and lower is the wording that claims less — the safe direction to fail in.
    public void Record(McpSignInStage stage)
    {
        if ((int)stage > Volatile.Read(ref _reached))
        {
            Volatile.Write(ref _reached, (int)stage);
        }
    }
}
