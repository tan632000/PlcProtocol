// Edge agent entry point (.NET Generic Host). Configuration comes from appsettings.json and
// environment variables (Edge__...); AddEdgeAgent validates it before anything starts.
using IndustrialIot.Edge;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddEdgeAgent(builder.Configuration);

var host = builder.Build();
host.Run();
