using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using WebApiTgBot.Common.Models;

namespace WebApiTgBot.Modules.Health.Controllers;

/// <summary>
/// Health check endpoint for the platform.
/// Used to verify that the API and database are running correctly.
/// Safe to expose — does NOT return any internal system details.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    private readonly HealthCheckService _healthCheckService;

    public HealthController(HealthCheckService healthCheckService)
    {
        _healthCheckService = healthCheckService;
    }

    /// <summary>
    /// Returns the health status of the API and its database connection.
    /// </summary>
    /// <returns>200 OK when healthy, 503 Service Unavailable when not.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<HealthStatusResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<HealthStatusResponse>), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetHealth(CancellationToken cancellationToken)
    {
        var report = await _healthCheckService.CheckHealthAsync(cancellationToken: cancellationToken);

        var response = new HealthStatusResponse
        {
            Status = report.Status.ToString(),
            // Return each check name and its status — never expose internal details
            Checks = report.Entries.Select(e => new HealthCheckEntry
            {
                Name = e.Key,
                Status = e.Value.Status.ToString()
            }).ToList()
        };

        if (report.Status == HealthStatus.Healthy)
        {
            return Ok(ApiResponse<HealthStatusResponse>.Ok(response, "All systems operational."));
        }

        return StatusCode(StatusCodes.Status503ServiceUnavailable,
            new ApiResponse<HealthStatusResponse>
            {
                Success = false,
                Message = "One or more health checks failed.",
                Data = response
            });
    }
}

/// <summary>Response payload for the health endpoint.</summary>
public record HealthStatusResponse
{
    public string Status { get; init; } = string.Empty;
    public List<HealthCheckEntry> Checks { get; init; } = [];
}

/// <summary>A single health check result entry.</summary>
public record HealthCheckEntry
{
    public string Name { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
}
