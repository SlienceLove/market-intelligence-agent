# .NET Agent Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Create a Rider-openable, buildable .NET 8 solution that hosts the custom services extending Dify while containing no live integrations or credentials.

**Architecture:** Build a modular monolith with API, application, infrastructure, and background-worker projects. API and Worker reference Application and Infrastructure; Infrastructure references Application; Application has no project reference. The API exposes an in-memory-testable health endpoint and makes no external requests.

**Tech Stack:** .NET 8 LTS, C# 12, ASP.NET Core Minimal API, Worker Service, xUnit, Microsoft.AspNetCore.Mvc.Testing, built-in dependency injection and configuration.

## Delivery Status (2026-07-31)

- [x] Task 1: The .NET 8 Rider solution and its five-project dependency graph are in place.
- [x] Task 2: The API exposes `GET /health`, with an in-memory contract test.
- [x] Task 3: The cancellable background host, safe configuration defaults, ignore rules, and README are in place.
- [x] The solution passed `dotnet test MarketIntelligence.Agent.sln --configuration Release --no-restore -m:1 -p:NuGetAudit=false`.
- [x] The source is published at https://github.com/SlienceLove/market-intelligence-agent on the `main` branch.

### Communication Smoke Test (2026-07-31)

- [x] `dotnet --list-sdks` confirmed SDK `8.0.423` is installed.
- [x] `dotnet restore MarketIntelligence.Agent.sln` restored all five projects successfully.
- [x] `dotnet build MarketIntelligence.Agent.sln --configuration Release -m:1 -p:NuGetAudit=false` built all five projects with 0 warnings and 0 errors.
- [x] `dotnet test MarketIntelligence.Agent.sln --configuration Release --no-build -m:1 -p:NuGetAudit=false --filter "FullyQualifiedName~HealthEndpointTests"` passed the focused health endpoint test (1 passed, 0 failed, 0 skipped).
- [x] The in-process API communication path is operational: the test host started, `GET /health` returned HTTP 200, and the response matched the `{"status":"ready"}` contract.
The task details below are retained as the implementation record for the delivered foundation. Future feature work should be added as separate, scoped plans rather than modifying the completed baseline tasks.

## Global Constraints

- Use the locally installed .NET SDK 8.0.423 and target net8.0 in every project.
- Dify remains responsible for conversation, RAG, knowledge-base storage, and workflow logic; this repository contains extension services only.
- Use C# as the only source language in src/ and tests/; do not add Python or a local model runtime.
- Do not commit API keys, endpoint URLs, connection strings, scraped data, media files, model files, or Rider .idea/ state.
- API and Worker may reference Application and Infrastructure; Infrastructure may reference Application; Application references no project; Tests reference API and Application only.
- The initial application must not make network calls other than a test client's in-memory request to /health.
- In this local environment, add -m:1 to all build and test commands to avoid concurrent MSBuild intermediate-file locks; add -p:NuGetAudit=false when a command would otherwise query unavailable NuGet vulnerability metadata.

---

## Planned File Structure

| Path | Responsibility |
| --- | --- |
| MarketIntelligence.Agent.sln | Rider and .NET solution entry point. |
| src/MarketIntelligence.Agent.Application/ServiceCollectionExtensions.cs | Application-layer composition boundary. |
| src/MarketIntelligence.Agent.Infrastructure/ServiceCollectionExtensions.cs | Provider-facing composition boundary. |
| src/MarketIntelligence.Agent.Api/Program.cs | API composition and /health. |
| src/MarketIntelligence.Agent.Worker/AgentWorker.cs | Background host that waits for cancellation. |
| tests/MarketIntelligence.Agent.Tests/HealthEndpointTests.cs | In-memory health-contract test. |
| .gitignore | Excludes secrets, local state, output, and Rider state. |
| README.md | Defines project boundary and local commands. |

### Task 1: Create the Solution and Enforce the Dependency Graph

**Files:**
- Create: MarketIntelligence.Agent.sln
- Create: src/MarketIntelligence.Agent.Api/MarketIntelligence.Agent.Api.csproj
- Create: src/MarketIntelligence.Agent.Application/MarketIntelligence.Agent.Application.csproj
- Create: src/MarketIntelligence.Agent.Infrastructure/MarketIntelligence.Agent.Infrastructure.csproj
- Create: src/MarketIntelligence.Agent.Worker/MarketIntelligence.Agent.Worker.csproj
- Create: tests/MarketIntelligence.Agent.Tests/MarketIntelligence.Agent.Tests.csproj

**Interfaces:**
- Consumes: .NET SDK 8.0.423.
- Produces: a net8.0 solution with the dependency direction required above.

- [ ] **Step 1: Verify the required SDK is installed**

Run: dotnet --list-sdks

Expected: output includes 8.0.423.

- [ ] **Step 2: Generate the projects and add them to the solution**

