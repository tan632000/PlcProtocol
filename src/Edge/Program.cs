// Edge agent entry point (.NET Generic Host).
// Wave 1 only builds the host; drivers and hosted services are registered in later tasks/waves.
var builder = Host.CreateApplicationBuilder(args);

var host = builder.Build();
host.Run();
