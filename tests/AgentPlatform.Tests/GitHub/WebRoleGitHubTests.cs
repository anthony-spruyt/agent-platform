using AgentPlatform.Host.GitHub;
using AgentPlatform.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Tests.GitHub;

public class WebRoleGitHubTests
{
    private static WebApplicationFactory<Program> Factory(string? secret, string? privateKey = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("AgentPlatform:Role", "Web");
            builder.UseSetting("AgentGateway:SessionTokenKey", "0123456789abcdef0123456789abcdef");
            if (secret is not null)
            {
                builder.UseSetting("GitHub:WebhookSecret", secret);
            }

            if (privateKey is not null)
            {
                builder.UseSetting("GitHub:PrivateKey", privateKey);
                builder.UseSetting("GitHub:AppId", "123");
            }
        });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void WithoutAWebhookSecret_TheWebRoleFailsAtStartup(string? secret)
    {
        using var factory = Factory(secret);

        var failure = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("WebhookSecret", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWebRole_NeverRegistersTheGitHubAppClientOrItsKey()
    {
        using var rsa = GitHubPayloads.NewKey();
        using var factory = Factory(GitHubPayloads.Secret, rsa.ExportRSAPrivateKeyPem());

        using var scope = factory.Services.CreateScope();

        Assert.Null(scope.ServiceProvider.GetService<IGitHubApiClient>());
        Assert.Null(scope.ServiceProvider.GetService<IGitHubInstallationTokenProvider>());
        Assert.Empty(scope.ServiceProvider.GetRequiredService<IOptions<GitHubAppOptions>>().Value.PrivateKey);
        Assert.Equal(GitHubPayloads.Secret, scope.ServiceProvider.GetRequiredService<IOptions<GitHubWebhookOptions>>().Value.WebhookSecret);
    }

    [Fact]
    public void TheWebRole_UsesTheTemporalSignaler()
    {
        using var factory = Factory(GitHubPayloads.Secret);

        using var scope = factory.Services.CreateScope();

        Assert.IsType<TemporalRenovateTriageSignaler>(scope.ServiceProvider.GetRequiredService<IRenovateTriageSignaler>());
    }
}
