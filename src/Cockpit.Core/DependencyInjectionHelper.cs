using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Abstractions;

namespace Cockpit.Core;

public static class DependencyInjectionHelper
{
    // AsyncLocal rather than [ThreadStatic]: StackGuard moves a deep resolve onto a second thread, and
    // only ExecutionContext flows across that hop — a thread-bound chain is empty there and never sees
    // the loop (AC-1112).
    private static readonly AsyncLocal<Frame?> ResolutionChain = new();

    // One node per frame rather than a shared list. An AsyncLocal value is inherited by every branch of
    // the context, so once a resolve on the main thread sets it, parallel resolves further on would share
    // the one instance and report a cycle that is not there.
    private sealed record Frame(Type ServiceType, Frame? Caller);

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
        var caller = ResolutionChain.Value;

        for (var frame = caller; frame is not null; frame = frame.Caller)
        {
            if (frame.ServiceType == serviceType)
            {
                throw new InvalidOperationException(
                    $"A circular dependency was detected: {DescribeLoop(caller, serviceType)}");
            }
        }

        ResolutionChain.Value = new Frame(serviceType, caller);
        try
        {
            return forward(provider);
        }
        finally
        {
            ResolutionChain.Value = caller;
        }
    };

    // Walks back to the first visit of the repeated type, then reads out in resolution order.
    private static string DescribeLoop(Frame? caller, Type serviceType)
    {
        var loop = new List<string>();

        for (var frame = caller; frame is not null; frame = frame.Caller)
        {
            loop.Add(frame.ServiceType.Name);

            if (frame.ServiceType == serviceType)
            {
                break;
            }
        }

        loop.Reverse();
        loop.Add(serviceType.Name);

        return string.Join(" -> ", loop);
    }
}
