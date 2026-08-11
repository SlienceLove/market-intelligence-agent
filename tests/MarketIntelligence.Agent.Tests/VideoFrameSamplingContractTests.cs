using MarketIntelligence.Agent.Application.Media;

namespace MarketIntelligence.Agent.Tests;

public sealed class VideoFrameSamplingContractTests
{
    [Fact]
    public void Builds_bounded_sampling_command()
    {
        var request = FfmpegFrameSamplingArgumentBuilder.Build(
            new MediaAssetReference("fixture://video/demo", "video/mp4", 1024, TimeSpan.FromMinutes(2)),
            "media/frames/job-1",
            new VideoFrameSamplingOptions { SampleInterval = TimeSpan.FromSeconds(2), MaxFrames = 12 });

        Assert.Equal("ffmpeg", request.FileName);
        Assert.Contains("-frames:v", request.Arguments);
        Assert.Contains("12", request.Arguments);
        Assert.Contains("fps=0.5", request.Arguments);
        Assert.Contains("media/frames/job-1/frame-%06d.jpg", request.Arguments);
    }

    [Theory]
    [InlineData("file:///tmp/video.mp4", "video/mp4", "media/frames")]
    [InlineData("fixture://video/demo", "audio/mpeg", "media/frames")]
    [InlineData("fixture://video/demo", "video/mp4", "../frames")]
    public void Rejects_unsafe_inputs(string uri, string mediaType, string outputDirectory)
    {
        Assert.Throws<ArgumentException>(() => FfmpegFrameSamplingArgumentBuilder.Build(
            new MediaAssetReference(uri, mediaType, 1024), outputDirectory, new VideoFrameSamplingOptions()));
    }
}
