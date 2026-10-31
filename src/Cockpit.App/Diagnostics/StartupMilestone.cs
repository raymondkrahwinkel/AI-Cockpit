using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.Diagnostics;

internal sealed class StartupMilestone : IDisposable
{
    private readonly ILogger _logger;
    private readonly string _step;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public StartupMilestone(ILogger logger, string step)
    {
        _logger = logger;
        _step = step;
        _logger.LogInformation("startup {Step} begin", _step);
    }

    public void Dispose()
    {
        _logger.LogInformation("startup {Step} end duration={ElapsedMs}ms", _step, _clock.ElapsedMilliseconds);
    }
}
