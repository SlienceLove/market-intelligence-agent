using Microsoft.Extensions.DependencyInjection;
using MarketIntelligence.Agent.Application.Images;
using MarketIntelligence.Agent.Infrastructure.Images;
using MarketIntelligence.Agent.Infrastructure.Media;

namespace MarketIntelligence.Agent.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddOptions<ComfyUiOptions>().BindConfiguration("ComfyUi");
        services.AddOptions<MediaOptions>().BindConfiguration("Media");
        services.AddHttpClient<IImageGenerationService, ComfyUiImageGenerationService>();
        return services;
    }
}
