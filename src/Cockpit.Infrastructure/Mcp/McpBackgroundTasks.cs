using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Mcp;

internal sealed class McpBackgroundTasks(ILogger logger)
{
    private readonly Lock _sync = new();
    private readonly Dictionary<long, Task> _pending = [];
    private long _nextId;

    public void Run(Func<Task> work)
    {
        lock (_sync)
        {
            var id = ++_nextId;
            _pending[id] = Task.Run(async () =>
            {
                try
                {
                    await work();
                }
                catch (Exception)
                {
                    logger.LogWarning("MCP background cleanup failed.");
                }
                finally
                {
                    lock (_sync)
                    {
                        _pending.Remove(id);
                    }
                }
            });
        }
    }

    public Task DrainAsync()
    {
        lock (_sync)
        {
            return Task.WhenAll(_pending.Values);
        }
    }
}
