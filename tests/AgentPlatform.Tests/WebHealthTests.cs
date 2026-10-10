using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AgentPlatform.Tests;

public class WebHealthTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public WebHealthTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(builder => builder.UseSetting("AgentPlatform:Role", "Web"));

    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    public async Task HealthEndpoints_ReturnHealthy(string path)
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
