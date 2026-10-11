using System.Text.Json.Serialization;

namespace AgentPlatform.Host.Agents;

[JsonConverter(typeof(JsonStringEnumConverter<AgentTier>))]
public enum AgentTier
{
    [JsonStringEnumMemberName("read")]
    Read,

    [JsonStringEnumMemberName("cluster-read")]
    ClusterRead,
}
