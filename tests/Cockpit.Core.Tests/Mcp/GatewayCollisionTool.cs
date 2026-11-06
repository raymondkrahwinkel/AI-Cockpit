using ModelContextProtocol.Server;

namespace Cockpit.Core.Tests.Mcp;

internal sealed class GatewayCollisionTool
{
    public static TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    [McpServerTool(Name = "contract", ReadOnly = false, Destructive = true)]
    public async Task<string> Contract(bool waitForRevoke = false, CancellationToken cancellationToken = default)
    {
        if (waitForRevoke)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Cancelled.TrySetResult();
                }
            }
        }
        return "other-upstream";
    }
}
