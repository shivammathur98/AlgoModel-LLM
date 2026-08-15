namespace AlgoTrader.Api.Endpoints;

using AlgoTrader.Application.Status;

/// <summary>
/// Status and monitoring endpoints (§31, §35). Minimal API style for lightweight operational queries.
/// </summary>
public static class StatusEndpoints
{
    /// <summary>Maps GET /api/status to return the current system status.</summary>
    public static IEndpointRouteBuilder MapStatusEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/status", (ISystemStatusService statusService) =>
            Results.Ok(statusService.GetStatus()))
            .WithName("GetSystemStatus")
            .WithDescription("Returns current platform mode, safety state, kill switch status, and uptime.");

        return endpoints;
    }
}
