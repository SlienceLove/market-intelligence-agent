using MarketIntelligence.Agent.Application;
using MarketIntelligence.Agent.Infrastructure;
using MarketIntelligence.Agent.Infrastructure.Bidding;
using MarketIntelligence.Agent.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddApplication();
builder.Services.AddInfrastructure();
builder.Services.AddBiddingCollectionInfrastructure();
builder.Services.AddHostedService<AgentWorker>();
builder.Services.AddHostedService<ScheduledBiddingCollectionService>();

var host = builder.Build();
host.Run();
