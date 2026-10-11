using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using WebApiTgBot.Infrastructure.Data;

namespace WebApiTgBot.Tests;

/// <summary>
/// Integration tests for the Health endpoint.
/// These tests start the full API in memory and call the real endpoint.
/// They use an in-memory database so no real PostgreSQL is needed to run tests.
/// </summary>
public class HealthControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public HealthControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetHealth_ReturnsOk_WhenDatabaseIsAvailable()
    {
        // Arrange — create an HTTP client that talks to the test server
        var client = _factory.CreateClient();

        // Act — call the health endpoint
        var response = await client.GetAsync("/api/health");

        // Assert — the endpoint should return 200 OK
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetHealth_ReturnsJsonContentType()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/api/health");

        // Assert — the response must be JSON
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GetHealth_ResponseContainsSuccessTrue()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/api/health");
        var content = await response.Content.ReadAsStringAsync();

        // Assert — the JSON body must contain "success":true (case-insensitive)
        Assert.Contains("\"success\":true", content, StringComparison.OrdinalIgnoreCase);
    }
}
