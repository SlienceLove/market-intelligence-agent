# Market Intelligence Agent Design

## Purpose

This repository contains the services that extend Dify for the marketing
intelligence agent. Dify remains responsible for the conversation experience,
knowledge bases, RAG, and workflow orchestration. This codebase provides only
the capabilities that Dify does not provide directly:

- collecting WeChat Channels links and approved tender information;
- submitting audio to a transcription provider and video frames to an OCR provider;
- assembling speech and video with FFmpeg;
- executing scheduled collection tasks and delivering results to configured channels.

The first deliverable is a source-level, Rider-openable .NET solution. It does
not contain credentials, production endpoint URLs, scraped data, or model files.

## Technology Choices

- Runtime and language: .NET 8 LTS and C#.
- HTTP host: ASP.NET Core Web API.
- Scheduling: Quartz.NET, using cron schedules and persistent job state when a
  database is configured.
- Collection: HttpClient for supported HTTP endpoints and Playwright for
  browser-dependent sources. Collection must follow each source's applicable
  terms, permissions, and rate limits.
- Media: FFmpeg is executed as an external process with timeouts and bounded
  temporary storage.
- External AI: ASR and OCR use provider-specific HTTP adapters behind stable
  application interfaces. No local model runtime is included.
- Persistence: PostgreSQL stores task definitions, executions, delivery status,
  and audit metadata. Dify remains the system of record for knowledge-base
  content. Redis is intentionally excluded until queue throughput requires it.

Python is not selected as the primary runtime because the confirmed maintenance
IDE is Rider and the required work is primarily service integration and
automation, rather than local model training. A separate Python worker may be
introduced later only for a required library that has no viable .NET equivalent.

## Solution Layout

```text
MarketIntelligence.Agent.sln
src/
  MarketIntelligence.Agent.Api/
  MarketIntelligence.Agent.Application/
  MarketIntelligence.Agent.Infrastructure/
  MarketIntelligence.Agent.Worker/
tests/
  MarketIntelligence.Agent.Tests/
docs/
```

`Api` exposes health checks and authenticated endpoints that Dify workflows or
internal callers invoke. `Application` contains use cases and interfaces, and
has no dependency on provider SDKs. `Infrastructure` implements those
interfaces for Dify, collection sources, ASR, OCR, FFmpeg, PostgreSQL, and
delivery channels. `Worker` runs scheduled and long-running work outside the
HTTP request path. `Tests` validates application behavior and adapter contracts.

This is a modular monolith. It allows a single engineer to ship the initial
modules without the operational cost of multiple independently deployed
services, while preserving boundaries for later extraction.

## Data Flow

1. A user configures a workflow in Dify or an internal caller creates a task.
2. Dify calls the API through an authenticated HTTP endpoint, or Quartz triggers
   a scheduled task in the worker.
3. The application layer selects the required adapter and starts a tracked job.
4. The adapter collects or transforms data, then stores job metadata and output
   references in PostgreSQL. Temporary media is removed after processing.
5. The worker sends the result to the configured delivery channel or returns it
   to the Dify workflow. Knowledge-base ingestion stays in Dify's supported
   import or API path.

## Reliability and Security

- API keys, connection strings, and webhook secrets are supplied through
  environment variables or a deployment secret store; they are never committed.
- Every task has a correlation identifier, structured logs, a final status, and
  an error reason suitable for operational review.
- Transient network failures use bounded exponential retries. Permanent source
  failures are recorded without blocking unrelated tasks.
- Repeated requests use idempotency keys so a Dify retry cannot create duplicate
  deliveries.
- FFmpeg and browser processes have execution limits, cancellation, and cleanup.
- The initial scaffold contains no production credentials and does not make live
  calls to Dify, sources, ASR, OCR, or delivery services.

## Testing Strategy

- Unit tests cover application use cases, routing decisions, validation, and
  retry classification.
- Integration tests use mocked HTTP handlers and temporary storage; they do not
  contact external platforms.
- Provider adapters are contract-tested against recorded or local test fixtures.
- The initial solution must restore, build, and run its test project from the
  command line before it is considered ready.

## Initial Scope

The initial repository setup creates the solution, the five projects above,
basic dependency direction, health-check endpoint, configuration templates,
test project, README, and Git ignore rules. It deliberately does not implement
live collection, ASR, OCR, video generation, Dify credentials, or deployment
automation. Those functions will be added in the implementation plan in the
order defined by the approved project schedule.
