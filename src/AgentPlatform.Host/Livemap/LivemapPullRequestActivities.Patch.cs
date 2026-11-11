using System.Globalization;

namespace AgentPlatform.Host.Livemap;

public sealed partial class LivemapPullRequestActivities
{
    // A data patch goes the way of any data pull request, checked again against main as it is now; a code patch is branched off the commit the agent read and left to the owner.
    private async Task OpenPatchPullRequestAsync(LivemapBuild row, long runId, CancellationToken cancellationToken)
    {
        if (LivemapStoredPatch.Read(row.PatchResult) is not { BuildsJson: { } buildsJson } patch || row.PatchBaseSha is not { } baseSha)
        {
            await StartOverAsync(row.BuildId, "The build was classified without a patch the worker can read.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var branch = LivemapBuildBranch.For(row.BuildId);
        await RemoveAsync(branch, cancellationToken).ConfigureAwait(false);
        if (patch.Status == LivemapResultStatus.Code)
        {
            await OpenCodePullRequestAsync(row.BuildId, runId, patch, buildsJson, baseSha, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await OpenDataPatchPullRequestAsync(row, runId, patch, buildsJson, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task OpenDataPatchPullRequestAsync(LivemapBuild row, long runId, LivemapResult patch, string buildsJson, CancellationToken cancellationToken)
    {
        CompatArtifact? artifact;
        try
        {
            artifact = await github.ReadCompatArtifactAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        catch (CompatArtifactException ex)
        {
            await builds.FlagPullRequestAsync(row.BuildId, ex.Message, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (artifact is null)
        {
            await StartOverAsync(row.BuildId, "The compat run's artifact is no longer there.", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!CompatReport.TryParse(artifact.ReportJson, row.BuildId, row.ManifestGid, out var report, out _))
        {
            await StartOverAsync(row.BuildId, "The compat run's report no longer matches the build.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var mainHead = await pullRequests.GetMainHeadAsync(cancellationToken).ConfigureAwait(false);
        var current = await github.ReadBuildsJsonAsync(mainHead, cancellationToken).ConfigureAwait(false);
        var schemaJson = await github.ReadBuildsSchemaAsync(mainHead, cancellationToken).ConfigureAwait(false);
        if (current is null || schemaJson is null)
        {
            await builds.FlagPullRequestAsync(row.BuildId, "The fork has no builds file or no builds schema on main.", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (new LivemapPatchValidator(schema).Check(patch, new LivemapPatchBase(current, schemaJson, report!), row.BuildId, row.ManifestGid) is not null)
        {
            await StartOverAsync(row.BuildId, "Main changed since the patch was made, so the patch no longer fits it.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var body = $"Adds the build to `{LivemapMergeGate.BuildsPath}`. The hard-patch agent wrote the row, and the worker checked the file against the schema.\n\n{Links(runId)}";
        await OpenDataPullRequestAsync(row.BuildId, LivemapClassification.Hard, mainHead, buildsJson, body, cancellationToken).ConfigureAwait(false);
    }

    private async Task OpenCodePullRequestAsync(long buildId, long runId, LivemapResult patch, string buildsJson, string baseSha, CancellationToken cancellationToken)
    {
        if (LivemapResult.CheckFiles(patch.Files) is not null)
        {
            await builds.FlagPullRequestAsync(buildId, "The stored patch holds files the worker will not commit.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var branch = LivemapBuildBranch.For(buildId);
        var headline = Headline(LivemapClassification.Hard, buildId);
        await pullRequests.CreateBranchAsync(branch, baseSha, cancellationToken).ConfigureAwait(false);
        var head = await pullRequests.CommitFilesAsync(branch, baseSha, headline, [new LivemapFile(LivemapMergeGate.BuildsPath, buildsJson), .. patch.Files], cancellationToken).ConfigureAwait(false);
        var number = await pullRequests.OpenAsync(branch, headline, CodeBody(runId, patch, baseSha), cancellationToken).ConfigureAwait(false);
        if (!await builds.OpenOwnerReviewAsync(buildId, number, head, cancellationToken).ConfigureAwait(false))
        {
            await DiscardAsync(number, branch, cancellationToken).ConfigureAwait(false);
        }
    }

    private string CodeBody(long runId, LivemapResult patch, string baseSha) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"The hard-patch agent changed {patch.Files.Count + 1} files for this build, `{LivemapMergeGate.BuildsPath}` among them. This pull request waits for the owner: the worker does not merge it.\n\nBased on main at {baseSha}.\n\n{Links(runId)}");
}
