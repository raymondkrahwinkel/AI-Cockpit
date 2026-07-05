using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SoundFlow.Abstracts;
using SoundFlow.Backends.MiniAudio;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Infrastructure.Claude;
using Cockpit.Infrastructure.Claude.Permissions;

namespace Cockpit.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<AudioEngine, MiniAudioEngine>();
        services.AddTransient<IClaudeCliProcess, ClaudeCliProcess>();

        // One shared MCP permission server for the whole app: the same instance backs the
        // IPermissionServerState sessions read at spawn time and the IHostedService lifecycle.
        services.AddSingleton<PermissionMcpServer>();
        services.AddSingleton<IPermissionServerState>(sp => sp.GetRequiredService<PermissionMcpServer>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<PermissionMcpServer>());

        return services;
    }
}
