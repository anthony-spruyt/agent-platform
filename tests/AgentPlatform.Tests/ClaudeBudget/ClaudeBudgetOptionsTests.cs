using AgentPlatform.Host.ClaudeBudget;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Tests.ClaudeBudget;

public class ClaudeBudgetOptionsTests
{
    private static ServiceProvider Build(Dictionary<string, string?>? settings = null)
    {
        var services = new ServiceCollection();
        services.AddClaudeBudget(new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build());
        return services.BuildServiceProvider();
    }

    private static ClaudeBudgetOptions Options(Dictionary<string, string?>? settings = null)
    {
        using var provider = Build(settings);
        return provider.GetRequiredService<IOptions<ClaudeBudgetOptions>>().Value;
    }

    [Fact]
    public void TheGlobalCaps_DefaultToTwoAnHourAndEightADay()
    {
        var caps = Options();

        Assert.Equal((2, 8), (caps.MaxPerHour, caps.MaxPerDay));
    }

    [Fact]
    public void TheGlobalCapsAndEachKindsCaps_BindFromTheClaudeBudgetSection()
    {
        var options = Options(new()
        {
            ["ClaudeBudget:MaxPerHour"] = "4",
            ["ClaudeBudget:MaxPerDay"] = "32",
            ["ClaudeBudget:Kinds:kind-a:MaxPerSubjectPerDay"] = "5",
            ["ClaudeBudget:Kinds:kind-a:MaxFailedAttemptsPerRevision"] = "3",
            ["ClaudeBudget:Kinds:kind-b:MaxPerSubjectPerDay"] = "7",
        });

        var a = options.For("kind-a");
        var b = options.For("kind-b");

        Assert.Equal((4, 32), (options.MaxPerHour, options.MaxPerDay));
        Assert.Equal((5, 3), (a.MaxPerSubjectPerDay, a.MaxFailedAttemptsPerRevision));
        Assert.Equal((7, 2), (b.MaxPerSubjectPerDay, b.MaxFailedAttemptsPerRevision));
    }

    [Fact]
    public void AKindWithoutASection_FallsBackToTwoRunsPerSubjectAndTwoFailedAttempts()
    {
        var options = Options(new() { ["ClaudeBudget:Kinds:kind-a:MaxPerSubjectPerDay"] = "5" });

        var other = options.For("kind-b");

        Assert.Equal((2, 2), (other.MaxPerSubjectPerDay, other.MaxFailedAttemptsPerRevision));
    }

    [Fact]
    public void TheKindNames_AreMatchedExactly()
    {
        var options = Options(new() { ["ClaudeBudget:Kinds:kind-a:MaxPerSubjectPerDay"] = "5" });

        Assert.Equal(2, options.For("KIND-A").MaxPerSubjectPerDay);
    }

    [Theory]
    [InlineData("ClaudeBudget:MaxPerHour", "0")]
    [InlineData("ClaudeBudget:MaxPerDay", "0")]
    [InlineData("ClaudeBudget:Kinds:kind-a:MaxPerSubjectPerDay", "0")]
    [InlineData("ClaudeBudget:Kinds:kind-a:MaxFailedAttemptsPerRevision", "-1")]
    public void ACapBelowOne_FailsAtStartup(string key, string value)
    {
        Assert.Throws<InvalidOperationException>(() => Build(new() { [key] = value }));
    }
}
