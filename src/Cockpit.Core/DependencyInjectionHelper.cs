using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Abstractions;

namespace Cockpit.Core;

public static class DependencyInjectionHelper
{
    // AsyncLocal rather than [ThreadStatic]: StackGuard moves a deep resolve onto a second thread, and
    // only ExecutionContext flows across that hop. The value is a List, so both threads share the one
    // instance — a thread-bound chain would be empty there and never see the loop (AC-1112).
    private static readonly AsyncLocal<List<Type>> ResolutionChain = new();

    public static IServiceCollection AddServices(this IServiceCollection services, params Assembly[] assemblies)
    {
        var scannedFrom = services.Count;

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

        // AsSelfWithInterfaces forwards every interface through a factory that re-enters GetService, which
        // starts a fresh CallSiteChain and blinds MEDI's cycle detector. Descriptors carrying an
        // ImplementationType are ordinary call sites MEDI already guards, so only the factories are wrapped.
        for (var index = scannedFrom; index < services.Count; index++)
        {
            var descriptor = services[index];
            if (descriptor.ImplementationFactory is not { } forward)
            {
                continue;
            }

            services[index] = new ServiceDescriptor(
                descriptor.ServiceType,
                GuardAgainstCycles(descriptor.ServiceType, forward),
                descriptor.Lifetime);
        }

        return services;
    }

    // Throws on the second visit rather than past a depth threshold: it steps in before the recursion runs
    // on, so it never waits on anything. A threshold would become the next stalling test instead.
    private static Func<IServiceProvider, object> GuardAgainstCycles(
        Type serviceType,
        Func<IServiceProvider, object> forward) => provider =>
    {
        var chain = ResolutionChain.Value ??= [];

        var openedAt = chain.IndexOf(serviceType);
        if (openedAt >= 0)
        {
            var loop = chain.Skip(openedAt).Append(serviceType).Select(type => type.Name);

            throw new InvalidOperationException(
                $"A circular dependency was detected: {string.Join(" -> ", loop)}");
        }

        chain.Add(serviceType);
        try
        {
            return forward(provider);
        }
        finally
        {
            chain.RemoveAt(chain.Count - 1);
        }
    };
}
