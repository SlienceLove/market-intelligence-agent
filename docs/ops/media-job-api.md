# Media Job API

The API exposes a small, provider-neutral job boundary for Dify and approved
internal callers. The in-memory queue is a development and contract-test
implementation; production deployments must replace it with a durable queue
and status store.

## Authentication

Set `Media:BridgeApiKey` through an environment-specific configuration source.
Every media request must send the value in `X-Agent-Api-Key`. The repository
does not contain a real key, and an empty value disables access.

## Endpoints

`POST /api/media/jobs` accepts a `MediaJobRequest` and returns `202 Accepted`
with a `Location` header when the request is queued.

```json
{
  "jobId": "job-placeholder",
  "kind": "collection",
  "inputs": [
    {
      "uri": "https://approved.example/media-placeholder",
      "mediaType": "text/uri-list"
    }
  ],
  "correlationId": "correlation-placeholder",
  "idempotencyKey": "idempotency-placeholder"
}
```

`GET /api/media/jobs/{jobId}` returns the current status and safe failure
metadata. States are `accepted`, `running`, `succeeded`, `failed`, and
`cancelled`.

`POST /api/media/jobs/{jobId}/cancel` requests cancellation. A provider must
honor the cancellation token before producing an asset.

## Provider behavior

Collection and ASR HTTP adapters are disabled by default. Enable them only in
an environment with an approved endpoint, credential source, allowlist, data
retention policy, and explicit smoke-test authorization. Missing configuration
returns `provider_not_configured`; it is never reported as a successful asset.

The collector disables automatic redirects and validates every redirect target,
host, port, response size, timeout, and media type. It stores only controlled
asset references and status metadata, not response bodies.
