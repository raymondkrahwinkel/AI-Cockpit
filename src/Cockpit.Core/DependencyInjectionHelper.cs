using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Abstractions;

namespace Cockpit.Core;

public static class DependencyInjectionHelper
{
    public static IServiceCollection AddServices(this IServiceCollection services, params Assembly[] assemblies)
    {
        services.Scan(scan => scan
            .FromAssemblies(assemblies)
            .AddClasses(classes => classes.AssignableTo<ISingletonService>(), publicOnly: false)
            .AsSelfWithInterfaces()
            .WithSingletonLifetime()
            .AddClasses(classes => classes.AssignableTo<IScopedService>(), publicOnly: false)
            .AsSelfWithInterfaces()
            .WithScopedLifetime()
            .AddClasses(classes => classes.AssignableTo<ITransientService>(), publicOnly: false)
            .AsSelfWithInterfaces()
            .WithTransientLifetime());

        return services;
    }
}
