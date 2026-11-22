using Tereditor.Host.Abstractions;

namespace Tereditor.Host.Core;

/// <summary>
/// The stage a workspace creation is in, and its budgets. Every stage is one <c>host.workspace-create</c> line pair in the
/// code-intelligence log (with the reporter's <c>still-waiting</c> line every 5 s while it is pending), so a creation that
/// stalls names its stage; the agent tool's deadline error names it too.
/// </summary>
internal sealed class WorkspaceCreateStages
{
    internal const string BudgetVariable = "TEREDITOR_WORKSPACE_CREATE_BUDGET_MS", FetchVariable = "TEREDITOR_WORKSPACE_FETCH_BUDGET_MS";

    private static readonly AsyncLocal<WorkspaceCreateStages?> Active = new();

    /// <summary>The whole agent call, from the first wait to the new worktree; the language servers it starts have their own wait.</summary>
    internal static TimeSpan CreateBudget => Read(BudgetVariable, TimeSpan.FromSeconds(120));

    /// <summary>
    /// The fetch of a remote base. A failed or late fetch only matters when no tracking ref exists yet, so it is short:
    /// a base fetched a moment ago is as good as one fetched now.
    /// </summary>
    internal static TimeSpan FetchBudget => Read(FetchVariable, TimeSpan.FromSeconds(20));

    private static TimeSpan Read(string name, TimeSpan fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var milliseconds) && milliseconds > 0 ? TimeSpan.FromMilliseconds(milliseconds) : fallback;

    private volatile string stage = "start";

    /// <summary>The stage the creation entered last.</summary>
    internal string Stage => stage;

    /// <summary>Marks the creation running in this async flow, so the stages below it report to it.</summary>
    internal static WorkspaceCreateStages Start()
    {
        var stages = new WorkspaceCreateStages();
        Active.Value = stages;
        return stages;
    }

    /// <summary>Enters a stage; dispose it when the stage ends. The log gets the duration; how the call ended is the tool's own result line.</summary>
    internal static IDisposable Step(string name)
    {
        if (Active.Value is { } stages) stages.stage = name;
        return new Scope(CodeIntelligenceLog.Begin("host.workspace-create", ("stage", name)));
    }

    private sealed class Scope(CodeIntelligenceLog.Operation operation) : IDisposable
    {
        private bool done;

        public void Dispose()
        {
            if (done) return;
            done = true;
            operation.Succeed("left");
        }
    }
}