using System.Net;

namespace MarketIntelligence.Agent.Infrastructure.Media;

public static class MediaCollectionFailureCodes
{
    public const string Disabled = "provider_not_configured";
    public const string Unreachable = "source_unreachable";
    public const string Forbidden = "source_forbidden";
    public const string RateLimited = "source_rate_limited";
    public const string ServerError = "source_server_error";
    public const string Rejected = "source_rejected";
    public const string NotFound = "source_not_found";
    public const string Timeout = "source_timeout";
    public const string ResponseTooLarge = "source_response_too_large";
    public const string MediaTypeNotAllowed = "source_media_type_not_allowed";
    public const string RedirectLimitExceeded = "source_redirect_limit_exceeded";
    public const string InvalidRedirect = "source_redirect_invalid";
    public const string PortNotAllowed = "source_port_not_allowed";
    public const string InvalidResponse = "source_invalid_response";

    public static string FromStatusCode(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Forbidden,
        HttpStatusCode.NotFound => NotFound,
        HttpStatusCode.RequestTimeout => Timeout,
        (HttpStatusCode)429 => RateLimited,
        >= HttpStatusCode.InternalServerError and <= (HttpStatusCode)599 => ServerError,
        _ => Rejected
    };

    public static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or
            (HttpStatusCode)429 or
            >= HttpStatusCode.InternalServerError;
}
