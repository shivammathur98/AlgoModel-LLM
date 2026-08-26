namespace AlgoTrader.Api.Infrastructure;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using System.Threading.Tasks;

public sealed class ApiKeyAuthenticationEndpointFilter : IEndpointFilter
{
    private readonly string _adminApiKey;

    public ApiKeyAuthenticationEndpointFilter(IConfiguration configuration)
    {
        _adminApiKey = configuration["ApiSettings:AdminApiKey"] ?? string.Empty;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        // If no API key is configured, warn but allow (for local dev fallback, or we can fail).
        // For strict security, we fail if it's not configured and they don't provide it.
        if (string.IsNullOrEmpty(_adminApiKey))
        {
            // In a real prod environment we might want to block startup if missing,
            // but returning 401 is safer than implicitly allowing public access.
            return Results.Unauthorized();
        }

        if (!context.HttpContext.Request.Headers.TryGetValue("X-Api-Key", out var extractedApiKey) ||
            extractedApiKey != _adminApiKey)
        {
            return Results.Unauthorized();
        }

        return await next(context);
    }
}
