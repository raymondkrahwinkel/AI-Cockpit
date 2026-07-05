using Microsoft.Extensions.DependencyInjection;
using SoundFlow.Abstracts;
using SoundFlow.Backends.MiniAudio;
using Zyra.Voice.Infrastructure.Claude;

namespace Zyra.Voice.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<AudioEngine, MiniAudioEngine>();
        services.AddTransient<IClaudeCliProcess, ClaudeCliProcess>();

        return services;
    }
}
