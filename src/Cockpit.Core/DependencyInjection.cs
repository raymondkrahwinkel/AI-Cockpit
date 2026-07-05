using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Configuration;

namespace Cockpit.Core;

public static class DependencyInjection
{
    public static IServiceCollection AddCore(this IServiceCollection services)
    {
        services.AddOptions<CockpitOptions>();

        return services;
    }
}
