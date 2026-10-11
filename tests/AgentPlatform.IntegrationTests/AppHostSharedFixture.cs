namespace AgentPlatform.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class AppHostSharedFixture : ICollectionFixture<AppHostFixture>
{
    public const string Name = "AppHost";
}
