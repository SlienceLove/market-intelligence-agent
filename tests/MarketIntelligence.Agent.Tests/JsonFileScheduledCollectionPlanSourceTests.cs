using System.Text.Json;
using MarketIntelligence.Agent.Infrastructure.Bidding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Tests;

public sealed class JsonFileScheduledCollectionPlanSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "mia-plan-source-tests",
        Guid.NewGuid().ToString("N"));

    public JsonFileScheduledCollectionPlanSourceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Valid_file_loads_plans_and_writes_snapshot_and_content_free_audit()
    {
        await WriteActiveAsync(BuildDocument("daily-market", "云计算"));
        using var source = CreateSource();

        var plans = await source.GetPlansAsync();

        var plan = Assert.Single(plans);
        Assert.Equal("daily-market", plan.PlanId);
        Assert.Equal(["云计算"], plan.Keywords);
        Assert.Equal(new TimeOnly(9, 30), plan.ExecutionTimeUtc);
        Assert.Contains(DayOfWeek.Monday, plan.DaysOfWeek);
        Assert.True(File.Exists(Path.Combine(
            _root,
            JsonFileScheduledCollectionPlanSource.LastKnownGoodFileName)));

        var audit = await File.ReadAllTextAsync(Path.Combine(
            _root,
            JsonFileScheduledCollectionPlanSource.AuditFileName));
        Assert.Contains("\"outcome\":\"loaded\"", audit);
        Assert.DoesNotContain("云计算", audit);
        Assert.DoesNotContain("daily-market", audit);
    }

    [Fact]
    public async Task Documented_example_is_a_valid_loadable_plan_file()
    {
        var examplePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "docs",
            "ops",
            "scheduled-plans.example.json"));
        File.Copy(
            examplePath,
            Path.Combine(_root, JsonFileScheduledCollectionPlanSource.PlanFileName));
        using var source = CreateSource();

        var plan = Assert.Single(await source.GetPlansAsync());

        Assert.Equal("daily-market", plan.PlanId);
        Assert.Equal(["云计算", "软件采购"], plan.Keywords);
    }

    [Fact]
    public async Task Changed_valid_file_reloads_without_restarting()
    {
        await WriteActiveAsync(BuildDocument("plan-v1", "软件"));
        using var source = CreateSource();
        Assert.Equal("plan-v1", Assert.Single(await source.GetPlansAsync()).PlanId);

        await WriteActiveAsync(BuildDocument("plan-v2", "硬件"));

        Assert.Equal("plan-v2", Assert.Single(await source.GetPlansAsync()).PlanId);
    }

    [Fact]
    public async Task Invalid_change_keeps_current_last_known_good_plans()
    {
        await WriteActiveAsync(BuildDocument("stable-plan", "安全"));
        using var source = CreateSource();
        await source.GetPlansAsync();

        await WriteActiveAsync("{not-json}");
        var plans = await source.GetPlansAsync();

        Assert.Equal("stable-plan", Assert.Single(plans).PlanId);
        var audit = await ReadAuditAsync();
        Assert.Contains("\"outcome\":\"rejected\"", audit);
        Assert.Contains("\"failureCode\":\"invalid_plan_json\"", audit);
    }

    [Fact]
    public async Task Restart_with_invalid_active_file_uses_persisted_snapshot()
    {
        await WriteActiveAsync(BuildDocument("restart-safe", "招标"));
        using (var first = CreateSource())
        {
            await first.GetPlansAsync();
        }

        await WriteActiveAsync("{\"version\":2,\"plans\":[]}");
        using var restarted = CreateSource();

        var plans = await restarted.GetPlansAsync();

        Assert.Equal("restart-safe", Assert.Single(plans).PlanId);
        Assert.Contains("\"outcome\":\"fallback\"", await ReadAuditAsync());
    }

    [Fact]
    public async Task Duplicate_plan_ids_reject_entire_change()
    {
        await WriteActiveAsync(BuildDocument("stable", "基线"));
        using var source = CreateSource();
        await source.GetPlansAsync();

        var duplicate = $$"""
            {
              "version": 1,
              "plans": [
                {{BuildPlan("duplicate", "一")}},
                {{BuildPlan("DUPLICATE", "二")}}
              ]
            }
            """;
        await WriteActiveAsync(duplicate);

        var plans = await source.GetPlansAsync();

        Assert.Equal("stable", Assert.Single(plans).PlanId);
        Assert.Contains("invalid_plan_document", await ReadAuditAsync());
    }

    [Fact]
    public async Task Missing_active_and_snapshot_files_schedule_nothing()
    {
        using var source = CreateSource();

        var plans = await source.GetPlansAsync();

        Assert.Empty(plans);
        Assert.Contains("plan_file_missing", await ReadAuditAsync());
    }

    [Fact]
    public async Task Oversized_active_file_falls_back_to_persisted_snapshot()
    {
        await WriteActiveAsync(BuildDocument("size-safe", "baseline"));
        using (var first = CreateSource())
        {
            await first.GetPlansAsync();
        }

        await File.WriteAllBytesAsync(
            Path.Combine(_root, JsonFileScheduledCollectionPlanSource.PlanFileName),
            new byte[1_048_577]);
        using var restarted = CreateSource();

        var plans = await restarted.GetPlansAsync();

        Assert.Equal("size-safe", Assert.Single(plans).PlanId);
        Assert.Contains("invalid_plan_document", await ReadAuditAsync());
        Assert.Contains("\"outcome\":\"fallback\"", await ReadAuditAsync());
    }

    [Theory]
    [InlineData("{\"version\":1}")]
    [InlineData("{\"version\":1,\"plans\":[null]}")]
    [InlineData("{\"version\":1,\"plans\":[{\"planId\":\"p\",\"name\":\"n\",\"notificationChannel\":\"webhook\",\"executionTimeUtc\":\"09:30:00\",\"daysOfWeek\":[]}]}")]
    [InlineData("{\"version\":1,\"plans\":[{\"planId\":\"p\",\"name\":\"n\",\"keywords\":[\"k\"],\"notificationChannel\":\"webhook\",\"executionTimeUtc\":\"09:30:00\"}]}")]
    [InlineData("{\"version\":1,\"plans\":[{\"planId\":\"p\",\"name\":\"n\",\"keywords\":[\"k\"],\"notificationChannel\":\"webhook\",\"daysOfWeek\":[]}]}")]
    public async Task Missing_or_null_required_fields_reject_the_document(string document)
    {
        await WriteActiveAsync(document);
        using var source = CreateSource();

        var plans = await source.GetPlansAsync();

        Assert.Empty(plans);
        Assert.Contains("invalid_plan_document", await ReadAuditAsync());
    }

    [Fact]
    public async Task Audit_write_failure_does_not_block_valid_plan_loading()
    {
        await WriteActiveAsync(BuildDocument("audit-independent", "baseline"));
        var auditPath = Path.Combine(
            _root,
            JsonFileScheduledCollectionPlanSource.AuditFileName);
        Directory.CreateDirectory(auditPath);
        using var source = CreateSource();

        var plans = await source.GetPlansAsync();

        Assert.Equal("audit-independent", Assert.Single(plans).PlanId);

        Directory.Delete(auditPath);
        await source.GetPlansAsync();
        Assert.Contains("\"outcome\":\"loaded\"", await File.ReadAllTextAsync(auditPath));
    }

    [Fact]
    public async Task Audit_rotates_to_one_previous_file_at_the_size_limit()
    {
        var auditPath = Path.Combine(_root, JsonFileScheduledCollectionPlanSource.AuditFileName);
        await File.WriteAllBytesAsync(auditPath, new byte[1_048_576]);
        using var source = CreateSource();

        await source.GetPlansAsync();

        Assert.True(File.Exists(Path.Combine(
            _root,
            JsonFileScheduledCollectionPlanSource.PreviousAuditFileName)));
        Assert.Contains("plan_file_missing", await File.ReadAllTextAsync(auditPath));
    }

    [Fact]
    public async Task Temporary_snapshot_write_failure_is_retried_without_a_file_change()
    {
        await WriteActiveAsync(BuildDocument("retry-snapshot", "baseline"));
        var snapshotPath = Path.Combine(
            _root,
            JsonFileScheduledCollectionPlanSource.LastKnownGoodFileName);
        Directory.CreateDirectory(snapshotPath);
        using var source = CreateSource();

        Assert.Empty(await source.GetPlansAsync());

        Directory.Delete(snapshotPath);
        Assert.Equal("retry-snapshot", Assert.Single(await source.GetPlansAsync()).PlanId);
        Assert.True(File.Exists(snapshotPath));
    }

    [Theory]
    [InlineData("{\"Version\":1,\"plans\":[]}")]
    [InlineData("{\"version\":1,\"version\":1,\"plans\":[]}")]
    [InlineData("{\"version\":1,\"plans\":[{\"planId\":\"a\",\"planId\":\"b\",\"name\":\"n\",\"keywords\":[\"k\"],\"notificationChannel\":\"webhook\",\"executionTimeUtc\":\"09:30:00\",\"daysOfWeek\":[]}]}")]
    public async Task Wrong_property_casing_or_duplicate_properties_are_rejected(string document)
    {
        await WriteActiveAsync(document);
        using var source = CreateSource();

        Assert.Empty(await source.GetPlansAsync());
        Assert.Contains("\"outcome\":\"rejected\"", await ReadAuditAsync());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Test cleanup is best effort.
        }
    }

    private JsonFileScheduledCollectionPlanSource CreateSource() => new(
        Options.Create(new BiddingOptions { PlanRoot = _root }),
        NullLogger<JsonFileScheduledCollectionPlanSource>.Instance);

    private Task WriteActiveAsync(string content) => File.WriteAllTextAsync(
        Path.Combine(_root, JsonFileScheduledCollectionPlanSource.PlanFileName),
        content);

    private Task<string> ReadAuditAsync() => File.ReadAllTextAsync(Path.Combine(
        _root,
        JsonFileScheduledCollectionPlanSource.AuditFileName));

    private static string BuildDocument(string planId, string keyword) => $$"""
        {
          "version": 1,
          "plans": [{{BuildPlan(planId, keyword)}}]
        }
        """;

    private static string BuildPlan(string planId, string keyword) => $$"""
        {
          "planId": {{JsonSerializer.Serialize(planId)}},
          "name": "Daily market scan",
          "keywords": [{{JsonSerializer.Serialize(keyword)}}],
          "lookbackDays": 7,
          "maxResults": 20,
          "notificationChannel": "webhook",
          "executionTimeUtc": "09:30:00",
          "daysOfWeek": ["Monday", "Friday"],
          "enabled": true
        }
        """;
}
