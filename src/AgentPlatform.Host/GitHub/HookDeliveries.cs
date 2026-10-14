namespace AgentPlatform.Host.GitHub;

public sealed record HookDelivery(long Id, string DeliveryGuid, DateTimeOffset DeliveredAt, bool Redelivery, int StatusCode, string Event);

public static class HookRedeliveryPlanner
{
    public const int DefaultMaxRedeliveries = 3;

    // Returns the original delivery ids to redeliver, oldest first: GitHub groups a redelivery under the original's guid
    public static IReadOnlyList<long> Plan(IEnumerable<HookDelivery> deliveries, DateTimeOffset windowStart, int maxRedeliveries = DefaultMaxRedeliveries)
    {
        ArgumentNullException.ThrowIfNull(deliveries);

        var plan = new List<(DateTimeOffset At, long Id)>();
        foreach (var group in deliveries.GroupBy(d => d.DeliveryGuid, StringComparer.Ordinal))
        {
            var original = group.FirstOrDefault(d => !d.Redelivery);
            if (original is null
                || original.DeliveredAt < windowStart
                || original.Event != "check_suite"
                || group.Any(d => d.StatusCode is >= 200 and < 300)
                || group.Count(d => d.Redelivery) >= maxRedeliveries)
            {
                continue;
            }

            plan.Add((original.DeliveredAt, original.Id));
        }

        return [.. plan.OrderBy(p => p.At).Select(p => p.Id)];
    }
}
