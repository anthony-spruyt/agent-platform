using AgentPlatform.Host.GitHub;

namespace AgentPlatform.Tests.Sweeper;

public class HookRedeliveryPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Start = Now.AddHours(-1);

    private static HookDelivery Delivery(long id, string guid, int minutesAgo, int status = 502, bool redelivery = false, string eventName = "check_suite") =>
        new(id, guid, Now.AddMinutes(-minutesAgo), redelivery, status, eventName);

    [Fact]
    public void AFailedCheckSuiteDelivery_IsPlannedForRedelivery()
    {
        var plan = HookRedeliveryPlanner.Plan([Delivery(1, "g1", 10, 502)], Start);

        Assert.Equal([1L], plan);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(202)]
    [InlineData(299)]
    public void ASuccessfulDelivery_IsLeftAlone(int status)
    {
        Assert.Empty(HookRedeliveryPlanner.Plan([Delivery(1, "g1", 10, status)], Start));
    }

    [Fact]
    public void AGuidThatLaterSucceeded_IsSkipped_WhateverHappenedBefore()
    {
        var plan = HookRedeliveryPlanner.Plan(
            [Delivery(3, "g1", 5, 200, redelivery: true), Delivery(2, "g1", 20, 502, redelivery: true), Delivery(1, "g1", 30, 502)],
            Start);

        Assert.Empty(plan);
    }

    [Fact]
    public void AGuidThatAlreadyHadThreeRedeliveries_IsGivenUp()
    {
        var plan = HookRedeliveryPlanner.Plan(
            [
                Delivery(4, "g1", 5, 502, redelivery: true),
                Delivery(3, "g1", 15, 502, redelivery: true),
                Delivery(2, "g1", 25, 502, redelivery: true),
                Delivery(1, "g1", 35, 502),
            ],
            Start);

        Assert.Empty(plan);
    }

    [Fact]
    public void AGuidWithTwoRedeliveries_GetsOneMoreAttempt_OnItsNewestDelivery()
    {
        var plan = HookRedeliveryPlanner.Plan(
            [Delivery(3, "g1", 5, 502, redelivery: true), Delivery(2, "g1", 15, 502, redelivery: true), Delivery(1, "g1", 25, 502)],
            Start);

        Assert.Equal([3L], plan);
    }

    [Fact]
    public void AGuidIsJudgedByItsNewestAttempt_SoARetriedGuidWhoseOriginalLeftTheWindowIsRetriedAgain()
    {
        var plan = HookRedeliveryPlanner.Plan(
            [Delivery(2, "g1", 5, 502, redelivery: true), Delivery(1, "g1", 90, 502)],
            Start);

        Assert.Equal([2L], plan);
    }

    [Fact]
    public void AGuidWhoseNewestAttemptIsOlderThanTheWindow_IsNotConsidered()
    {
        var plan = HookRedeliveryPlanner.Plan(
            [Delivery(2, "g1", 80, 502, redelivery: true), Delivery(1, "g1", 90, 502)],
            Start);

        Assert.Empty(plan);
    }

    [Fact]
    public void ARedeliveryWhoseOriginalIsNotInTheList_IsRetriedOnItsOwnId()
    {
        Assert.Equal([2L], HookRedeliveryPlanner.Plan([Delivery(2, "g1", 5, 502, redelivery: true)], Start));
    }

    [Fact]
    public void OtherEvents_AreNeverRedelivered()
    {
        Assert.Empty(HookRedeliveryPlanner.Plan([Delivery(1, "g1", 10, 500, eventName: "ping")], Start));
    }

    [Fact]
    public void EachFailedGuid_IsPlannedOnce_OldestFirst()
    {
        var plan = HookRedeliveryPlanner.Plan(
            [Delivery(3, "g3", 5), Delivery(2, "g2", 15), Delivery(1, "g1", 25), Delivery(4, "g2", 4, redelivery: true)],
            Start);

        Assert.Equal([1L, 3L, 4L], plan);
    }
}
