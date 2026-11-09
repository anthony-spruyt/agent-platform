using System.Collections.Concurrent;
using AgentPlatform.Host.Livemap;

namespace AgentPlatform.Tests.Livemap;

// Models the fork well enough that a pull request the worker opens passes the gate until a test changes one fact.
internal sealed class FakeLivemapPullRequests : ILivemapPinPullRequests
{
    public const string Fork = "octo/livemap";
    public const string AppLogin = "skynet-rw[bot]";
    public const string PlainFile = "100644";

    public static readonly string StartMain = new('b', 40);

    private int _nextNumber = 6;

    private int _nextCommit;

    public string Repo { get; init; } = Fork;

    public string OpenedFile { get; init; } = LivemapMergeGate.BuildsPath;

    public string MainHead { get; set; } = StartMain;

    public Dictionary<string, string> Branches { get; } = [];

    public Dictionary<int, LivemapPullRequest> Pulls { get; } = [];

    public Dictionary<int, List<LivemapPullRequestFile>> Files { get; } = [];

    public Dictionary<string, string[]> Parents { get; } = [];

    public Dictionary<string, string?> Modes { get; } = [];

    public Dictionary<(string Sha, string Path), string> Contents { get; } = [];

    public Dictionary<string, List<LivemapCheckRun>> Checks { get; } = [];

    public List<(string Branch, string ExpectedHeadOid, string Headline, string Path, string Content, string Oid)> Commits { get; } = [];

    public List<(int Number, string Branch, string Title, string Body)> Opened { get; } = [];

    public List<(int Number, string Sha, string Title)> Merges { get; } = [];

    public List<int> Closed { get; } = [];

    public List<string> Deleted { get; } = [];

    public ConcurrentQueue<string> CheckRunReads { get; } = [];

    public List<LivemapCheckRun>? DefaultChecks { get; set; }

    public LivemapMergeResult MergeResult { get; set; } = LivemapMergeResult.Merged;

    public int NotMergeableAnswers { get; set; }

    public Func<string, Task>? Hook { get; set; }

    public List<string> Calls { get; } = [];

    public void Seed(LivemapPullRequest pull, params LivemapPullRequestFile[] files)
    {
        Pulls[pull.Number] = pull;
        Files[pull.Number] = [.. files];
    }

    public LivemapPullRequest Leftover(string branch)
    {
        var pull = new LivemapPullRequest(++_nextNumber, "open", false, false, "main", Repo, branch, Repo, new string('9', 40), AppLogin, "Bot", 1);
        Seed(pull);
        Branches[branch] = pull.HeadSha;
        return pull;
    }

    public async Task<string> GetMainHeadAsync(CancellationToken cancellationToken)
    {
        await EnterAsync("main-head");
        return MainHead;
    }

    public async Task CreateBranchAsync(string branch, string oid, CancellationToken cancellationToken)
    {
        await EnterAsync($"create-branch:{branch}");
        if (!Branches.TryAdd(branch, oid))
        {
            throw new InvalidOperationException($"The branch {branch} exists already.");
        }
    }

    public async Task<string> CommitFileAsync(string branch, string expectedHeadOid, string headline, string path, string content, CancellationToken cancellationToken)
    {
        await EnterAsync($"commit:{branch}");
        if (Branches.GetValueOrDefault(branch) != expectedHeadOid)
        {
            throw new InvalidOperationException($"The branch {branch} is not at the expected head.");
        }

        var oid = $"{++_nextCommit:x40}";
        Commits.Add((branch, expectedHeadOid, headline, path, content, oid));
        Branches[branch] = oid;
        Parents[oid] = [expectedHeadOid];
        return oid;
    }

    public async Task<int> OpenAsync(string branch, string title, string body, CancellationToken cancellationToken)
    {
        await EnterAsync($"open:{branch}");
        var number = ++_nextNumber;
        Opened.Add((number, branch, title, body));
        Seed(
            new LivemapPullRequest(number, "open", false, false, "main", Repo, branch, Repo, Branches[branch], AppLogin, "Bot", 1, title),
            new LivemapPullRequestFile(OpenedFile, "modified", null));
        return number;
    }

    public async Task<LivemapPullRequest?> GetAsync(int number, CancellationToken cancellationToken)
    {
        await EnterAsync($"get:{number}");
        return Pulls.GetValueOrDefault(number);
    }

    public async Task<IReadOnlyList<LivemapPullRequest>> FindOpenByBranchAsync(string branch, CancellationToken cancellationToken)
    {
        await EnterAsync($"find:{branch}");
        return [.. Pulls.Values.Where(p => p.State == "open" && p.HeadRef == branch)];
    }

    public async Task<IReadOnlyList<LivemapPullRequestFile>> ListFilesAsync(int number, CancellationToken cancellationToken)
    {
        await EnterAsync($"files:{number}");
        return Files.GetValueOrDefault(number) ?? [];
    }

    public async Task<IReadOnlyList<string>> GetParentsAsync(string commitSha, CancellationToken cancellationToken)
    {
        await EnterAsync($"parents:{commitSha}");
        return Parents.GetValueOrDefault(commitSha) ?? [];
    }

    public async Task<string?> ReadFileAsync(string commitSha, string path, CancellationToken cancellationToken)
    {
        await EnterAsync($"read:{path}@{commitSha}");
        return Contents.GetValueOrDefault((commitSha, path));
    }

    public async Task<string?> GetFileModeAsync(string commitSha, string path, CancellationToken cancellationToken)
    {
        await EnterAsync($"mode:{commitSha}");
        return Modes.TryGetValue(commitSha, out var mode) ? mode : PlainFile;
    }

    public async Task<IReadOnlyList<LivemapCheckRun>> ListCheckRunsAsync(string sha, CancellationToken cancellationToken)
    {
        await EnterAsync($"checks:{sha}");
        CheckRunReads.Enqueue(sha);
        return Checks.GetValueOrDefault(sha) ?? DefaultChecks ?? [];
    }

    public async Task<LivemapMergeResult> MergeAsync(int number, string sha, string title, CancellationToken cancellationToken)
    {
        await EnterAsync($"merge:{number}");
        Merges.Add((number, sha, title));
        if (NotMergeableAnswers > 0)
        {
            NotMergeableAnswers--;
            return LivemapMergeResult.NotMergeable;
        }

        if (MergeResult == LivemapMergeResult.Merged && Pulls.TryGetValue(number, out var pull))
        {
            Pulls[number] = pull with { State = "closed", Merged = true };
        }

        return MergeResult;
    }

    public async Task ClosePullRequestAsync(int number, CancellationToken cancellationToken)
    {
        await EnterAsync($"close:{number}");
        Closed.Add(number);
        if (Pulls.TryGetValue(number, out var pull))
        {
            Pulls[number] = pull with { State = "closed" };
        }
    }

    public async Task DeleteBranchAsync(string branch, CancellationToken cancellationToken)
    {
        await EnterAsync($"delete:{branch}");
        Deleted.Add(branch);
        Branches.Remove(branch);
    }

    public async Task<string> GetAppLoginAsync(CancellationToken cancellationToken)
    {
        await EnterAsync("app-login");
        return AppLogin;
    }

    private async Task EnterAsync(string call)
    {
        Calls.Add(call);
        if (Hook is { } hook)
        {
            await hook(call);
        }
    }
}

internal sealed class FakeBuildsSchema : ILivemapBuildsSchema
{
    public string? Problem { get; set; }

    public List<(string Schema, string Builds)> Checked { get; } = [];

    public string? Check(string schemaJson, string buildsJson)
    {
        Checked.Add((schemaJson, buildsJson));
        return Problem;
    }
}
