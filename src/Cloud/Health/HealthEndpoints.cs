namespace IndustrialIot.Cloud.Health;

/// <summary><c>/health/live</c> and <c>/health/ready</c>: anonymous, answered from memory only.</summary>
internal sealed class HealthEndpoints(ReadinessState state) : IEndpointModule
{
    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health/live", () => Results.Json(new { status = "live" })).AllowAnonymous();

        endpoints.MapGet("/health/ready", () => state.IsReady
                ? Results.Json(new { status = "ready" })
                : Results.Json(new { status = "not_ready", checks = state.Snapshot() }, statusCode: StatusCodes.Status503ServiceUnavailable))
            .AllowAnonymous();
    }
}
