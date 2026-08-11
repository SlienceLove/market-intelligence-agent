using MarketIntelligence.Agent.Application.Media;

namespace MarketIntelligence.Agent.Tests;

public sealed class MediaPipelineContractTests
{
    [Fact]
    public void Ocr_normalizer_sorts_clamps_and_deduplicates_frames()
    {
        var normalized = OcrResultNormalizer.Normalize(
        [
            new OcrFrameText(TimeSpan.FromSeconds(2), "same", Confidence: 1.4),
            new OcrFrameText(TimeSpan.FromSeconds(1), " same ", Confidence: -0.2),
            new OcrFrameText(TimeSpan.FromSeconds(10), "later")
        ],
        new FrameOcrOptions { SampleInterval = TimeSpan.FromSeconds(2) });

        Assert.Collection(
            normalized,
            first =>
            {
                Assert.Equal(TimeSpan.FromSeconds(1), first.Timestamp);
                Assert.Equal(0, first.Confidence);
            },
            second => Assert.Equal(TimeSpan.FromSeconds(10), second.Timestamp));
    }

    [Fact]
    public async Task Fake_ocr_returns_bounded_frame_results()
    {
        var request = new MediaJobRequest(
            "ocr-1",
            MediaJobKind.FrameOcr,
            [new MediaAssetReference("asset://fixture/video", "video/mp4", 1024, TimeSpan.FromSeconds(10))]);
        var service = new FakeFrameOcrService(
        [
            new OcrFrameText(TimeSpan.FromSeconds(1), "标题", new OcrBoundingBox(0, 0, 100, 20), Confidence: 1.2),
            new OcrFrameText(TimeSpan.FromSeconds(2), "标题", new OcrBoundingBox(0, 0, 100, 20), Confidence: 0.8),
            new OcrFrameText(TimeSpan.FromSeconds(5), "正文", Confidence: 0.7)
        ]);

        var result = await service.RecognizeAsync(request);

        Assert.Equal(MediaJobStatus.Succeeded, result.Status);
        Assert.Equal(2, result.OcrFrames!.Count);
        Assert.Equal(1, result.OcrFrames[0].Confidence);
    }

    [Fact]
    public void Speech_chunker_prefers_sentence_boundaries()
    {
        var chunks = SpeechTextChunker.Split("第一句。第二句。第三句", 5);

        Assert.Equal(["第一句。", "第二句。", "第三句"], chunks);
    }

    [Fact]
    public async Task Fake_tts_enforces_voice_allowlist_and_returns_audio_asset()
    {
        var request = new MediaJobRequest(
            "tts-1",
            MediaJobKind.SpeechSynthesis,
            [new MediaAssetReference("asset://script/tts-1", "text/plain")],
            Parameters: new Dictionary<string, string>
            {
                ["text"] = "这是一个用于契约测试的短句。",
                ["voice"] = "default",
                ["language"] = "zh-CN"
            });

        var result = await new FakeSpeechSynthesisService().SynthesizeAsync(request);

        Assert.Equal(MediaJobStatus.Succeeded, result.Status);
        Assert.Equal("audio/wav", result.Assets![0].MediaType);

        var rejected = await new FakeSpeechSynthesisService().SynthesizeAsync(
            request with
            {
                JobId = "tts-2",
                Parameters = new Dictionary<string, string>
                {
                    ["text"] = "短句",
                    ["voice"] = "unapproved"
                }
            });

        Assert.Equal("voice_not_allowed", rejected.FailureCode);
    }

    [Fact]
    public void Ffmpeg_builder_rejects_uncontrolled_paths()
    {
        var video = new MediaAssetReference("asset://fixture/video", "video/mp4");
        var audio = new MediaAssetReference("asset://fixture/audio", "audio/wav");

        Assert.Throws<ArgumentException>(() =>
            FfmpegArgumentBuilder.Build(video, audio, "..\\escape.mp4", TimeSpan.FromMinutes(1)));

        var request = FfmpegArgumentBuilder.Build(video, audio, "media/job.mp4", TimeSpan.FromMinutes(1));
        Assert.Equal("ffmpeg", request.FileName);
        Assert.Contains("-shortest", request.Arguments);
    }

    [Fact]
    public async Task Fake_composition_returns_controlled_asset()
    {
        var request = new MediaJobRequest(
            "compose-1",
            MediaJobKind.VideoComposition,
            [
                new MediaAssetReference("asset://fixture/video", "video/mp4", 1024, TimeSpan.FromSeconds(10)),
                new MediaAssetReference("asset://fixture/audio", "audio/wav", 1024, TimeSpan.FromSeconds(8))
            ]);

        var result = await new FakeVideoCompositionService(new FakeProcessRunner()).ComposeAsync(request);

        Assert.Equal(MediaJobStatus.Succeeded, result.Status);
        Assert.StartsWith("asset://fixture/video/", result.Assets![0].Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fake_media_pipeline_connects_collection_to_composition()
    {
        var collector = new FakeChannelMediaCollector(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "approved.example" });
        var collected = await collector.CollectAsync(new MediaJobRequest(
            "pipeline-collect",
            MediaJobKind.Collection,
            [new MediaAssetReference("https://approved.example/video", "text/uri-list")],
            IdempotencyKey: "pipeline-1"));
        var video = Assert.Single(collected.Assets!);

        var transcript = await new FakeTranscriptionService(
        [
            new TimedTextSegment(TimeSpan.Zero, TimeSpan.FromSeconds(2), "欢迎观看"),
            new TimedTextSegment(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), "产品介绍")
        ]).TranscribeAsync(new MediaJobRequest(
            "pipeline-asr",
            MediaJobKind.Transcription,
            [video]));

        var ocr = await new FakeFrameOcrService(
        [
            new OcrFrameText(TimeSpan.FromSeconds(1), "产品介绍", Confidence: 0.9)
        ]).RecognizeAsync(new MediaJobRequest(
            "pipeline-ocr",
            MediaJobKind.FrameOcr,
            [video]));

        var speech = await new FakeSpeechSynthesisService().SynthesizeAsync(new MediaJobRequest(
            "pipeline-tts",
            MediaJobKind.SpeechSynthesis,
            [video],
            Parameters: new Dictionary<string, string>
            {
                ["text"] = string.Join(' ', transcript.TimedText!.Select(segment => segment.Text)),
                ["voice"] = "default",
                ["language"] = "zh-CN"
            }));

        var composition = await new FakeVideoCompositionService(new FakeProcessRunner()).ComposeAsync(
            new MediaJobRequest(
                "pipeline-compose",
                MediaJobKind.VideoComposition,
                [video, Assert.Single(speech.Assets!)]));

        Assert.Equal(MediaJobStatus.Succeeded, transcript.Status);
        Assert.Equal(MediaJobStatus.Succeeded, ocr.Status);
        Assert.Equal(MediaJobStatus.Succeeded, speech.Status);
        Assert.Equal(MediaJobStatus.Succeeded, composition.Status);
        Assert.NotEmpty(composition.Assets!);
    }
}