~~~powershell
dotnet new sln -n MarketIntelligence.Agent
dotnet new webapi --no-https --no-openapi --framework net8.0 --output src/MarketIntelligence.Agent.Api
dotnet new classlib --framework net8.0 --output src/MarketIntelligence.Agent.Application
dotnet new classlib --framework net8.0 --output src/MarketIntelligence.Agent.Infrastructure
dotnet new worker --framework net8.0 --output src/MarketIntelligence.Agent.Worker
dotnet new xunit --framework net8.0 --output tests/MarketIntelligence.Agent.Tests
dotnet sln MarketIntelligence.Agent.sln add src/MarketIntelligence.Agent.Api/MarketIntelligence.Agent.Api.csproj src/MarketIntelligence.Agent.Application/MarketIntelligence.Agent.Application.csproj src/MarketIntelligence.Agent.Infrastructure/MarketIntelligence.Agent.Infrastructure.csproj src/MarketIntelligence.Agent.Worker/MarketIntelligence.Agent.Worker.csproj tests/MarketIntelligence.Agent.Tests/MarketIntelligence.Agent.Tests.csproj
~~~

- [ ] **Step 3: Add only the allowed project references**

~~~powershell
dotnet add src/MarketIntelligence.Agent.Api/MarketIntelligence.Agent.Api.csproj reference src/MarketIntelligence.Agent.Application/MarketIntelligence.Agent.Application.csproj src/MarketIntelligence.Agent.Infrastructure/MarketIntelligence.Agent.Infrastructure.csproj
dotnet add src/MarketIntelligence.Agent.Infrastructure/MarketIntelligence.Agent.Infrastructure.csproj reference src/MarketIntelligence.Agent.Application/MarketIntelligence.Agent.Application.csproj
dotnet add src/MarketIntelligence.Agent.Worker/MarketIntelligence.Agent.Worker.csproj reference src/MarketIntelligence.Agent.Application/MarketIntelligence.Agent.Application.csproj src/MarketIntelligence.Agent.Infrastructure/MarketIntelligence.Agent.Infrastructure.csproj
dotnet add tests/MarketIntelligence.Agent.Tests/MarketIntelligence.Agent.Tests.csproj reference src/MarketIntelligence.Agent.Api/MarketIntelligence.Agent.Api.csproj src/MarketIntelligence.Agent.Application/MarketIntelligence.Agent.Application.csproj
~~~

- [ ] **Step 4: Set project identity and remove generated sample files**

Add this property group to every project file, changing Application to the appropriate project name. Delete the two Class1.cs files, the API weather sample, the worker sample, and the xUnit template test.

~~~xml
<PropertyGroup>
  <TargetFramework>net8.0</TargetFramework>
  <ImplicitUsings>enable</ImplicitUsings>
  <Nullable>enable</Nullable>
  <RootNamespace>MarketIntelligence.Agent.Application</RootNamespace>
  <AssemblyName>MarketIntelligence.Agent.Application</AssemblyName>
</PropertyGroup>
~~~

- [ ] **Step 5: Restore and build the project graph**

Run: dotnet build MarketIntelligence.Agent.sln --configuration Release

Expected: exit code 0 with all five projects built. If NuGet access is unavailable, report the restore error without substituting packages.

- [ ] **Step 6: Commit the solution graph**

~~~powershell
git add MarketIntelligence.Agent.sln src tests
git commit -m "build: add agent solution structure"
~~~

### Task 2: Compose the API and Prove the Health Contract

**Files:**
- Create: src/MarketIntelligence.Agent.Application/ServiceCollectionExtensions.cs
- Create: src/MarketIntelligence.Agent.Infrastructure/ServiceCollectionExtensions.cs
- Modify: src/MarketIntelligence.Agent.Api/Program.cs
- Create: tests/MarketIntelligence.Agent.Tests/HealthEndpointTests.cs
- Modify: tests/MarketIntelligence.Agent.Tests/MarketIntelligence.Agent.Tests.csproj

**Interfaces:**
- Consumes: IServiceCollection AddApplication(this IServiceCollection services) and IServiceCollection AddInfrastructure(this IServiceCollection services).
- Produces: GET /health, returning HTTP 200 OK and { "status": "ready" }.

- [ ] **Step 1: Write the failing in-memory endpoint test**

Add Microsoft.AspNetCore.Mvc.Testing version 8.0.23 to the test project and create this test using System.Net and Microsoft.AspNetCore.Mvc.Testing:

~~~powershell
dotnet add tests/MarketIntelligence.Agent.Tests/MarketIntelligence.Agent.Tests.csproj package Microsoft.AspNetCore.Mvc.Testing --version 8.0.23
~~~

~~~csharp
public sealed class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public HealthEndpointTests(WebApplicationFactory<Program> factory) => _client = factory.CreateClient();

    [Fact]
    public async Task GetHealth_returns_ready_status()
    {
        var response = await _client.GetAsync("/health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"status\":\"ready\"", body, StringComparison.Ordinal);
    }
}
~~~

- [ ] **Step 2: Run the focused test and confirm it fails**

