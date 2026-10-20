// Edge agent entry point (.NET Generic Host). Configuration comes from appsettings.json and
// environment variables (Edge__...); AddEdgeAgent validates it before anything starts.
using IndustrialIot.Edge;

var builder = Host.CreateApplicationBuilder(args);

// Under the Windows Service Control Manager: service lifetime and Event Log logging; in a console (dotnet run)
// it does nothing. Relative paths still resolve against the exe's folder there, because the host ignores a
// System32 working directory for its content root; the installer also writes absolute paths for safety.
builder.Services.AddWindowsService(options => options.ServiceName = "IndustrialIot.Edge");
builder.Services.AddEdgeAgent(builder.Configuration);

var host = builder.Build();
host.Run();
