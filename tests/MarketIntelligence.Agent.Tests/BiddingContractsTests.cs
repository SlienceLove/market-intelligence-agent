using MarketIntelligence.Agent.Application.Bidding;

namespace MarketIntelligence.Agent.Tests;

public sealed class BiddingContractsTests
{
    private static BiddingCollectionRequest ValidRequest(params string[] keywords) =>
        new()
        {
            CollectionId = "collect-1",
            Keywords = keywords.Length == 0 ? ["智慧园区"] : keywords,
            CorrelationId = "corr-1"
        };

    private static BiddingNotice ValidNotice(
        string title = "智慧园区综合管理平台招标公告",
        string url = "https://www.ccgp.gov.cn/notice/12345.htm",
        string platform = "ccgp.gov.cn",
        DateTimeOffset? publishedAt = null) =>
        new()
        {
            Title = title,
            Publisher = "某市公共资源交易中心",
            PublishedAt = publishedAt ?? new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero),
            NoticeUrl = url,
            SourcePlatform = platform,
            Fingerprint = BiddingNoticeFingerprint.Compute(platform, url, title)
        };

    [Fact]
    public void Request_validation_rejects_missing_and_oversized_inputs()
    {
        Assert.Null(ValidRequest().Validate());

        Assert.Equal("collection_id_required", (ValidRequest() with { CollectionId = "  " }).Validate());
        Assert.Equal("keyword_required", (ValidRequest() with { Keywords = [] }).Validate());
        Assert.Equal("invalid_keyword", (ValidRequest() with { Keywords = ["  "] }).Validate());
        Assert.Equal(
            "keyword_limit_exceeded",
            (ValidRequest() with
            {
                Keywords = Enumerable.Range(0, BiddingContractLimits.MaxKeywords + 1)
                    .Select(index => $"kw-{index}")
                    .ToArray()
            }).Validate());
        Assert.Equal(
            "invalid_max_results",
            (ValidRequest() with { MaxResults = BiddingContractLimits.MaxResultsCeiling + 1 }).Validate());
        Assert.Equal("invalid_max_results", (ValidRequest() with { MaxResults = 0 }).Validate());
    }

    [Fact]
    public void Request_validation_rejects_inverted_and_oversized_time_windows()
    {
        var from = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.Null((ValidRequest() with { FromDate = from, ToDate = from.AddDays(7) }).Validate());
        Assert.Equal(
            "invalid_time_window",
            (ValidRequest() with { FromDate = from, ToDate = from.AddDays(-1) }).Validate());
        Assert.Equal(
            "invalid_time_window",
            (ValidRequest() with
            {
                FromDate = from,
                ToDate = from.Add(BiddingContractLimits.MaxTimeWindow).AddDays(1)
            }).Validate());
    }

    [Fact]
    public void Notice_validation_rejects_unsafe_and_non_public_urls()
    {
        Assert.Null(ValidNotice().Validate());

        // Malformed input is a validation problem; a well-formed URL pointing
        // somewhere it must not point is a security problem. The categories differ,
        // so the codes must too.
        Assert.Equal("invalid_notice_url", (ValidNotice() with { NoticeUrl = "ftp://example.com/a" }).Validate());
        Assert.Equal("invalid_notice_url", (ValidNotice() with { NoticeUrl = "/relative/path" }).Validate());
        Assert.Equal("invalid_notice_url", (ValidNotice() with { NoticeUrl = "  " }).Validate());

        Assert.Equal("unsafe_notice_url", (ValidNotice() with { NoticeUrl = "https://user:pw@example.com/a" }).Validate());
        Assert.Equal("unsafe_notice_url", (ValidNotice() with { NoticeUrl = "http://127.0.0.1/a" }).Validate());
        Assert.Equal("unsafe_notice_url", (ValidNotice() with { NoticeUrl = "http://localhost/a" }).Validate());

        Assert.Equal(BiddingFailureCategory.Validation, BiddingFailureCatalog.Classify("invalid_notice_url"));
        Assert.Equal(BiddingFailureCategory.Security, BiddingFailureCatalog.Classify("unsafe_notice_url"));
    }

    [Fact]
    public void Failure_catalog_classifies_codes_and_marks_configuration_gaps_non_retryable()
    {
        // Allowlist and robots denials are Authorization, matching the precedent
        // MediaFailureCatalog set for source_host_not_allowed.
        Assert.Equal(BiddingFailureCategory.Authorization, BiddingFailureCatalog.Classify("bidding_source_not_allowed"));
        Assert.Equal(BiddingFailureCategory.Authorization, BiddingFailureCatalog.Classify("robots_disallowed"));
        Assert.Equal(BiddingFailureCategory.Validation, BiddingFailureCatalog.Classify("keyword_required"));
        Assert.Equal(BiddingFailureCategory.None, BiddingFailureCatalog.Classify(null));
        Assert.Equal(BiddingFailureCategory.Unknown, BiddingFailureCatalog.Classify("something_new"));

        Assert.False(BiddingFailureCatalog.IsRetryable("bidding_source_not_configured"));
        Assert.False(BiddingFailureCatalog.IsRetryable("robots_disallowed"));
        Assert.False(BiddingFailureCatalog.IsRetryable("keyword_required"));
        Assert.True(BiddingFailureCatalog.IsRetryable("rate_limited"));
    }

    [Fact]
    public void Failure_messages_are_sanitized_so_secrets_and_urls_never_leak()
    {
        Assert.Equal(
            BiddingFailureCatalog.SafeDefaultMessage("robots_disallowed"),
            BiddingFailureCatalog.SanitizeMessage("robots_disallowed", "blocked at https://host/secret?token=abc"));
        Assert.Equal(
            BiddingFailureCatalog.SafeDefaultMessage("notification_rejected"),
            BiddingFailureCatalog.SanitizeMessage("notification_rejected", "webhook api_key=xyz rejected"));
        Assert.Equal(
            BiddingFailureCatalog.SafeDefaultMessage("internal_error"),
            BiddingFailureCatalog.SanitizeMessage("internal_error", "failed at C:\\secrets\\key.pem"));

        Assert.Equal("Keyword must not be blank.", BiddingFailureCatalog.SanitizeMessage("invalid_keyword", "Keyword must not be blank."));
    }

    [Fact]
    public void Result_factories_derive_category_and_deduplicate_notices_newest_first()
    {
        var older = ValidNotice(
            title: "旧公告",
            url: "https://www.ccgp.gov.cn/notice/1.htm",
            publishedAt: new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        var newer = ValidNotice(
            title: "新公告",
            url: "https://www.ccgp.gov.cn/notice/2.htm",
            publishedAt: new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero));

        var success = BiddingCollectionResult.Success("collect-1", [older, newer, older], "corr-1");

        Assert.True(success.Succeeded);
        Assert.True(success.IsTerminal);
        Assert.Equal(BiddingFailureCategory.None, success.ErrorCategory);
        Assert.Equal(2, success.Notices.Count);
        Assert.Equal("新公告", success.Notices[0].Title);
        Assert.Null(success.Validate());

        var failed = BiddingCollectionResult.Failed("collect-1", "bidding_source_not_configured");
        Assert.Equal(BiddingCollectionStatus.Failed, failed.Status);
        Assert.Equal(BiddingFailureCategory.ProviderUnavailable, failed.ErrorCategory);

        var cancelled = BiddingCollectionResult.Cancelled("collect-1");
        Assert.Equal(BiddingFailureCategory.Cancelled, cancelled.ErrorCategory);
        Assert.False(BiddingCollectionResult.Running("collect-1").IsTerminal);
    }

    [Fact]
    public void Fingerprint_is_stable_across_url_and_title_variants()
    {
        const string platform = "ccgp.gov.cn";
        var canonical = BiddingNoticeFingerprint.Compute(
            platform,
            "https://www.ccgp.gov.cn/notice/12345.htm?id=7",
            "智慧园区 综合管理平台 招标公告");

        Assert.Equal(canonical, BiddingNoticeFingerprint.Compute(
            "https://WWW.CCGP.GOV.CN/",
            "HTTPS://ccgp.gov.cn:443/notice/12345.htm?id=7&page=3&utm_source=wx#section",
            "  智慧园区\t综合管理平台\u3000招标公告  "));

        Assert.NotEqual(canonical, BiddingNoticeFingerprint.Compute(
            platform,
            "https://www.ccgp.gov.cn/notice/12345.htm?id=8",
            "智慧园区 综合管理平台 招标公告"));
        Assert.NotEqual(canonical, BiddingNoticeFingerprint.Compute(
            platform,
            "https://www.ccgp.gov.cn/notice/12345.htm?id=7",
            "另一个公告标题"));
        Assert.StartsWith("bidding:ccgp.gov.cn:", canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_code_the_validators_emit_is_registered_in_the_catalog()
    {
        // An unregistered code classifies as Unknown and loses its retry policy,
        // which is silent at runtime. Pin the emitted set so that stays impossible.
        var emitted = new[]
        {
            "collection_id_required", "invalid_collection_id", "keyword_required", "invalid_keyword",
            "keyword_limit_exceeded", "invalid_region", "invalid_industry", "invalid_max_results",
            "invalid_time_window", "invalid_notice_title", "invalid_notice_publisher",
            "invalid_notice_url", "unsafe_notice_url", "invalid_source_platform",
            "invalid_notice_fingerprint", "invalid_status", "failure_code_required",
            "notice_limit_exceeded", "invalid_request", "empty_collection_result",
            "bidding_source_not_configured", "bidding_source_not_allowed", "robots_disallowed",
            "cancelled", "internal_error"
        };

        var unregistered = emitted.Where(code => !BiddingFailureCatalog.IsRegistered(code)).ToArray();

        Assert.Empty(unregistered);
        Assert.All(emitted, code => Assert.NotEqual(
            BiddingFailureCategory.Unknown,
            BiddingFailureCatalog.Classify(code)));
        Assert.False(BiddingFailureCatalog.IsRegistered("not_a_real_code"));
    }

    [Fact]
    public void Notice_contract_exposes_no_personal_contact_fields()
    {
        var forbidden = new[] { "contact", "phone", "mobile", "email", "idcard", "person" };

        var offenders = typeof(BiddingNotice)
            .GetProperties()
            .Where(property => forbidden.Any(term =>
                property.Name.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Select(property => property.Name)
            .ToArray();

        Assert.Empty(offenders);
    }
}
