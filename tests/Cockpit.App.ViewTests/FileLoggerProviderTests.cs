using Cockpit.App.Logging;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-741: <c>cockpit.log</c> must not grow without bound during a long, uninterrupted run.
/// </summary>
public class FileLoggerProviderTests
{
    [Fact]
    public void Log_WhenFileIsAtOrOverTheLimit_RollsToTheDotOneFile()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "cockpit.log");
            var provider = new FileLoggerProvider(path);
            var logger = provider.CreateLogger("test");

            // The constructor truncates the file (existing startup behaviour) — grow it back past the
            // limit here to simulate what a long-running session would otherwise accumulate.
            File.WriteAllText(path, new string('x', (int)FileLoggerProvider.MaxSizeBytes));

            logger.LogInformation("this write should find the file over the limit and roll it first");

            var rolloverPath = FileLoggerProvider.RolloverPathFor(path);
            Assert.True(File.Exists(rolloverPath));
            Assert.Contains("this write should find the file over the limit and roll it first", File.ReadAllText(path));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
