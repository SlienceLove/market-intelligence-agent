using MarketIntelligence.Agent.Application;
using MarketIntelligence.Agent.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddApplication();
builder.Services.AddInfrastructure();

var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "ready" }));
app.Run();

public partial class Program { }
