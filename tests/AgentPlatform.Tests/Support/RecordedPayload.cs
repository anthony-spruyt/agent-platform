using Google.Protobuf;
using Temporalio.Api.Common.V1;
using Temporalio.Converters;

namespace AgentPlatform.Tests.Support;

internal static class RecordedPayload
{
    public static T Read<T>(string json)
    {
        var payload = new Payload { Data = ByteString.CopyFromUtf8(json) };
        payload.Metadata["encoding"] = ByteString.CopyFromUtf8("json/plain");
        return DataConverter.Default.PayloadConverter.ToValue<T>(payload);
    }
}
