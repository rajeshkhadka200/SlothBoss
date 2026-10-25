using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SlothBoss.Core.Storage;

namespace SlothBoss.Core.Agent;

/// <summary>Persistence for agent runs, steps, file operations, events, approved folders and tool policies.</summary>
public sealed class AgentStore(Workspace ws)
{
    private const string RunColumns =
        "id, goal, mode, planner, folder_id, folder_path, state, state_reason, plan_json, plan_version, plan_approved_version, " +
        "budget_json, usage_json, artifacts_json, report_json, control, undone_at, created_at, updated_at, started_at, finished_at";
    private const string StepColumns =
        "id, run_id, seq, plan_version, tool, tool_version, args_json, purpose, risk, approval, status, attempts, idempotency_key, " +
        "excluded_json, result_json, error, verification_json, created_at, started_at, finished_at, decided_at";
    private const string OpColumns =
        "id, run_id, step_id, seq, op, source_rel, target_rel, sha256, size, reason, status, note, error, applied_at, undone_at";

    private static RunRecord MapRun(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), AgentNames.ParseMode(r.GetString(2)), r.GetString(3), r.NullableString(4), r.NullableString(5),
        AgentNames.ParseRunState(r.GetString(6)), r.NullableString(7), r.NullableString(8), r.GetInt32(9), r.GetInt32(10),
        AgentBudget.FromJson(r.GetString(11)), AgentUsage.FromJson(r.GetString(12)), JsonNode.Parse(r.GetString(13))!.AsObject(),
        r.NullableString(14), r.NullableString(15), r.NullableString(16), r.GetString(17), r.GetString(18), r.NullableString(19), r.NullableString(20));

    private static StepRecord MapStep(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetString(4), r.GetInt32(5), JsonNode.Parse(r.GetString(6))!.AsObject(),
        r.GetString(7), AgentNames.ParseRisk(r.GetString(8)), AgentNames.ParseApproval(r.GetString(9)), AgentNames.ParseStepStatus(r.GetString(10)),
        r.GetInt32(11), r.GetString(12), r.NullableString(13) is { } ex ? JsonSerializer.Deserialize<List<int>>(ex) ?? [] : [],
        r.NullableString(14), r.NullableString(15), r.NullableString(16), r.GetString(17), r.NullableString(18), r.NullableString(19), r.NullableString(20));

    private static FileOpRecord MapOp(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.NullableString(2), r.GetInt32(3), r.GetString(4), r.NullableString(5), r.GetString(6),
        r.NullableString(7), r.IsDBNull(8) ? null : r.GetInt64(8), r.GetString(9), r.GetString(10), r.NullableString(11),
        r.NullableString(12), r.NullableString(13), r.NullableString(14));

    // ---- approved folders -------------------------------------------------------------------

    public ApprovedFolder ApproveFolder(string path, string? label = null)
    {
        var full = FileSandbox.NormalizeRoot(path);
        var existing = Folders().FirstOrDefault(f => string.Equals(f.Path, full, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
        var folder = new ApprovedFolder(Ids.New(), full, string.IsNullOrWhiteSpace(label) ? Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar)) : label, Clock.Stamp());
        ws.Write(WorkspaceChange.Agent, (c, tx) => c.Exec(
            "INSERT INTO approved_folders (id, path, label, created_at) VALUES ($id, $p, $l, $t)", tx,
            ("$id", folder.Id), ("$p", folder.Path), ("$l", folder.Label), ("$t", folder.CreatedAt)));
        return folder;
    }

    public List<ApprovedFolder> Folders() =>
        ws.Read(c => c.Query("SELECT id, path, label, created_at FROM approved_folders ORDER BY label",
            r => new ApprovedFolder(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3))));

    public ApprovedFolder? Folder(string id) => Folders().FirstOrDefault(f => f.Id == id);

    public void RevokeFolder(string id) =>
        ws.Write(WorkspaceChange.Agent, (c, tx) => c.Exec("DELETE FROM approved_folders WHERE id = $id", tx, ("$id", id)));

    // ---- tool policies (user-approved preferences) -------------------------------------------

    public ToolPolicy Policy(string toolId) =>
        ws.Settings.Get("agent.policy." + toolId) is { } v ? AgentNames.ParsePolicy(v) : ToolPolicy.Default;

    public void SetPolicy(string toolId, ToolPolicy policy) =>
        ws.Settings.Set("agent.policy." + toolId, policy == ToolPolicy.Default ? null : policy.Name());

    // ---- runs ---------------------------------------------------------------------------------

    public RunRecord CreateRun(string goal, AutonomyMode mode, string planner, ApprovedFolder? folder, AgentBudget budget)
    {
        goal = goal.Trim();
        if (goal.Length == 0) throw new SlothBossException("Describe what you want done first.");
        if (goal.Length > 2000) throw new SlothBossException("Please keep the goal under 2000 characters.");
        var id = Ids.New();
        var now = Clock.Stamp();
        ws.Write(WorkspaceChange.Agent, (c, tx) => c.Exec($"""
            INSERT INTO agent_runs (id, goal, mode, planner, folder_id, folder_path, state, budget_json, usage_json, created_at, updated_at)
            VALUES ($id, $goal, $mode, $planner, $fid, $fpath, 'created', $budget, $usage, $now, $now)
            """, tx, ("$id", id), ("$goal", goal), ("$mode", mode.Name()), ("$planner", planner), ("$fid", folder?.Id),
            ("$fpath", folder?.Path), ("$budget", budget.ToJson()), ("$usage", new AgentUsage().ToJson()), ("$now", now)));
        Log(id, null, "info", "created", $"Run created ({mode.Name()} mode, planner: {planner}).");
        return Run(id)!;
    }

    public RunRecord? Run(string id) =>
        ws.Read(c => c.Query($"SELECT {RunColumns} FROM agent_runs WHERE id = $id", MapRun, null, ("$id", id)).FirstOrDefault());

    public List<RunRecord> Runs(int limit = 100) =>
        ws.Read(c => c.Query($"SELECT {RunColumns} FROM agent_runs ORDER BY created_at DESC, rowid DESC LIMIT $n", MapRun, null, ("$n", limit)));

    /// <summary>Validated state transition; throws on an illegal move.</summary>
    public RunRecord SetState(string runId, RunState to, string? reason = null)
    {
        ws.Write(WorkspaceChange.Agent, (c, tx) =>
        {
            var current = AgentNames.ParseRunState((string)c.Scalar("SELECT state FROM agent_runs WHERE id = $id", tx, ("$id", runId))!);
            RunStateMachine.Ensure(current, to);
            var now = Clock.Stamp();
            c.Exec("""
                UPDATE agent_runs SET state = $s, state_reason = $r, updated_at = $now,
                    started_at = CASE WHEN $s = 'running' AND started_at IS NULL THEN $now ELSE started_at END,
                    finished_at = CASE WHEN $s IN ('completed','partially_completed','failed','cancelled') THEN $now ELSE NULL END
                WHERE id = $id
                """, tx, ("$s", to.Name()), ("$r", reason), ("$now", now), ("$id", runId));
            if (current != to) InsertEvent(c, tx, runId, null, to is RunState.Failed ? "error" : "info", "state",
                $"{current.Label()} → {to.Label()}" + (reason is null ? "" : $": {reason}"), null);
        });
        return Run(runId)!;
    }

    public void SetControl(string runId, string? control) =>
        ws.Write(WorkspaceChange.Agent, (c, tx) => c.Exec("UPDATE agent_runs SET control = $c, updated_at = $now WHERE id = $id", tx,
            ("$c", control), ("$now", Clock.Stamp()), ("$id", runId)));

    public void SaveUsage(string runId, AgentUsage usage) =>
        ws.Write(WorkspaceChange.Agent, (c, tx) => c.Exec("UPDATE agent_runs SET usage_json = $u, updated_at = $now WHERE id = $id", tx,
            ("$u", usage.ToJson()), ("$now", Clock.Stamp()), ("$id", runId)));

    public void SaveArtifacts(string runId, JsonObject artifacts) =>
        ws.Write(WorkspaceChange.Agent, (c, tx) => c.Exec("UPDATE agent_runs SET artifacts_json = $a, updated_at = $now WHERE id = $id", tx,
            ("$a", artifacts.ToJsonString()), ("$now", Clock.Stamp()), ("$id", runId)));

    public void SaveReport(string runId, RunReport report) =>
        ws.Write(WorkspaceChange.Agent, (c, tx) => c.Exec("UPDATE agent_runs SET report_json = $r, updated_at = $now WHERE id = $id", tx,
            ("$r", report.ToJson()), ("$now", Clock.Stamp()), ("$id", runId)));

    public void MarkUndone(string runId) =>
        ws.Write(WorkspaceChange.Agent, (c, tx) => c.Exec("UPDATE agent_runs SET undone_at = $now, updated_at = $now WHERE id = $id", tx,
            ("$now", Clock.Stamp()), ("$id", runId)));

    public void SetFolder(string runId, ApprovedFolder folder) =>
        ws.Write(WorkspaceChange.Agent, (c, tx) => c.Exec("UPDATE agent_runs SET folder_id = $f, folder_path = $p, updated_at = $now WHERE id = $id", tx,
            ("$f", folder.Id), ("$p", folder.Path), ("$now", Clock.Stamp()), ("$id", runId)));

    /// <summary>
    /// Stores a new plan version. Pending steps of older versions are superseded; finished steps are kept.
    /// </summary>
    public void SavePlan(string runId, AgentPlan plan, IReadOnlyList<(PlanStep Step, int Version, ToolRisk Risk)> steps)
    {
        ws.Write(WorkspaceChange.Agent, (c, tx) =>
        {
            var version = Convert.ToInt32(c.Scalar("SELECT plan_version FROM agent_runs WHERE id = $id", tx, ("$id", runId))) + 1;
            c.Exec("UPDATE agent_steps SET status = 'superseded' WHERE run_id = $id AND status IN ('pending', 'awaiting_approval')", tx, ("$id", runId));
            var seq = Convert.ToInt32(c.Scalar("SELECT coalesce(max(seq), 0) FROM agent_steps WHERE run_id = $id", tx, ("$id", runId)));
            var now = Clock.Stamp();
            foreach (var (step, toolVersion, risk) in steps)
            {
                seq++;
                c.Exec($"""
                    INSERT INTO agent_steps (id, run_id, seq, plan_version, tool, tool_version, args_json, purpose, risk, approval, status,
                                             idempotency_key, created_at)
                    VALUES ($id, $run, $seq, $v, $tool, $tv, $args, $purpose, $risk, 'not_required', 'pending', $key, $now)
                    """, tx, ("$id", Ids.New()), ("$run", runId), ("$seq", seq), ("$v", version), ("$tool", step.Tool), ("$tv", toolVersion),
                    ("$args", step.Args.ToJsonString()), ("$purpose", step.Purpose), ("$risk", risk.Name()),
                    ("$key", $"{runId}:{version}:{seq}:{step.Tool}"), ("$now", now));
            }
            c.Exec("UPDATE agent_runs SET plan_json = $p, plan_version = $v, updated_at = $now WHERE id = $id", tx,
                ("$p", plan.ToJson()), ("$v", version), ("$now", now), ("$id", runId));
            InsertEvent(c, tx, runId, null, "info", "plan", $"Plan v{version} with {steps.Count} step(s).", plan.ToJson());
        });
    }

    public void ApprovePlan(string runId) =>
        ws.Write(WorkspaceChange.Agent, (c, tx) =>
        {
            c.Exec("UPDATE agent_runs SET plan_approved_version = plan_version, updated_at = $now WHERE id = $id", tx, ("$now", Clock.Stamp()), ("$id", runId));
            InsertEvent(c, tx, runId, null, "info", "approval", "Plan approved by the user.", null);
        });

    // ---- steps --------------------------------------------------------------------------------

    public List<StepRecord> Steps(string runId, bool includeSuperseded = false) =>
        ws.Read(c => c.Query($"SELECT {StepColumns} FROM agent_steps WHERE run_id = $id {(includeSuperseded ? "" : "AND status <> 'superseded'")} ORDER BY seq",
            MapStep, null, ("$id", runId)));

    public StepRecord? Step(string id) =>
        ws.Read(c => c.Query($"SELECT {StepColumns} FROM agent_steps WHERE id = $id", MapStep, null, ("$id", id)).FirstOrDefault());

    public void UpdateStep(string stepId, StepStatus status, ApprovalState? approval = null, string? resultJson = null, string? error = null,
        string? verificationJson = null, bool startAttempt = false, bool finished = false, bool decided = false, IReadOnlyList<int>? excluded = null)
    {
        ws.Write(WorkspaceChange.Agent, (c, tx) => c.Exec("""
            UPDATE agent_steps SET status = $s,
                approval = coalesce($a, approval),
                result_json = CASE WHEN $res IS NULL THEN result_json ELSE $res END,
                error = $err,
                verification_json = CASE WHEN $ver IS NULL THEN verification_json ELSE $ver END,
                attempts = attempts + $inc,
                started_at = CASE WHEN $inc = 1 THEN $now ELSE started_at END,
                finished_at = CASE WHEN $fin = 1 THEN $now ELSE finished_at END,
                decided_at = CASE WHEN $dec = 1 THEN $now ELSE decided_at END,
                excluded_json = CASE WHEN $ex IS NULL THEN excluded_json ELSE $ex END
            WHERE id = $id
            """, tx, ("$s", status.Name()), ("$a", approval?.Name()), ("$res", resultJson), ("$err", error), ("$ver", verificationJson),
            ("$inc", startAttempt ? 1 : 0), ("$now", Clock.Stamp()), ("$fin", finished ? 1 : 0), ("$dec", decided ? 1 : 0),
            ("$ex", excluded is null ? null : JsonSerializer.Serialize(excluded)), ("$id", stepId)));
    }

    // ---- file operations ----------------------------------------------------------------------

    public List<FileOpRecord> FileOps(string runId) =>
        ws.Read(c => c.Query($"SELECT {OpColumns} FROM agent_file_ops WHERE run_id = $id ORDER BY seq", MapOp, null, ("$id", runId)));

    public List<FileOpRecord> FileOpsForStep(string stepId) =>
        ws.Read(c => c.Query($"SELECT {OpColumns} FROM agent_file_ops WHERE step_id = $id ORDER BY seq", MapOp, null, ("$id", stepId)));

    /// <summary>Replaces the run's not-yet-executed proposal (proposals are regenerated idempotently).</summary>
    public void ReplaceProposal(string runId, string stepId, IEnumerable<(string Op, string? Source, string Target, string? Sha, long? Size, string Reason, string? Note)> ops)
    {
        ws.Write(WorkspaceChange.Agent, (c, tx) =>
        {
            c.Exec("DELETE FROM agent_file_ops WHERE run_id = $r AND status IN ('proposed', 'excluded')", tx, ("$r", runId));
            var seq = Convert.ToInt32(c.Scalar("SELECT coalesce(max(seq), 0) FROM agent_file_ops WHERE run_id = $r", tx, ("$r", runId)));
            foreach (var o in ops)
                c.Exec($"""
                    INSERT INTO agent_file_ops ({OpColumns})
                    VALUES ($id, $r, $step, $seq, $op, $src, $dst, $sha, $size, $reason, 'proposed', $note, NULL, NULL, NULL)
                    """, tx, ("$id", Ids.New()), ("$r", runId), ("$step", stepId), ("$seq", ++seq), ("$op", o.Op), ("$src", o.Source),
                    ("$dst", o.Target), ("$sha", o.Sha), ("$size", o.Size), ("$reason", o.Reason), ("$note", o.Note));
        });
    }

    public FileOpRecord AddFileOp(string runId, string stepId, string op, string? source, string target, string? sha, long? size, string reason)
    {
        var id = Ids.New();
        ws.Write(WorkspaceChange.Agent, (c, tx) =>
        {
            var seq = Convert.ToInt32(c.Scalar("SELECT coalesce(max(seq), 0) + 1 FROM agent_file_ops WHERE run_id = $r", tx, ("$r", runId)));
            c.Exec($"""
                INSERT INTO agent_file_ops ({OpColumns})
                VALUES ($id, $r, $step, $seq, $op, $src, $dst, $sha, $size, $reason, 'proposed', NULL, NULL, NULL, NULL)
                """, tx, ("$id", id), ("$r", runId), ("$step", stepId), ("$seq", seq), ("$op", op), ("$src", source), ("$dst", target),
                ("$sha", sha), ("$size", size), ("$reason", reason));
        });
        return FileOps(runId).Single(o => o.Id == id);
    }

    public void AssignOpsToStep(string runId, string stepId) =>
        ws.Write(WorkspaceChange.Agent, (c, tx) => c.Exec(
            "UPDATE agent_file_ops SET step_id = $s WHERE run_id = $r AND status IN ('proposed', 'excluded')", tx, ("$s", stepId), ("$r", runId)));

    public void UpdateOp(string opId, string status, string? note = null, string? error = null, string? target = null)
    {
        ws.Write(WorkspaceChange.Agent, (c, tx) => c.Exec("""
            UPDATE agent_file_ops SET status = $s, note = coalesce($note, note), error = $err,
                target_rel = coalesce($dst, target_rel),
                applied_at = CASE WHEN $s = 'applied' THEN $now ELSE applied_at END,
                undone_at = CASE WHEN $s = 'undone' THEN $now ELSE undone_at END
            WHERE id = $id
            """, tx, ("$s", status), ("$note", note), ("$err", error), ("$dst", target), ("$now", Clock.Stamp()), ("$id", opId)));
    }

    public void SetOpHash(string opId, string sha) =>
        ws.Write(WorkspaceChange.Agent, (c, tx) => c.Exec("UPDATE agent_file_ops SET sha256 = $s WHERE id = $id", tx, ("$s", sha), ("$id", opId)));

    // ---- events -------------------------------------------------------------------------------

    public void Log(string runId, string? stepId, string level, string kind, string message, string? dataJson = null) =>
        ws.Write(WorkspaceChange.Agent, (c, tx) => InsertEvent(c, tx, runId, stepId, level, kind, message, dataJson));

    private static void InsertEvent(SqliteConnection c, SqliteTransaction tx, string runId, string? stepId, string level, string kind, string message, string? data) =>
        c.Exec("INSERT INTO agent_events (run_id, step_id, at, level, kind, message, data_json) VALUES ($r, $s, $at, $l, $k, $m, $d)", tx,
            ("$r", runId), ("$s", stepId), ("$at", Clock.Stamp()), ("$l", level), ("$k", kind), ("$m", message), ("$d", data));

    public List<AgentEvent> Events(string runId, int limit = 500) =>
        ws.Read(c => c.Query("SELECT id, run_id, step_id, at, level, kind, message, data_json FROM agent_events WHERE run_id = $r ORDER BY id LIMIT $n",
            r => new AgentEvent(r.GetInt64(0), r.GetString(1), r.NullableString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.NullableString(7)),
            null, ("$r", runId), ("$n", limit)));
}
