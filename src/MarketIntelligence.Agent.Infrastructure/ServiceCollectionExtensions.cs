using Microsoft.Extensions.DependencyInjection;
using MarketIntelligence.Agent.Application.Bidding;
using MarketIntelligence.Agent.Application.Media;
using MarketIntelligence.Agent.Application.Images;
using MarketIntelligence.Agent.Application.Notifications;
using MarketIntelligence.Agent.Infrastructure.Images;
using MarketIntelligence.Agent.Infrastructure.Media;
using MarketIntelligence.Agent.Infrastructure.Notifications;

namespace MarketIntelligence.Agent.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddOptions<ComfyUiOptions>().BindConfiguration("ComfyUi");
        services.AddOptions<MediaOptions>().BindConfiguration("Media");
        services.AddOptions<MediaCollectorOptions>().BindConfiguration("Media:Collector");
        services.AddOptions<AsrHttpOptions>().BindConfiguration("Media:Asr");
        services.AddOptions<OcrHttpOptions>().BindConfiguration("Media:Ocr");
        services.AddOptions<TtsHttpOptions>().BindConfiguration("Media:Tts");
        services.AddHttpClient<IImageGenerationService, ComfyUiImageGenerationService>();
        services.AddHttpClient<IChannelMediaCollector, HttpChannelMediaCollector>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false
            });
        services.AddHttpClient<ITranscriptionService, HttpTranscriptionService>();
        services.AddHttpClient<IFrameOcrService, HttpFrameOcrService>();
        services.AddHttpClient<HttpSpeechSynthesisService>();

        // FFmpeg-backed capabilities. These resolve even when unconfigured: the
        // resolver and runner return provider_not_configured rather than throwing,
        // so the host still starts without an ffmpeg install.
        services.AddSingleton<IMediaAssetPathResolver, MediaAssetPathResolver>();
        services.AddTransient<ISpeechSynthesisService>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<TtsHttpOptions>>().Value;
            return options.Enabled
                ? serviceProvider.GetRequiredService<HttpSpeechSynthesisService>()
                : new UnconfiguredSpeechSynthesisService();
        });
        services.AddSingleton<IProcessRunner, FfmpegProcessRunner>();
        services.AddSingleton<IMediaProbe, FfprobeMediaProbe>();
        services.AddSingleton<IVideoFrameSampler, FfmpegVideoFrameSampler>();
        services.AddSingleton<IVideoCompositionService, FfmpegVideoCompositionService>();
        AddNotifications(services);
        return services;
    }

    /// <summary>
    /// Registers the real push channels under their plan channel keys. Both are
    /// registered unconditionally because each reports <c>IsConfigured == false</c>
    /// and returns <c>notification_not_configured</c> when its section is absent, and
    /// <see cref="NotificationOptions"/> defaults to <c>Enabled = false</c> with
    /// <c>DryRun = true</c>: a host that configures nothing cannot send.
    /// </summary>
    private static void AddNotifications(IServiceCollection services)
    {
        services.AddOptions<NotificationOptions>().BindConfiguration("Notifications");
        services.AddHttpClient();

        services.AddKeyedSingleton<INotificationChannel, SmtpNotificationChannel>(
            ScheduledNotificationChannels.Smtp);
        services.AddKeyedSingleton<INotificationChannel, WebhookNotificationChannel>(
            ScheduledNotificationChannels.Webhook);
    }
}
