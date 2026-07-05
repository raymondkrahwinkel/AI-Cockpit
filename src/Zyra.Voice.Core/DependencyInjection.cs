using Microsoft.Extensions.DependencyInjection;
using Zyra.Voice.Core.Configuration;

namespace Zyra.Voice.Core;

public static class DependencyInjection
{
    public static IServiceCollection AddCore(this IServiceCollection services)
    {
        services.AddOptions<ZyraVoiceOptions>();

        return services;
    }
}
