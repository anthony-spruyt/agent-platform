using AgentPlatform.Host.Agents;

namespace AgentPlatform.Tests.Agents;

public class AgentRunUsageTests
{
    [Fact]
    public void ToCostUsd_ConvertsAnOrdinaryCost()
    {
        Assert.Equal(0.1m, AgentRunUsage.ToCostUsd(0.1));
    }

    [Fact]
    public void ToCostUsd_KeepsAMissingCostMissing()
    {
        Assert.Null(AgentRunUsage.ToCostUsd(null));
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.NaN)]
    [InlineData(double.MaxValue)]
    [InlineData(-double.MaxValue)]
    [InlineData(1e29)]
    [InlineData(1e10)]
    public void ToCostUsd_TreatsACostThatCannotBeStoredAsUnknown(double cost)
    {
        Assert.Null(AgentRunUsage.ToCostUsd(cost));
    }

    [Fact]
    public void ToCostUsd_KeepsTheLargestStorableCost()
    {
        Assert.Equal(9_999_999_999m, AgentRunUsage.ToCostUsd(9_999_999_999d));
    }
}
