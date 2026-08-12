using Microsoft.Extensions.DependencyInjection;
using MarketIntelligence.Agent.Application.Bidding;
using MarketIntelligence.Agent.Application.Media;
using MarketIntelligence.Agent.Application.Images;
using MarketIntelligence.Agent.Application.Notifications;
using MarketIntelligence.Agent.Infrastructure.Bidding;
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
        AddBiddingPersistence(services);
        return services;
    }

    /// <summary>
    /// Persists the notice ledger and the scheduled-execution history when
    /// <c>Bidding:LedgerRoot</c> is configured. Without it both fall back to the
    /// in-memory implementations registered by the application layer, so the host
    /// still starts; the cost is that dedupe and (plan, date) idempotency do not
    /// survive a restart. Both are resolved through a factory rather than eagerly so
    /// an unconfigured host never touches the filesystem.
    /// </summary>
    private static void AddBiddingPersistence(IServiceCollection services)
    {
        services.AddOptions<BiddingOptions>().BindConfiguration("Bidding");

        services.AddSingleton<IBiddingNoticeLedger>(serviceProvider =>
            IsLedgerRootConfigured(serviceProvider)
                ? ActivatorUtilities.CreateInstance<JsonLinesBiddingNoticeLedger>(serviceProvider)
                : new InMemoryNoticeLedger());

        services.AddSingleton<IScheduledCollectionHistory>(serviceProvider =>
            IsLedgerRootConfigured(serviceProvider)
                ? ActivatorUtilities.CreateInstance<JsonLinesScheduledCollectionHistory>(serviceProvider)
                : new InMemoryScheduledCollectionHistory());
    }

    private static bool IsLedgerRootConfigured(IServiceProvider serviceProvider) =>
        !string.IsNullOrWhiteSpace(serviceProvider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<BiddingOptions>>()
            .Value.LedgerRoot);

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