Run: dotnet test tests/MarketIntelligence.Agent.Tests/MarketIntelligence.Agent.Tests.csproj --filter FullyQualifiedName~HealthEndpointTests

Expected: FAIL because the generated API does not expose the specified contract.

- [ ] **Step 3: Implement the composition methods and endpoint**

Create one extension in each of Application and Infrastructure with this exact signature; each returns the input service collection without registering an external client:

~~~csharp
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services) => services;
}
~~~

Replace the API template program with the following, using the two extension namespaces. The public partial type enables the in-memory test host:

~~~csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddApplication();
builder.Services.AddInfrastructure();

var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "ready" }));
app.Run();

public partial class Program { }
~~~

- [ ] **Step 4: Run the focused test and then all tests**

~~~powershell
dotnet test tests/MarketIntelligence.Agent.Tests/MarketIntelligence.Agent.Tests.csproj --filter FullyQualifiedName~HealthEndpointTests
dotnet test MarketIntelligence.Agent.sln --configuration Release
~~~

Expected: both commands exit with 0; no test contacts an external service.

- [ ] **Step 5: Commit the API composition root**

~~~powershell
git add src/MarketIntelligence.Agent.Application src/MarketIntelligence.Agent.Infrastructure src/MarketIntelligence.Agent.Api tests/MarketIntelligence.Agent.Tests
git commit -m "feat: add agent health endpoint"
~~~

### Task 3: Add the Background Host and Repository Defaults

**Files:**
- Modify: src/MarketIntelligence.Agent.Worker/Program.cs
- Create: src/MarketIntelligence.Agent.Worker/AgentWorker.cs
- Create: src/MarketIntelligence.Agent.Api/appsettings.json
- Create: src/MarketIntelligence.Agent.Worker/appsettings.json
- Create: .gitignore
- Create: README.md

**Interfaces:**
- Consumes: AddApplication() and AddInfrastructure() from Task 2.
- Produces: AgentWorker : BackgroundService, which logs startup and waits for cancellation without starting a schedule or network client.

- [ ] **Step 1: Write the worker host before adding infrastructure behavior**

Create AgentWorker with this implementation:

~~~csharp
public sealed class AgentWorker(ILogger<AgentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Agent worker started.");
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }
}
~~~

Replace the worker template program with a host that calls AddApplication(), AddInfrastructure(), and AddHostedService<AgentWorker>() before Build().Run().

- [ ] **Step 2: Prove the worker compiles without live side effects**

Run: dotnet build src/MarketIntelligence.Agent.Worker/MarketIntelligence.Agent.Worker.csproj --configuration Release

Expected: exit code 0; no Dify, collection, ASR, OCR, FFmpeg, PostgreSQL, or delivery adapter is instantiated.

- [ ] **Step 3: Add safe configuration, ignore, and README files**

Each appsettings.json contains only Logging:LogLevel:Default set to Information and Logging:LogLevel:Microsoft.Hosting.Lifetime set to Information. .gitignore excludes bin/, obj/, .idea/, .vs/, *.user, *.suo, .env, appsettings.*.local.json, data/, media/, and artifacts/. README.md identifies Dify as the RAG/workflow layer and lists:

~~~powershell
dotnet restore MarketIntelligence.Agent.sln
dotnet build MarketIntelligence.Agent.sln --configuration Release
dotnet test MarketIntelligence.Agent.sln --configuration Release
~~~

- [ ] **Step 4: Run final build, tests, and solution inspection**

~~~powershell
dotnet build MarketIntelligence.Agent.sln --configuration Release
dotnet test MarketIntelligence.Agent.sln --configuration Release
dotnet sln MarketIntelligence.Agent.sln list
git status --short --branch
~~~

Expected: build and tests exit with 0; the solution lists API, Application, Infrastructure, Worker, and Tests exactly once; only the newly created files are pending before commit.

- [ ] **Step 5: Document the Rider entry point and commit**

Add this exact sentence under a Development heading in README.md:

~~~markdown
Open MarketIntelligence.Agent.sln in JetBrains Rider, then run the API or Worker launch configuration generated by Rider.
~~~

Commit:

~~~powershell
git add .gitignore README.md src/MarketIntelligence.Agent.Worker src/MarketIntelligence.Agent.Api/appsettings.json src/MarketIntelligence.Agent.Worker/appsettings.json
git commit -m "feat: add background agent host"
~~~

## Self-Review

**Spec coverage:** This plan creates the approved .NET 8 modular-monolith structure, Rider solution, API health proof, background-host proof, safe configuration boundary, test project, ignore rules, and Git-ready documentation. It preserves Dify as the RAG/workflow system and omits live external integrations, credentials, scraping, ASR, OCR, FFmpeg, and deployment automation as required by the initial scope.

**Placeholder scan:** Every project, file, command, endpoint response, interface, expected test outcome, and commit message is explicit.

**Type consistency:** AddApplication, AddInfrastructure, Program, and AgentWorker use the same names and contracts in every task.
