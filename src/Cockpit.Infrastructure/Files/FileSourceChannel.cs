using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Files;

namespace Cockpit.Infrastructure.Files;

internal static class FileSourceChannel
{
    public static IDisposable Register(IPluginBackendChannel channel, IFileSourceProvider provider) =>
        throw new NotSupportedException("File sources are not implemented yet.");
}
