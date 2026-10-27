using Cockpit.Core.ModelIntelligence;
using Cockpit.Infrastructure.ModelIntelligence;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Infrastructure;

public static class ModelIntelligenceServiceCollectionExtensions
{
    public static IServiceCollection AddModelIntelligence(this IServiceCollection services, string cacheRoot)
    {
        services.AddSingleton<IModelIntelligence>(new OpenRouterModelIntelligence(cacheRoot));
        return services;
    }
}
