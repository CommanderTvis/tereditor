using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.Host;

namespace Tereditor.Plugins.BranchGraph.Host;

/// <summary>The host half: the project's history through the host's Git runner, a page at a time, and a commit's patch.</summary>
public sealed class BranchGraphHost : PluginHostModule
{
    public override PluginContract Contract => BranchGraphContract.Contract;

    public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        HostProject Project(string projectId) =>
            context.Projects().FirstOrDefault(project => project.Id == projectId) ?? throw new InvalidOperationException($"Unknown project: {projectId}");

        context.Method(BranchGraphContract.Graph, async (parameters, _, cancellationToken) =>
        {
            var project = Project(parameters.ProjectId);
            var log = await context.GitAsync(project.Path, GraphBuild.LogArgs(parameters.Skip), cancellationToken: cancellationToken);
            if (log.Failure is not null) throw new InvalidOperationException("Could not read the history: " + (log.Err.Length > 0 ? log.Err : "git failed"));
            if (!log.Ok || log.Out.Length == 0) return new GitGraph([], [], false);
            var (commits, hasMore) = GraphBuild.ParseLog(log.Out);
            var listed = await context.GitAsync(project.Path, ["worktree", "list", "--porcelain"], cancellationToken: cancellationToken);
            var worktrees = listed.Ok ? GraphBuild.ParseWorktrees(listed.Out, await context.WorkspacesAsync(project.Id, cancellationToken)) : [];
            return new GitGraph(commits, worktrees, hasMore);
        });
        context.Method(BranchGraphContract.Patch, async (parameters, _, cancellationToken) =>
        {
            var project = Project(parameters.ProjectId);
            var patch = await context.GitAsync(project.Path, ["format-patch", "-1", "-m", parameters.Sha, "--stdout"], cancellationToken: cancellationToken);
            if (patch.Failure is not null || !patch.Ok) throw new InvalidOperationException("Could not generate patch: " + (patch.Err.Length > 0 ? patch.Err : "git failed"));
            return new GitPatch(patch.Out);
        });
        // The checks come from whichever repo host owns origin (GitHub, when it is on); none is a normal answer, never an error.
        context.Method(BranchGraphContract.Checks, async (parameters, _, cancellationToken) =>
        {
            var project = Project(parameters.ProjectId);
            var found = await context.CommitChecksAsync(project.Path, [.. parameters.Shas.Take(50)], cancellationToken);
            return new GitGraphChecks([.. found.Select(item => new GitGraphCheck(item.Sha, item.State, item.Passed, item.Total, item.Attention, item.Url))]);
        });
        return ValueTask.FromResult<PluginDisposer?>(null);
    }
}