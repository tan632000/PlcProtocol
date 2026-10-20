using Microsoft.AspNetCore.Mvc;

namespace IndustrialIot.Cloud.Api;

/// <summary>
/// RFC 7807 bodies with short machine-readable titles. Bodies never carry token contents, exception
/// messages or stack traces.
/// </summary>
public static class ProblemMapping
{
    public const string ContentType = "application/problem+json";

    public static IServiceCollection AddCloudProblemDetails(this IServiceCollection services) =>
        services.AddProblemDetails(options => options.CustomizeProblemDetails = ctx =>
        {
            if (ctx.ProblemDetails.Status == StatusCodes.Status500InternalServerError)
            {
                ctx.ProblemDetails.Title = "internal_error";
                ctx.ProblemDetails.Detail = null;
                ctx.ProblemDetails.Type = null;

                // Development adds the exception (message, stack, headers) as an extension; never send it.
                ctx.ProblemDetails.Extensions.Remove("exception");
            }
        });

    public static Task WriteAsync(HttpContext context, int status, string title, string? detail = null)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(
            new ProblemDetails { Status = status, Title = title, Detail = detail },
            options: null,
            contentType: ContentType);
    }
}
