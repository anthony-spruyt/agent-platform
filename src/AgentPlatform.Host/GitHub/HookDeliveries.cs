namespace AgentPlatform.Host.GitHub;

public sealed record HookDelivery(long Id, string DeliveryGuid, DateTimeOffset DeliveredAt, bool Redelivery, int StatusCode, string Event);

public static class HookRedeliveryPlanner
{
    private const int MaxRedeliveries = 3;

    // GitHub groups a redelivery under the original's guid; a guid is judged by its newest attempt and retried through that attempt's id, oldest first
    public static IReadOnlyList<long> Plan(IEnumerable<HookDelivery> deliveries, DateTimeOffset windowStart)
    {
        ArgumentNullException.ThrowIfNull(deliveries);

        var plan = new List<HookDelivery>();
        foreach (var group in deliveries.GroupBy(d => d.DeliveryGuid, StringComparer.Ordinal))
        {
            var newest = group.MaxBy(d => d.DeliveredAt)!;
            if (newest.DeliveredAt < windowStart
                || newest.Event != "check_suite"
                || group.Any(d => d.StatusCode is >= 200 and < 300)
                || group.Count(d => d.Redelivery) >= MaxRedeliveries)
            {
                continue;
            }

            plan.Add(newest);
        }

        return [.. plan.OrderBy(d => d.DeliveredAt).Select(d => d.Id)];
    }
}
