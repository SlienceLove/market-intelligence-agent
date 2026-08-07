using Microsoft.Extensions.DependencyInjection;
using MarketIntelligence.Agent.Application.Media;

namespace MarketIntelligence.Agent.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<IChannelMediaCollector, UnconfiguredChannelMediaCollector>();
        services.AddSingleton<ITranscriptionService, UnconfiguredTranscriptionService>();
        services.AddSingleton<IFrameOcrService, UnconfiguredFrameOcrService>();
        services.AddSingleton<ISpeechSynthesisService, UnconfiguredSpeechSynthesisService>();
        services.AddSingleton<IVideoCompositionService, UnconfiguredVideoCompositionService>();
        return services;
    }
}
