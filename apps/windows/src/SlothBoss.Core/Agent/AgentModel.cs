using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlothBoss.Core.Agent;

public enum RunState
{
    Created, Planning, AwaitingApproval, Ready, Running, Paused, NeedsInput, Retrying, Verifying,
    Completed, PartiallyCompleted, Failed, Cancelled,
}

public enum StepStatus { Pending, AwaitingApproval, Running, Succeeded, Failed, Skipped, Rejected, Uncertain, Superseded }
public enum ApprovalState { NotRequired, Required, Approved, Rejected }

/// <summary>What a tool can do to the user's data. There is deliberately no "destructive" tool.</summary>
public enum ToolRisk { Read, Create, Modify }

/// <summary>
/// Assist: ask before any change. Autopilot: run pre-authorized low-risk actions, ask for the rest.
/// Supervised: the user approves the whole plan once; any replan needs approval again.
/// </summary>
public enum AutonomyMode { Assist, Autopilot, Supervised }

public enum ToolPolicy { Default, Ask, Auto, Deny }

public static class AgentNames
{
    private static string Snake(string pascal) =>
        string.Concat(pascal.Select((c, i) => char.IsUpper(c) && i > 0 ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    private static T Parse<T>(string s) where T : struct, Enum
    {
        foreach (var v in Enum.GetValues<T>())
            if (Snake(v.ToString()) == s) return v;
        throw new SlothBossException($"Unknown {typeof(T).Name} '{s}'.");
    }

    public static string Name(this RunState s) => Snake(s.ToString());
    public static string Name(this StepStatus s) => Snake(s.ToString());
    public static string Name(this ApprovalState s) => Snake(s.ToString());
    public static string Name(this ToolRisk s) => Snake(s.ToString());
    public static string Name(this AutonomyMode s) => Snake(s.ToString());
    public static string Name(this ToolPolicy s) => Snake(s.ToString());

    public static RunState ParseRunState(string s) => Parse<RunState>(s);
    public static StepStatus ParseStepStatus(string s) => Parse<StepStatus>(s);
    public static ApprovalState ParseApproval(string s) => Parse<ApprovalState>(s);
    public static ToolRisk ParseRisk(string s) => Parse<ToolRisk>(s);
    public static AutonomyMode ParseMode(string s) => Parse<AutonomyMode>(s);
    public static ToolPolicy ParsePolicy(string s) => Parse<ToolPolicy>(s);

    public static string Label(this RunState s) => s switch
    {
        RunState.AwaitingApproval => "Needs approval",
        RunState.NeedsInput => "Needs your input",
        RunState.PartiallyCompleted => "Partly done",
        _ => string.Concat(s.ToString().Select((c, i) => char.IsUpper(c) && i > 0 ? " " + char.ToLowerInvariant(c) : c.ToString())),
    };

    public static bool IsTerminal(this RunState s) => s is RunState.Completed or RunState.PartiallyCompleted or RunState.Failed or RunState.Cancelled;
    public static bool IsActive(this RunState s) => s is RunState.Planning or RunState.Running or RunState.Retrying or RunState.Verifying;
}

/// <summary>Allowed run state transitions. Every state change goes through <see cref="Ensure"/>.</summary>
public static class RunStateMachine
{
    private static readonly Dictionary<RunState, RunState[]> Allowed = new()
    {
        [RunState.Created] = [RunState.Planning, RunState.Cancelled],
        [RunState.Planning] = [RunState.AwaitingApproval, RunState.Ready, RunState.NeedsInput, RunState.Failed, RunState.Cancelled, RunState.Paused],
        [RunState.AwaitingApproval] = [RunState.Ready, RunState.Running, RunState.Planning, RunState.Verifying, RunState.Cancelled, RunState.Failed],
        [RunState.Ready] = [RunState.Running, RunState.AwaitingApproval, RunState.NeedsInput, RunState.Paused, RunState.Cancelled, RunState.Verifying, RunState.Planning],
        [RunState.Running] = [RunState.AwaitingApproval, RunState.Paused, RunState.NeedsInput, RunState.Retrying, RunState.Verifying,
                              RunState.Planning, RunState.Failed, RunState.Cancelled, RunState.PartiallyCompleted],
        [RunState.Retrying] = [RunState.Running, RunState.Failed, RunState.Cancelled, RunState.Paused, RunState.Verifying],
        [RunState.Paused] = [RunState.Running, RunState.Ready, RunState.Planning, RunState.Cancelled, RunState.NeedsInput],
        [RunState.NeedsInput] = [RunState.Planning, RunState.Ready, RunState.Running, RunState.Cancelled],
        [RunState.Verifying] = [RunState.Completed, RunState.PartiallyCompleted, RunState.Failed, RunState.Paused],
        // Terminal states: only "retry failed steps" re-opens a finished run.
        [RunState.Completed] = [],
        [RunState.PartiallyCompleted] = [RunState.Ready],
        [RunState.Failed] = [RunState.Ready, RunState.Planning],
        [RunState.Cancelled] = [],
    };

    public static bool CanMove(RunState from, RunState to) => from == to || Allowed[from].Contains(to);

    public static void Ensure(RunState from, RunState to)
    {
        if (!CanMove(from, to)) throw new InvalidRunTransitionException(from, to);
    }
}

public sealed class InvalidRunTransitionException(RunState from, RunState to)
    : SlothBossException($"A run can't go from {from.Label()} to {to.Label()}.");

/// <summary>Hard limits for one run. Exceeding any of them stops the run truthfully.</summary>
public sealed record AgentBudget(
    int MaxPlanSteps = 12, int MaxToolCalls = 40, int MaxModelCalls = 40, int MaxRetriesPerStep = 2,
    int MaxReplans = 2, int MaxFileOps = 400, int MaxMinutes = 20, int MaxFiles = 500, long MaxBytesHashed = 2L * 1024 * 1024 * 1024)
{
    public string ToJson() => JsonSerializer.Serialize(this);
    public static AgentBudget FromJson(string json) => JsonSerializer.Deserialize<AgentBudget>(json) ?? new AgentBudget();
}

public sealed class AgentUsage
{
    public int ToolCalls { get; set; }
    public int ModelCalls { get; set; }
    public int FileOps { get; set; }
    public int Replans { get; set; }
    public double ActiveSeconds { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this);
    public static AgentUsage FromJson(string json) => JsonSerializer.Deserialize<AgentUsage>(json) ?? new AgentUsage();
}

public sealed class BudgetExceededException(string what) : SlothBossException($"The run's budget for {what} is used up.")
{
    public string What { get; } = what;
}

public sealed record RunRecord(
    string Id, string Goal, AutonomyMode Mode, string Planner, string? FolderId, string? FolderPath, RunState State,
    string? StateReason, string? PlanJson, int PlanVersion, int PlanApprovedVersion, AgentBudget Budget, AgentUsage Usage,
    JsonObject Artifacts, string? ReportJson, string? Control, string? UndoneAt, string CreatedAt, string UpdatedAt,
    string? StartedAt, string? FinishedAt)
{
    public AgentPlan? Plan => PlanJson is null ? null : AgentPlan.FromJson(PlanJson);
    public RunReport? Report => ReportJson is null ? null : RunReport.FromJson(ReportJson);
}

public sealed record StepRecord(
    string Id, string RunId, int Seq, int PlanVersion, string Tool, int ToolVersion, JsonObject Args, string Purpose,
    ToolRisk Risk, ApprovalState Approval, StepStatus Status, int Attempts, string IdempotencyKey, IReadOnlyList<int> Excluded,
    string? ResultJson, string? Error, string? VerificationJson, string CreatedAt, string? StartedAt, string? FinishedAt, string? DecidedAt);

public sealed record FileOpRecord(
    string Id, string RunId, string? StepId, int Seq, string Op, string? SourceRel, string TargetRel, string? Sha, long? Size,
    string Reason, string Status, string? Note, string? Error, string? AppliedAt, string? UndoneAt);

public sealed record AgentEvent(long Id, string RunId, string? StepId, string At, string Level, string Kind, string Message, string? DataJson);

public sealed record ApprovedFolder(string Id, string Path, string Label, string CreatedAt);

/// <summary>A validated plan. Steps carry only tool ids and arguments; permissions are decided by the runtime.</summary>
public sealed record AgentPlan(string Understanding, IReadOnlyList<string> MissingInformation, IReadOnlyList<PlanStep> Steps,
    IReadOnlyList<string> CompletionCriteria, IReadOnlyList<string> RuntimeNotes)
{
    public string ToJson() => JsonSerializer.Serialize(new
    {
        understanding = Understanding,
        missing_information = MissingInformation,
        steps = Steps.Select(s => new { tool = s.Tool, purpose = s.Purpose, args = s.Args }),
        completion_criteria = CompletionCriteria,
        runtime_notes = RuntimeNotes,
    });

    public static AgentPlan FromJson(string json)
    {
        var o = JsonNode.Parse(json)!.AsObject();
        return new AgentPlan(
            o["understanding"]?.GetValue<string>() ?? "",
            o["missing_information"]?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? [],
            o["steps"]!.AsArray().Select(s => new PlanStep(s!["tool"]!.GetValue<string>(), s["purpose"]?.GetValue<string>() ?? "",
                s["args"] as JsonObject is { } a ? (JsonObject)a.DeepClone() : [])).ToList(),
            o["completion_criteria"]?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? [],
            o["runtime_notes"]?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? []);
    }
}

public sealed record PlanStep(string Tool, string Purpose, JsonObject Args);

public sealed record VerificationCheck(string Name, bool Passed, string Detail);

/// <summary>The truthful end-of-run report, built from persisted state and re-checked against disk and database.</summary>
public sealed record RunReport(string Outcome, string Headline, IReadOnlyList<VerificationCheck> Checks, IReadOnlyList<string> Done,
    IReadOnlyList<string> NotDone, int FilesMoved, int FoldersCreated, int DuplicateGroups, int Summaries, int TasksCreated,
    string? ReportPageId, bool CanUndo)
{
    public string ToJson() => JsonSerializer.Serialize(this);
    public static RunReport? FromJson(string json) => JsonSerializer.Deserialize<RunReport>(json);
}
