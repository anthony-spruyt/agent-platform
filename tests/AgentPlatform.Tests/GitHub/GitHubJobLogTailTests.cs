using System.Net;
using System.Text;
using AgentPlatform.Host.GitHub;
using AgentPlatform.Tests.Support;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Tests.GitHub;

public class GitHubJobLogTailTests
{
    private const string Failure = "2026-10-13T19:54:42.5172974Z ##[error]Process completed with exit code 1.\n";

    // A bats job trimmed to its shape: the failing step, then the post-job output of checkout and the runner hardening step.
    private const string BatsJobLog =
        "2026-10-13T19:54:23.2634451Z ##[group]Run mapfile -t suites < <(git ls-files '*.bats')\n" +
        "2026-10-13T19:54:23.2635517Z mapfile -t suites < <(git ls-files '*.bats')\n" +
        "2026-10-13T19:54:23.2640302Z bats \"${suites[@]}\"\n" +
        "2026-10-13T19:54:23.2641288Z shell: /usr/bin/bash -e {0}\n" +
        "2026-10-13T19:54:23.2642001Z ##[endgroup]\n" +
        "2026-10-13T19:54:31.4410233Z 1..3\n" +
        "2026-10-13T19:54:31.4411208Z ok 1 bootstrap installs the plugin list\n" +
        "2026-10-13T19:54:41.9876015Z not ok 2 bootstrap fails when the marketplace is unreachable\n" +
        "2026-10-13T19:54:41.9877432Z # (in test file scripts/bootstrap-plugins.bats, line 71)\n" +
        "2026-10-13T19:54:41.9878120Z #   `[ \"$status\" -eq 0 ]' failed\n" +
        "2026-10-13T19:54:42.5087592Z BW02: Using flags on `run` requires at least BATS_VERSION=1.5.0.\n" +
        Failure +
        "2026-10-13T19:54:42.5307908Z Post job cleanup.\n" +
        "2026-10-13T19:54:42.6175792Z [command]/usr/bin/git version\n" +
        "2026-10-13T19:54:42.6221718Z git version 2.55.0\n" +
        "2026-10-13T19:54:42.6310883Z [command]/usr/bin/git config --local --name-only --get-regexp core\\.sshCommand\n" +
        "2026-10-13T19:54:42.7711970Z Removing credentials config '/home/runner/work/_temp/git-credentials-1.config'\n" +
        "2026-10-13T19:54:42.7865328Z Post job cleanup.\n" +
        "2026-10-13T19:54:42.8593263Z [harden-runner] post-step\n" +
        "2026-10-13T19:54:43.8612533Z log:\n" +
        "2026-10-13T19:54:43.8939368Z Oct 13 19:54:22 runner agentservice[2311]: INFO Map size module=armour protected_bpf_ids=9\n" +
        "2026-10-13T19:54:43.8940067Z Oct 13 19:54:22 runner agentservice[2311]: INFO Map size module=armour protected_fs_inodes=6\n" +
        "2026-10-13T19:54:43.8950970Z Oct 13 19:54:22 runner agentservice[2311]: INFO Protection maps populated module=armour\n" +
        "2026-10-13T19:54:43.8952330Z Oct 13 19:54:22 runner agentservice[2311]: INFO Armour engaged module=armour\n" +
        "2026-10-13T19:54:43.9007426Z Cleaning up orphan processes\n";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<string?> TailAsync(string log, int maxChars)
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(log, Encoding.UTF8, "text/plain") });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var client = new GitHubApiClient(http, new StaticTokens(), Options.Create(new GitHubAppOptions { AppId = "3233443" }));
        return client.GetJobLogTailAsync("octo/widgets", 55, maxChars, Ct);
    }

    private static string Noise(int chars, string label) => string.Concat(Enumerable.Range(0, chars / 40 + 1).Select(i => $"{label} noise line {i:D6} padding padding\n"));

    private sealed class StaticTokens : IGitHubInstallationTokenProvider
    {
        public Task<string> GetTokenAsync(string repo, IReadOnlyDictionary<string, string> permissions, CancellationToken cancellationToken) => Task.FromResult("ghs_install_token");
    }

    [Fact]
    public async Task TheTail_EndsAtTheFailingStep_NotAtThePostJobOutput()
    {
        var tail = await TailAsync(BatsJobLog, 700);

        Assert.EndsWith(Failure, tail, StringComparison.Ordinal);
        Assert.Contains("not ok 2 bootstrap fails when the marketplace is unreachable", tail, StringComparison.Ordinal);
        Assert.DoesNotContain("Post job cleanup", tail, StringComparison.Ordinal);
        Assert.DoesNotContain("Cleaning up orphan processes", tail, StringComparison.Ordinal);
        Assert.True(tail!.Length <= 700);
    }

    [Fact]
    public async Task AFailureLineWithoutATrailingNewline_IsStillWhereTheTailEnds()
    {
        var tail = await TailAsync("start\nsome output\n##[error]boom" + "\nPost job cleanup.\n" + Noise(500, "post") + "##[error]last", 4000);

        Assert.EndsWith("##[error]last", tail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithSeveralFailureLines_TheTailEndsAtTheLastOne()
    {
        var log = "build\n##[error]first\nmore output\n##[error]second\nPost job cleanup.\nCleaning up orphan processes\n";

        Assert.Equal(log[..log.IndexOf("Post job", StringComparison.Ordinal)], await TailAsync(log, 4000));
    }

    [Fact]
    public async Task NoiseBetweenFailureLinesLongerThanTheCap_StillKeepsTheCharactersBeforeTheLastFailure()
    {
        var log = Noise(3000, "early") + "##[error]first\n" + Noise(3000, "middle") + "##[error]last\n" + Noise(3000, "post");

        var tail = await TailAsync(log, 500);

        Assert.Equal(500, tail!.Length);
        Assert.EndsWith("##[error]last\n", tail, StringComparison.Ordinal);
        Assert.Contains("middle noise line", tail, StringComparison.Ordinal);
        Assert.DoesNotContain("post noise", tail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALogWithoutAFailureLine_KeepsTheLastMaxCharacters()
    {
        var log = Noise(3000, "plain") + "Post job cleanup.\n" + Noise(300, "post");

        Assert.Equal(log[^200..], await TailAsync(log, 200));
    }

    [Fact]
    public async Task AFailureLineSplitAcrossReads_IsStillFound()
    {
        var log = new string('a', 8179) + "\n2026-10-13T19:54:42.5172974Z ##[error]split\n" + Noise(1000, "post");

        var tail = await TailAsync(log, 100);

        Assert.EndsWith("Z ##[error]split\n", tail, StringComparison.Ordinal);
    }
}
