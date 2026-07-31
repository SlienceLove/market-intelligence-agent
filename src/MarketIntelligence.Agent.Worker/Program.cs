using MarketIntelligence.Agent.Application;
using MarketIntelligence.Agent.Infrastructure;
using MarketIntelligence.Agent.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddApplication();
builder.Services.AddInfrastructure();
builder.Services.AddHostedService<AgentWorker>();

var host = builder.Build();
host.Run();
