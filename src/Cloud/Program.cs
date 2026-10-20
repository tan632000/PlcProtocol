using IndustrialIot.Cloud;
using IndustrialIot.Cloud.Live;
using IndustrialIot.Cloud.Security;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCloudPlatform(builder.Configuration);

var app = builder.Build();

// First, so the scheme and client address are right for everything after it (only from configured proxies).
app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapEndpointModules();

// A connection lives only as long as its token: SignalR closes it when the token expires.
app.MapHub<LiveHub>(LiveHub.Path, options => options.CloseOnAuthenticationExpiration = true)
    .RequireAuthorization(Policies.Viewer);

app.Run();

public partial class Program;
