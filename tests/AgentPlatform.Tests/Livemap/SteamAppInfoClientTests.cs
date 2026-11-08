using System.Net;
using System.Text.Json.Nodes;
using AgentPlatform.Host.Livemap;
using AgentPlatform.Tests.Support;

namespace AgentPlatform.Tests.Livemap;

public class SteamAppInfoClientTests
{
    private const string BuildId = "26430253";
    private const string Gid = "8886672487109033979";
    private const string UpdatedAt = "1793801894";

    private static string Body(string? build = BuildId, string? gid = Gid, string? updated = UpdatedAt, string status = "success")
    {
        var branch = new JsonObject();
        if (build is not null)
        {
            branch["buildid"] = build;
        }

        if (updated is not null)
        {
            branch["timeupdated"] = updated;
        }

        var manifest = new JsonObject();
        if (gid is not null)
        {
            manifest["gid"] = gid;
        }

        return new JsonObject
        {
            ["status"] = status,
            ["data"] = new JsonObject
            {
                ["2278520"] = new JsonObject
                {
                    ["depots"] = new JsonObject
                    {
                        ["branches"] = new JsonObject { ["public"] = branch },
                        ["2278521"] = new JsonObject { ["manifests"] = new JsonObject { ["public"] = manifest } },
                    },
                },
            },
        }.ToJsonString();
    }

    private static (SteamAppInfoClient Client, StubHttpHandler Handler) Create(Func<RecordedRequest, HttpResponseMessage> respond)
    {
        var handler = new StubHttpHandler(respond);
        var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://steam.example/") };
        return (new SteamAppInfoClient(http), handler);
    }

    [Fact]
    public async Task ReadsThePublicBranchBuildAndItsManifest()
    {
        var (client, handler) = Create(_ => StubHttpHandler.Json(Body()));

        var build = await client.GetPublicBuildAsync(TestContext.Current.CancellationToken);

        Assert.Equal(26430253L, build.BuildId);
        Assert.Equal(Gid, build.ManifestGid);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793801894), build.UpdatedAt);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/v1/info/2278520", request.Uri.AbsolutePath);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("0123")]
    [InlineData("12ab")]
    [InlineData("1234567890123")]
    [InlineData("26430253\n")]
    [InlineData("")]
    [InlineData(null)]
    public async Task ABuildIdThatIsNotAPositiveNumber_IsRejected(string? build)
    {
        var (client, _) = Create(_ => StubHttpHandler.Json(Body(build: build)));

        await Assert.ThrowsAsync<SteamAppInfoException>(() => client.GetPublicBuildAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("12ab")]
    [InlineData("123456789012345678901")]
    [InlineData("")]
    [InlineData(null)]
    public async Task AManifestGidThatIsNotAPositiveNumber_IsRejected(string? gid)
    {
        var (client, _) = Create(_ => StubHttpHandler.Json(Body(gid: gid)));

        await Assert.ThrowsAsync<SteamAppInfoException>(() => client.GetPublicBuildAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("soon")]
    [InlineData("0")]
    [InlineData("253402300800")]
    [InlineData("")]
    [InlineData(null)]
    public async Task AnUpdateTimeThatIsNotADate_IsRejected(string? updated)
    {
        var (client, _) = Create(_ => StubHttpHandler.Json(Body(updated: updated)));

        await Assert.ThrowsAsync<SteamAppInfoException>(() => client.GetPublicBuildAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnUnsuccessfulStatus_IsRejected()
    {
        var (client, _) = Create(_ => StubHttpHandler.Json(Body(status: "failed")));

        await Assert.ThrowsAsync<SteamAppInfoException>(() => client.GetPublicBuildAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"status":"success"}""")]
    [InlineData("""{"status":"success","data":{"2278520":{"depots":{}}}}""")]
    [InlineData("""{"status":"success","data":{"2278520":{"depots":{"branches":{"public":{"buildid":"1","timeupdated":"1"}}}}}}""")]
    [InlineData("not json")]
    [InlineData("[]")]
    public async Task AnUnexpectedBody_IsRejected(string body)
    {
        var (client, _) = Create(_ => StubHttpHandler.Json(body));

        await Assert.ThrowsAsync<SteamAppInfoException>(() => client.GetPublicBuildAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ARequestThatTimesOut_IsReportedAsASteamFailure()
    {
        var (client, _) = Create(_ => throw new TaskCanceledException("timeout"));

        await Assert.ThrowsAsync<SteamAppInfoException>(() => client.GetPublicBuildAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ACancelledCaller_IsNotReportedAsASteamFailure()
    {
        using var cts = new CancellationTokenSource();
        var (client, _) = Create(_ =>
        {
            cts.Cancel();
            throw new TaskCanceledException("cancelled");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetPublicBuildAsync(cts.Token));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task AFailedRequest_IsRejected(HttpStatusCode status)
    {
        var (client, _) = Create(_ => StubHttpHandler.Json(Body(), status));

        await Assert.ThrowsAsync<SteamAppInfoException>(() => client.GetPublicBuildAsync(TestContext.Current.CancellationToken));
    }
}
