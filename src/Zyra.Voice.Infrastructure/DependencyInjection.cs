using Microsoft.Extensions.DependencyInjection;
using SoundFlow.Abstracts;
using SoundFlow.Backends.MiniAudio;

namespace Zyra.Voice.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<AudioEngine, MiniAudioEngine>();

        return services;
    }
}
