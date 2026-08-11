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

        // One open end would slip past the ceiling entirely: a 1900 start with no
        // end is an unbounded history walk that the result cap does not prevent.
        Assert.Equal("invalid_time_window", (ValidRequest() with { FromDate = from }).Validate());
        Assert.Equal("invalid_time_window", (ValidRequest() with { ToDate = from }).Validate());
        Assert.Equal(
            "invalid_time_window",
            (ValidRequest() with { FromDate = new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero) }).Validate());

        // Omitting the window entirely stays valid.
        Assert.Null((ValidRequest() with { FromDate = null, ToDate = null }).Validate());
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
    public void Fingerprint_preserves_query_keys_that_may_carry_notice_identity()
    {
        const string platform = "ccgp.gov.cn";
        const string title = "招标公告";

        // p is the canonical post id on WordPress-style sites; index and from are
        // equally often a record index or a date bound. Dropping them would let two
        // distinct notices collapse onto one ledger identity.
        foreach (var key in new[] { "p", "index", "from", "id", "noticeid" })
        {
            Assert.NotEqual(
                BiddingNoticeFingerprint.Compute(platform, $"https://ccgp.gov.cn/notice?{key}=101", title),
                BiddingNoticeFingerprint.Compute(platform, $"https://ccgp.gov.cn/notice?{key}=102", title));
        }

        // Paging and tracking keys stay volatile.
        foreach (var key in new[] { "page", "pagesize", "utm_source", "spm", "ts", "_", "rnd" })
        {
            Assert.Equal(
                BiddingNoticeFingerprint.Compute(platform, "https://ccgp.gov.cn/notice?id=7", title),
                BiddingNoticeFingerprint.Compute(platform, $"https://ccgp.gov.cn/notice?id=7&{key}=99", title));
        }
    }

    [Fact]
    public void Fingerprint_accepts_platform_specific_volatile_keys_without_loosening_the_default()
    {
        const string platform = "example.gov.cn";
        const string title = "招标公告";
        const string withKey = "https://example.gov.cn/notice?id=7&view=grid";

        var strict = BiddingNoticeFingerprint.Compute(platform, withKey, title);
        var baseline = BiddingNoticeFingerprint.Compute(platform, "https://example.gov.cn/notice?id=7", title);

        Assert.NotEqual(baseline, strict);
        Assert.Equal(baseline, BiddingNoticeFingerprint.Compute(platform, withKey, title, ["view"]));

        // An adapter opting a key out must not affect identity-bearing keys.
        Assert.NotEqual(
            BiddingNoticeFingerprint.Compute(platform, "https://example.gov.cn/notice?id=8", title, ["view"]),
            BiddingNoticeFingerprint.Compute(platform, "https://example.gov.cn/notice?id=9", title, ["view"]));
    }

    [Fact]
    public void Notice_validation_rejects_personal_data_smuggled_into_free_text()
    {
        Assert.Equal("personal_data_detected", (ValidNotice() with { Title = "招标公告 联系 13812345678" }).Validate());
        Assert.Equal("personal_data_detected", (ValidNotice() with { Publisher = "采购中心 010-12345678" }).Validate());
        Assert.Equal("personal_data_detected", (ValidNotice() with { Region = "江苏 a.b@example.com" }).Validate());
        Assert.Equal("personal_data_detected", (ValidNotice() with { Industry = "110101199001011234" }).Validate());
        Assert.Equal(
            "personal_data_detected",
            (ValidNotice() with { NoticeUrl = "https://www.ccgp.gov.cn/n?mail=a.b@example.com" }).Validate());

        Assert.Equal(BiddingFailureCategory.Security, BiddingFailureCatalog.Classify("personal_data_detected"));

        // Amounts, project codes, and years must not trip the guard.
        Assert.Null((ValidNotice() with { AmountRange = "1000000-5000000" }).Validate());
        Assert.Null((ValidNotice() with { Title = "2026 年度智慧园区项目 编号 JS20260811001" }).Validate());
        Assert.Null((ValidNotice() with { Publisher = "某市公共资源交易中心" }).Validate());
    }

    [Fact]
    public void Retryability_fails_closed_for_configuration_gaps_and_unregistered_codes()
    {
        // The Media-style spelling is permanent too; only the bidding-specific
        // code used to be special-cased, so this variant retried forever.
        Assert.False(BiddingFailureCatalog.IsRetryable("provider_not_configured"));
        Assert.False(BiddingFailureCatalog.IsRetryable("notification_not_configured"));
        Assert.False(BiddingFailureCatalog.IsRetryable("some_future_not_configured"));
        Assert.False(BiddingFailureCatalog.IsRetryable("bidding_source_not_allowed"));

        // An unregistered code carries no reviewed retry decision, even when the
        // heuristic would land it in a retryable category.
        Assert.Equal(BiddingFailureCategory.Timeout, BiddingFailureCatalog.Classify("gateway_timeout"));
        Assert.False(BiddingFailureCatalog.IsRetryable("gateway_timeout"));
        Assert.False(BiddingFailureCatalog.IsRetryable(null));

        Assert.True(BiddingFailureCatalog.IsRetryable("rate_limited"));
        Assert.True(BiddingFailureCatalog.IsRetryable("timeout"));
        Assert.True(BiddingFailureCatalog.IsRetryable("transient_provider_failure"));
    }

    [Fact]
    public void Result_applies_the_cap_after_deduplication_so_duplicates_cannot_hide_distinct_notices()
    {
        var x = ValidNotice(
            title: "重复公告",
            url: "https://www.ccgp.gov.cn/notice/x.htm",
            publishedAt: new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero));
        var y = ValidNotice(
            title: "唯一公告",
            url: "https://www.ccgp.gov.cn/notice/y.htm",
            publishedAt: new DateTimeOffset(2026, 8, 8, 0, 0, 0, TimeSpan.Zero));

        var result = BiddingCollectionResult.Success("collect-1", [x, x, y], maxResults: 2);

        Assert.Equal(2, result.Notices.Count);
        Assert.Contains(result.Notices, notice => notice.Title == "唯一公告");

        // The cap can never exceed the compliance ceiling, whatever a caller asks.
        var many = Enumerable.Range(0, BiddingContractLimits.MaxResultsCeiling + 20)
            .Select(index => ValidNotice(
                title: $"公告 {index}",
                url: $"https://www.ccgp.gov.cn/notice/{index}.htm"))
            .ToArray();

        var capped = BiddingCollectionResult.Success("collect-1", many, maxResults: int.MaxValue);
        Assert.Equal(BiddingContractLimits.MaxResultsCeiling, capped.Notices.Count);
        Assert.Null(capped.Validate());
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
            "personal_data_detected", "provider_not_configured", "notification_not_configured",
            "notification_rejected", "duplicate_notice_suppressed",
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
