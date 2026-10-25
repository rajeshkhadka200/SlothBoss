using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using SlothBoss.Core.Agent.Tools;
using SlothBoss.Core.Ai;
using SlothBoss.Core.Storage;

namespace SlothBoss.Core.Agent;

/// <summary>
/// Drives agent runs: plan → (approve) → execute one tool → verify → persist → decide (continue, retry,
/// replan, ask, stop) → final verification. All permission and approval decisions are made here, in
/// code; the model only proposes plans and produces content.
/// </summary>
public sealed class AgentRuntime
{
    private readonly Workspace _ws;
    private readonly Func<IAgentModel?> _model;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();
    private readonly ConcurrentDictionary<string, Task<RunRecord>> _tasks = new();

    public IReadOnlyList<IAgentTool> Tools { get; }
    /// <summary>Delay before retry n (overridable for tests).</summary>
    public Func<int, TimeSpan> RetryDelay { get; set; } = n => TimeSpan.FromSeconds(Math.Min(10, 2 * n));
    /// <summary>Raised (on a worker thread) when a background run throws unexpectedly.</summary>
    public event Action<string, Exception>? BackgroundError;

    private AgentStore Store => _ws.Agent;

    public AgentRuntime(Workspace ws, Func<IAgentModel?> model, IReadOnlyList<IAgentTool>? tools = null)
    {
        _ws = ws;
        _model = model;
        Tools = tools ?? ToolRegistry.Default();
    }

    public IAgentTool Tool(string id) => Tools.FirstOrDefault(t => t.Spec.Id == id) ?? throw new SlothBossException($"Unknown tool '{id}'.");

    /// <summary>Tools the planner may choose from right now (e.g. web tools only when web research is enabled).</summary>
    public IReadOnlyList<IAgentTool> AvailableTools() => Tools.Where(t => t.UnavailableReason is null).ToList();

    public bool IsActive(string runId) => _active.ContainsKey(runId);

    /// <summary>The concrete items (file moves, tasks…) a waiting step would apply, for the approval screen.</summary>
    public IReadOnlyList<ApprovalItem> ApprovalItemsFor(string stepId)
    {
        var step = Store.Step(stepId) ?? throw new SlothBossException("That step no longer exists.");
        var run = Store.Run(step.RunId)!;
        return Tool(step.Tool).ApprovalItems(Context(run, step, (JsonObject)run.Artifacts.DeepClone(), run.Usage, () => { }));
    }

    // ---- starting and controlling runs ----------------------------------------------------------

    /// <summary>Creates a run. <paramref name="useModel"/> = false uses the rules planner and is labelled as such.</summary>
    public RunRecord Create(string goal, AutonomyMode mode, string? folderId, bool useModel = true, AgentBudget? budget = null)
    {
        var folder = folderId is null ? null : Store.Folder(folderId) ?? throw new SlothBossException("That folder is no longer approved.");
        string planner;
        if (useModel)
        {
            var model = _model() ?? throw new SlothBossException(
                "The local AI model isn't available, so SlothBoss can't plan this. Start Ollama with a Gemma model, or run it without AI (folder tidying only).");
            planner = model.PlannerId;
        }
        else planner = "rules";
        return Store.CreateRun(goal, mode, planner, folder, budget ?? new AgentBudget());
    }

    /// <summary>Runs in the background until the run needs the user, finishes or is stopped.</summary>
    public void Kick(string runId) => _ = ExecuteAsync(runId);

    public Task<RunRecord> ExecuteAsync(string runId, CancellationToken ct = default)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!_active.TryAdd(runId, cts))
        {
            cts.Dispose();
            return _tasks.TryGetValue(runId, out var existing) ? existing : Task.FromResult(Store.Run(runId)!);
        }
        var task = Task.Run(async () =>
        {
            try { return await DriveAsync(runId, cts.Token).ConfigureAwait(false); }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                BackgroundError?.Invoke(runId, e);
                TryFail(runId, "Unexpected error: " + e.Message);
                return Store.Run(runId)!;
            }
            finally
            {
                _active.TryRemove(runId, out _);
                _tasks.TryRemove(runId, out _);
                cts.Dispose();
            }
        });
        _tasks[runId] = task;
        return task;
    }

    private void TryFail(string runId, string reason)
    {
        try
        {
            var run = Store.Run(runId)!;
            if (!run.State.IsTerminal() && RunStateMachine.CanMove(run.State, RunState.Failed)) Store.SetState(runId, RunState.Failed, reason);
            Store.Log(runId, null, "error", "error", reason);
        }
        catch (SlothBossException) { }
    }

    public void Pause(string runId)
    {
        var run = Store.Run(runId)!;
        if (IsActive(runId)) { Store.SetControl(runId, "pause"); Store.Log(runId, null, "info", "control", "Pause requested; stopping after the current operation."); }
        else if (RunStateMachine.CanMove(run.State, RunState.Paused) && !run.State.IsTerminal()) Store.SetState(runId, RunState.Paused, "Paused by you.");
    }

    public void Resume(string runId)
    {
        var run = Store.Run(runId)!;
        Store.SetControl(runId, null);
        // A run interrupted before its plan was saved plans again; otherwise it continues where it stopped.
        if (run.State == RunState.Paused) Store.SetState(runId, run.PlanVersion == 0 ? RunState.Planning : RunState.Ready, "Resumed.");
        Kick(runId);
    }

    /// <summary>Waits for the run's background work (if any) to reach a stopping point.</summary>
    public Task WaitIdleAsync(string runId) => _tasks.TryGetValue(runId, out var t) ? t : Task.CompletedTask;

    public void Cancel(string runId)
    {
        Store.Log(runId, null, "info", "control", "Cancel requested.");
        if (_active.TryGetValue(runId, out var cts))
        {
            Store.SetControl(runId, "cancel");
            cts.Cancel();
            return;
        }
        var run = Store.Run(runId)!;
        if (!run.State.IsTerminal()) Finish(runId, cancelled: true);
    }

    public void ApprovePlan(string runId)
    {
        var run = Store.Run(runId)!;
        if (run.State != RunState.AwaitingApproval || run.PlanApprovedVersion == run.PlanVersion)
            throw new SlothBossException("There is no plan waiting for approval.");
        Store.ApprovePlan(runId);
        Store.SetState(runId, RunState.Ready, "Plan approved.");
        Kick(runId);
    }

    public void ApproveStep(string runId, string stepId, IReadOnlyList<int>? excluded = null)
    {
        var step = Store.Step(stepId) ?? throw new SlothBossException("That step no longer exists.");
        if (step.RunId != runId || step.Status != StepStatus.AwaitingApproval) throw new SlothBossException("That step isn't waiting for approval.");
        Store.UpdateStep(stepId, StepStatus.Pending, ApprovalState.Approved, decided: true, excluded: excluded ?? []);
        Store.Log(runId, stepId, "info", "approval",
            $"You approved {step.Tool}" + (excluded is { Count: > 0 } ? $", excluding {excluded.Count} item(s)." : "."));
        Store.SetState(runId, RunState.Ready, "Approved.");
        Kick(runId);
    }

    public void RejectStep(string runId, string stepId)
    {
        var step = Store.Step(stepId) ?? throw new SlothBossException("That step no longer exists.");
        if (step.RunId != runId || step.Status != StepStatus.AwaitingApproval) throw new SlothBossException("That step isn't waiting for approval.");
        Store.UpdateStep(stepId, StepStatus.Rejected, ApprovalState.Rejected, error: "Rejected by you; nothing was changed.", finished: true, decided: true);
        Store.Log(runId, stepId, "info", "approval", $"You rejected {step.Tool}. Nothing was changed by it.");
        SkipDependents(runId, step);
        Store.SetState(runId, RunState.Ready, "Continuing without the rejected step.");
        Kick(runId);
    }

    /// <summary>Approves a folder for a run that was waiting for one, then plans again.</summary>
    public void ProvideFolder(string runId, string folderId)
    {
        var run = Store.Run(runId)!;
        if (run.State != RunState.NeedsInput) throw new SlothBossException("This run isn't waiting for input.");
        Store.SetFolder(runId, Store.Folder(folderId) ?? throw new SlothBossException("That folder is no longer approved."));
        Store.SetState(runId, RunState.Planning, "Folder provided.");
        Kick(runId);
    }

    /// <summary>For a step whose outcome was uncertain after an interruption: the user says whether it happened.</summary>
    public void ResolveUncertain(string runId, string stepId, bool alreadyDone)
    {
        var step = Store.Step(stepId) ?? throw new SlothBossException("That step no longer exists.");
        if (step.Status != StepStatus.Uncertain) throw new SlothBossException("That step isn't uncertain.");
        if (alreadyDone)
            Store.UpdateStep(stepId, StepStatus.Succeeded, resultJson: Result("Marked as done by you after an interruption.", []), finished: true);
        else
            Store.UpdateStep(stepId, StepStatus.Pending);
        Store.Log(runId, stepId, "info", "resolve", alreadyDone ? "You confirmed the interrupted step had finished." : "You chose to run the interrupted step again.");
        Store.SetState(runId, RunState.Ready, "Resolved.");
        Kick(runId);
    }

    /// <summary>Re-opens a finished run and retries its failed or skipped steps (not rejected ones).</summary>
    public void RetryFailed(string runId)
    {
        var run = Store.Run(runId)!;
        if (run.State is not (RunState.Failed or RunState.PartiallyCompleted)) throw new SlothBossException("Only failed or partly done runs can be retried.");
        if (run.UndoneAt is not null) throw new SlothBossException("This run was undone; start a new run instead.");
        var steps = Store.Steps(runId).Where(s => s.Status is StepStatus.Failed or StepStatus.Skipped).ToList();
        if (steps.Count == 0 && run.PlanVersion > 0) throw new SlothBossException("There are no failed steps to retry.");
        foreach (var s in steps) Store.UpdateStep(s.Id, StepStatus.Pending);
        // The written report should describe the final outcome, so it is refreshed after the retried steps.
        foreach (var r in Store.Steps(runId).Where(s => s.Tool == "report.create" && s.Status == StepStatus.Succeeded))
        {
            Store.UpdateStep(r.Id, StepStatus.Pending);
            Store.Log(runId, r.Id, "info", "retry", "The report will be refreshed after the retried steps.");
        }
        Store.SetState(runId, run.PlanVersion == 0 ? RunState.Planning : RunState.Ready, "Retrying failed steps.");
        Kick(runId);
    }

    // ---- the loop ---------------------------------------------------------------------------------

    private async Task<RunRecord> DriveAsync(string runId, CancellationToken ct)
    {
        var run = Store.Run(runId)!;
        var artifacts = (JsonObject)run.Artifacts.DeepClone();
        var usage = run.Usage;
        var clock = Stopwatch.StartNew();
        var baseSeconds = usage.ActiveSeconds;
        void Persist() { usage.ActiveSeconds = baseSeconds + clock.Elapsed.TotalSeconds; Store.SaveUsage(runId, usage); Store.SaveArtifacts(runId, artifacts); }

        try
        {
            while (true)
            {
                run = Store.Run(runId)!;
                if (run.Control == "cancel" || ct.IsCancellationRequested) { Persist(); return Finish(runId, cancelled: true); }
                if (run.Control == "pause")
                {
                    Store.SetControl(runId, null);
                    if (RunStateMachine.CanMove(run.State, RunState.Paused)) Store.SetState(runId, RunState.Paused, "Paused by you.");
                    Persist();
                    return Store.Run(runId)!;
                }
                if (baseSeconds + clock.Elapsed.TotalSeconds > run.Budget.MaxMinutes * 60) throw new BudgetExceededException("time");

                switch (run.State)
                {
                    case RunState.Created:
                        Store.SetState(runId, RunState.Planning);
                        continue;
                    case RunState.Planning:
                        await PlanAsync(run, usage, artifacts, Persist, ct).ConfigureAwait(false);
                        Persist();
                        continue;
                    case RunState.Ready or RunState.Running or RunState.Retrying:
                    {
                        var steps = Store.Steps(runId);
                        if (steps.Any(s => s.Status == StepStatus.Uncertain))
                        {
                            Store.SetState(runId, RunState.NeedsInput, "An interrupted step needs your decision.");
                            return Store.Run(runId)!;
                        }
                        var step = steps.FirstOrDefault(s => s.Status == StepStatus.Pending);
                        if (step is null) { Persist(); return Verify(runId, artifacts, usage); }
                        if (!Authorize(run, step)) return Store.Run(runId)!;
                        if (Store.Step(step.Id)!.Status != StepStatus.Pending) continue; // skipped by policy
                        if (run.State != RunState.Running) Store.SetState(runId, RunState.Running);
                        var stop = await RunStepAsync(Store.Run(runId)!, Store.Step(step.Id)!, usage, artifacts, Persist, ct).ConfigureAwait(false);
                        Persist();
                        if (stop) return Store.Run(runId)!;
                        continue;
                    }
                    default:
                        Persist();
                        return run; // AwaitingApproval, Paused, NeedsInput, Verifying(handled), terminal
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || Store.Run(runId)!.Control == "cancel")
        {
            Persist();
            return Finish(runId, cancelled: true);
        }
        catch (BudgetExceededException e)
        {
            Store.Log(runId, null, "warning", "budget", e.Message + " Stopping here.");
            foreach (var s in Store.Steps(runId).Where(s => s.Status is StepStatus.Pending or StepStatus.Running))
                Store.UpdateStep(s.Id, StepStatus.Skipped, error: e.Message, finished: true);
            Persist();
            if (Store.Run(runId)!.State == RunState.Planning) { Store.SetState(runId, RunState.Failed, e.Message); return Store.Run(runId)!; }
            return Verify(runId, artifacts, usage, budgetNote: e.Message);
        }
    }

    // ---- planning ---------------------------------------------------------------------------------

    private IAgentPlanner PlannerFor(RunRecord run)
    {
        if (run.Planner == "rules") return new RulesPlanner();
        var model = _model();
        if (model is null || model.PlannerId != run.Planner)
            throw new OllamaUnavailableException($"This run was planned with {run.Planner.Replace("gemma:", "")}, which isn't available right now. " +
                                                 "SlothBoss doesn't switch models on its own. Start Ollama with that model, then retry.");
        return new ModelPlanner(model);
    }

    private FileSandbox? Sandbox(RunRecord run) => run.FolderPath is null ? null : new FileSandbox(run.FolderPath);

    private async Task PlanAsync(RunRecord run, AgentUsage usage, JsonObject artifacts, Action persist, CancellationToken ct)
    {
        AgentPlan plan;
        try
        {
            var planner = PlannerFor(run);
            var (folder, preview) = PlanningContext.Describe(Sandbox(run), 200);
            Store.Log(run.Id, null, "info", "planning", $"Planning with {planner.DisplayName}…");
            plan = await planner.PlanAsync(new PlanningInput(run, AvailableTools(), folder, preview, _ws.Pages.List().Count > 0,
                () => CountModel(run, usage, persist)), ct).ConfigureAwait(false);
        }
        catch (NeedsInputException e) { Store.SetState(run.Id, RunState.NeedsInput, e.Message); return; }
        catch (Exception e) when (e is OllamaException or ModelOutputException or SandboxViolationException)
        {
            Store.SetState(run.Id, RunState.Failed, "Planning failed: " + e.Message);
            return;
        }

        if (plan.Steps.Count == 0)
        {
            Store.SetState(run.Id, plan.MissingInformation.Count > 0 ? RunState.NeedsInput : RunState.Failed,
                plan.MissingInformation.Count > 0 ? string.Join(" ", plan.MissingInformation) : "The planner found nothing it could do for this goal.");
            return;
        }
        Store.SavePlan(run.Id, plan, plan.Steps.Select(s => (s, Tool(s.Tool).Spec.Version, Tool(s.Tool).Spec.Risk)).ToList());
        foreach (var note in plan.RuntimeNotes) Store.Log(run.Id, null, "info", "plan-check", note);
        foreach (var missing in plan.MissingInformation) Store.Log(run.Id, null, "warning", "plan", "Planner notes: " + missing);
        if (run.Mode == AutonomyMode.Supervised)
            Store.SetState(run.Id, RunState.AwaitingApproval, "Review the plan. Once approved, SlothBoss carries it out and only stops if the plan has to change.");
        else
            Store.SetState(run.Id, RunState.Ready, $"Plan ready: {plan.Steps.Count} step(s).");
    }

    private void CountModel(RunRecord run, AgentUsage usage, Action persist)
    {
        if (usage.ModelCalls >= run.Budget.MaxModelCalls) throw new BudgetExceededException("model calls");
        usage.ModelCalls++;
        persist();
    }

    // ---- permissions ------------------------------------------------------------------------------

    /// <summary>Decides, in code, whether a step may run now. Returns false when the run must wait for the user.</summary>
    private bool Authorize(RunRecord run, StepRecord step)
    {
        var tool = Tool(step.Tool);
        var policy = Store.Policy(step.Tool);
        if (policy == ToolPolicy.Deny)
        {
            Store.UpdateStep(step.Id, StepStatus.Skipped, ApprovalState.NotRequired, error: "Not allowed by your agent permission settings.", finished: true);
            Store.Log(run.Id, step.Id, "warning", "permission", $"{step.Tool} is set to “Never”, so it was skipped.");
            SkipDependents(run.Id, step);
            return true;
        }
        if (step.Approval == ApprovalState.Approved) return true;

        if (run.Mode == AutonomyMode.Supervised && run.PlanApprovedVersion != step.PlanVersion)
        {
            Store.SetState(run.Id, RunState.AwaitingApproval, "The plan changed. Review and approve it before SlothBoss continues.");
            return false;
        }

        var needsApproval = policy switch
        {
            ToolPolicy.Ask => true,
            ToolPolicy.Auto => false,
            _ => tool.Spec.Risk switch
            {
                ToolRisk.Read => false,
                ToolRisk.Create => run.Mode == AutonomyMode.Assist,
                ToolRisk.Modify => run.Mode != AutonomyMode.Supervised,
                _ => true,
            },
        };
        var reviewCtx = Context(run, step, (JsonObject)Store.Run(run.Id)!.Artifacts.DeepClone(), run.Usage, () => { });
        if (!needsApproval && policy != ToolPolicy.Auto && tool.RequiresReview(reviewCtx))
        {
            needsApproval = true;
            Store.Log(run.Id, step.Id, "info", "approval", $"{step.Tool} includes items SlothBoss couldn't verify, so it asks you first.");
        }
        if (!needsApproval) return true;

        // Nothing to approve (e.g. no proposed moves): skip the request rather than ask about an empty change.
        var items = tool.ApprovalItems(reviewCtx);
        if (items.Count == 0 && tool.Spec.Risk != ToolRisk.Read && step.Tool is "files.apply_organization" or "tasks.create")
        {
            Store.UpdateStep(step.Id, StepStatus.Skipped, error: "Nothing to change.", finished: true);
            Store.Log(run.Id, step.Id, "info", "approval", $"{step.Tool} had nothing to apply, so no approval was needed.");
            return true;
        }
        Store.UpdateStep(step.Id, StepStatus.AwaitingApproval, ApprovalState.Required);
        Store.SetState(run.Id, RunState.AwaitingApproval, $"Waiting for your approval: {tool.Spec.Purpose}");
        return false;
    }

    // ---- executing one step -----------------------------------------------------------------------

    private ToolContext Context(RunRecord run, StepRecord step, JsonObject artifacts, AgentUsage usage, Action persist) =>
        new(Sandbox(run))
        {
            Ws = _ws, Run = run, Step = step, Artifacts = artifacts, Usage = usage, Budget = run.Budget, Model = _model(),
            Log = (level, msg) => Store.Log(run.Id, step.Id, level, "tool", msg),
            CheckStop = () =>
            {
                var control = Store.Run(run.Id)!.Control;
                if (control is not null) throw new RunInterruptedException(control == "cancel");
            },
            PersistProgress = persist,
        };

    /// <summary>Runs one step (with bounded retries). Returns true when the loop must stop (pause/cancel/approval).</summary>
    private async Task<bool> RunStepAsync(RunRecord run, StepRecord step, AgentUsage usage, JsonObject artifacts, Action persist, CancellationToken ct)
    {
        var tool = Tool(step.Tool);
        if (usage.ToolCalls >= run.Budget.MaxToolCalls) throw new BudgetExceededException("tool calls");
        usage.ToolCalls++;
        persist();

        if (tool.UnavailableReason is { } unavailable)
        {
            Store.UpdateStep(step.Id, StepStatus.Failed, error: unavailable, finished: true);
            Store.Log(run.Id, step.Id, "warning", "result", $"✗ {step.Tool}: {unavailable}");
            SkipDependents(run.Id, Store.Step(step.Id)!);
            return false;
        }

        JsonObject args;
        try { args = tool.ValidateArgs(step.Args); }
        catch (ToolArgumentException e)
        {
            Store.UpdateStep(step.Id, StepStatus.Failed, error: "Invalid arguments: " + e.Message, finished: true);
            await HandleFailureAsync(run, Store.Step(step.Id)!, tool, usage, artifacts, persist, ct).ConfigureAwait(false);
            return false;
        }

        Store.UpdateStep(step.Id, StepStatus.Running, startAttempt: true);
        step = Store.Step(step.Id)!;
        Store.Log(run.Id, step.Id, "info", "step", $"Running {step.Tool}" + (step.Attempts > 1 ? $" (attempt {step.Attempts})" : "") + $": {step.Purpose}");
        var ctx = Context(run, step, artifacts, usage, persist);

        ToolOutcome outcome;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(tool.Spec.Timeout);
        try
        {
            outcome = await tool.ExecuteAsync(ctx, args, timeout.Token).ConfigureAwait(false);
        }
        catch (RunInterruptedException e)
        {
            // Stopped between operations: completed operations stay recorded; the step resumes later.
            Store.UpdateStep(step.Id, StepStatus.Pending);
            persist();
            if (e.Cancel) { Finish(run.Id, cancelled: true); return true; }
            Store.SetControl(run.Id, null);
            Store.SetState(run.Id, RunState.Paused, "Paused by you between operations.");
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            outcome = ToolOutcome.Failure($"{step.Tool} took longer than {tool.Spec.Timeout.TotalSeconds:0} seconds.", transient: true);
        }
        catch (BudgetExceededException)
        {
            Store.UpdateStep(step.Id, StepStatus.Failed, error: "Stopped: budget used up.", finished: true);
            throw;
        }
        catch (InferenceTimeoutException e) { outcome = ToolOutcome.Failure(e.Message, transient: true); }
        catch (OllamaException e) { outcome = ToolOutcome.Failure("Local AI error: " + e.Message); }
        catch (ModelOutputException e) { outcome = ToolOutcome.Failure("The model's output couldn't be used: " + e.Message); }
        catch (SandboxViolationException e) { outcome = ToolOutcome.Failure("Blocked: " + e.Message); }
        catch (IOException e) { outcome = ToolOutcome.Failure(e.Message, transient: e is not FileCollisionException); }
        catch (Exception e) when (e is SlothBossException or UnauthorizedAccessException) { outcome = ToolOutcome.Failure(e.Message); }

        persist();
        if (outcome.Ok)
        {
            IReadOnlyList<VerificationCheck> checks;
            try { checks = tool.Verify(ctx, args, outcome); }
            catch (Exception e) when (e is SlothBossException or IOException or UnauthorizedAccessException)
            {
                checks = [new("Verification could run", false, e.Message)];
            }
            var verification = new JsonArray([.. checks.Select(c => (JsonNode)new JsonObject { ["name"] = c.Name, ["passed"] = c.Passed, ["detail"] = c.Detail })]);
            if (checks.All(c => c.Passed))
            {
                Store.UpdateStep(step.Id, StepStatus.Succeeded, resultJson: Result(outcome.Summary, outcome.Data), verificationJson: verification.ToJsonString(), finished: true);
                Store.Log(run.Id, step.Id, "info", "result", $"✓ {step.Tool}: {outcome.Summary}");
                return false;
            }
            var failed = string.Join("; ", checks.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Detail}"));
            Store.UpdateStep(step.Id, StepStatus.Failed, resultJson: Result(outcome.Summary, outcome.Data), error: "Verification failed: " + failed,
                verificationJson: verification.ToJsonString(), finished: true);
            Store.Log(run.Id, step.Id, "error", "verification", $"{step.Tool} reported success but verification failed: {failed}");
            await HandleFailureAsync(run, Store.Step(step.Id)!, tool, usage, artifacts, persist, ct, allowRetry: false).ConfigureAwait(false);
            return false;
        }

        Store.UpdateStep(step.Id, StepStatus.Failed, resultJson: Result(outcome.Summary, outcome.Data), error: outcome.Error, finished: true);
        Store.Log(run.Id, step.Id, outcome.Transient ? "warning" : "error", "result", $"✗ {step.Tool}: {outcome.Error}");
        var retried = outcome.Transient && step.Attempts <= Math.Min(tool.Spec.MaxRetries, run.Budget.MaxRetriesPerStep);
        if (retried)
        {
            Store.SetState(run.Id, RunState.Retrying, $"Retrying {step.Tool} after a temporary problem.");
            Store.UpdateStep(step.Id, StepStatus.Pending, error: outcome.Error);
            await Task.Delay(RetryDelay(step.Attempts), ct).ConfigureAwait(false);
            Store.SetState(run.Id, RunState.Running);
            return false;
        }
        await HandleFailureAsync(run, Store.Step(step.Id)!, tool, usage, artifacts, persist, ct).ConfigureAwait(false);
        return false;
    }

    private static string Result(string summary, JsonObject data) => new JsonObject { ["summary"] = summary, ["data"] = data.DeepClone() }.ToJsonString();

    /// <summary>After a step finally failed: replan with the model when allowed, otherwise skip work that depended on it.</summary>
    private async Task HandleFailureAsync(RunRecord run, StepRecord failed, IAgentTool tool, AgentUsage usage, JsonObject artifacts, Action persist,
        CancellationToken ct, bool allowRetry = true)
    {
        IAgentPlanner? planner = null;
        try { planner = PlannerFor(Store.Run(run.Id)!); }
        catch (OllamaException) { }

        if (tool.Spec.Replannable && planner is { UsesModel: true } && usage.Replans < run.Budget.MaxReplans)
        {
            usage.Replans++;
            persist();
            Store.SetState(run.Id, RunState.Planning, $"{failed.Tool} failed; asking the planner how to continue.");
            var steps = Store.Steps(run.Id);
            try
            {
                var revised = await planner.ReplanAsync(new ReplanInput(Store.Run(run.Id)!, AvailableTools(),
                    steps.Where(s => s.Status == StepStatus.Succeeded).ToList(), failed,
                    steps.Where(s => s.Status == StepStatus.Pending).ToList(),
                    artifacts.Select(kv => kv.Key).ToHashSet(), () => CountModel(run, usage, persist)), ct).ConfigureAwait(false);
                if (revised is not null)
                {
                    if (revised.Steps.Count == 0)
                    {
                        foreach (var s in steps.Where(s => s.Status == StepStatus.Pending))
                            Store.UpdateStep(s.Id, StepStatus.Skipped, error: "The planner decided not to continue after the failure.", finished: true);
                        Store.Log(run.Id, null, "warning", "replan", "Replanning: nothing sensible remains. " + revised.Understanding);
                    }
                    else
                    {
                        Store.SavePlan(run.Id, revised, revised.Steps.Select(s => (s, Tool(s.Tool).Spec.Version, Tool(s.Tool).Spec.Risk)).ToList());
                        Store.Log(run.Id, null, "info", "replan", $"New plan after the failure: {string.Join(" → ", revised.Steps.Select(s => s.Tool))}");
                        foreach (var note in revised.RuntimeNotes) Store.Log(run.Id, null, "info", "plan-check", note);
                    }
                    var nextState = run.Mode == AutonomyMode.Supervised && revised.Steps.Count > 0 ? RunState.AwaitingApproval : RunState.Ready;
                    Store.SetState(run.Id, nextState, nextState == RunState.AwaitingApproval ? "The plan changed after a failure. Review it before SlothBoss continues." : "Continuing with the revised plan.");
                    return;
                }
            }
            catch (Exception e) when (e is OllamaException or ModelOutputException or NeedsInputException)
            {
                Store.Log(run.Id, null, "warning", "replan", "Replanning failed: " + e.Message);
            }
            Store.SetState(run.Id, RunState.Ready, "Continuing with the rest of the original plan.");
        }
        SkipDependents(run.Id, failed);
    }

    /// <summary>Marks pending steps that need an artifact no completed or earlier pending step can provide.</summary>
    private void SkipDependents(string runId, StepRecord blocked)
    {
        // Results available = what succeeded steps produced, plus what earlier still-pending steps will produce.
        var steps = Store.Steps(runId);
        var available = steps.Where(s => s.Status == StepStatus.Succeeded).SelectMany(s => Tool(s.Tool).Spec.Produces).ToHashSet();
        foreach (var s in steps.Where(s => s.Status == StepStatus.Pending))
        {
            var spec = Tool(s.Tool).Spec;
            if (spec.Consumes.Any(c => !available.Contains(c)))
            {
                Store.UpdateStep(s.Id, StepStatus.Skipped, error: $"Skipped because {blocked.Tool} did not complete.", finished: true);
                Store.Log(runId, s.Id, "warning", "skip", $"Skipped {s.Tool}: it needs the result of {blocked.Tool}.");
            }
            else foreach (var p in spec.Produces) available.Add(p);
        }
    }

    // ---- verification and completion ---------------------------------------------------------------

    /// <summary>Re-verifies every completed step against the real state and decides the truthful outcome.</summary>
    private RunRecord Verify(string runId, JsonObject artifacts, AgentUsage usage, string? budgetNote = null)
    {
        var run = Store.Run(runId)!;
        if (run.State != RunState.Verifying) Store.SetState(runId, RunState.Verifying);
        run = Store.Run(runId)!;
        var steps = Store.Steps(runId);
        var checks = new List<VerificationCheck>();
        foreach (var s in steps.Where(s => s.Status == StepStatus.Succeeded))
        {
            var tool = Tool(s.Tool);
            try
            {
                var ctx = Context(run, s, artifacts, usage, () => { });
                var recorded = new ToolOutcome(true, ModelPlanner.StepSummary(s), []);
                checks.AddRange(tool.Verify(ctx, s.Args, recorded).Select(c => c with { Name = $"{s.Tool}: {c.Name}" }));
            }
            catch (Exception e) when (e is SlothBossException or IOException or UnauthorizedAccessException)
            {
                checks.Add(new($"{s.Tool}: final check", false, e.Message));
            }
        }

        var ops = Store.FileOps(runId);
        var notDone = new List<string>();
        foreach (var s in steps.Where(s => s.Status is StepStatus.Failed or StepStatus.Skipped or StepStatus.Rejected))
            notDone.Add($"{s.Tool} — {s.Status.Name()}: {s.Error}");
        foreach (var o in ops.Where(o => o.Status is "failed" or "excluded" or "proposed" && o.Op == "move"))
            notDone.Add($"{(o.Status == "excluded" ? "Skipped at your request" : o.Status == "proposed" ? "Not applied" : "Failed")}: {o.SourceRel} → {o.TargetRel}" + (o.Error is null ? "" : $" ({o.Error})"));
        if (budgetNote is not null) notDone.Add(budgetNote);

        // A step that failed and was then replaced by a revised plan counts as recovered — if the newest plan fully succeeded.
        var allSteps = steps.Count > 0 && steps.All(s => s.Status == StepStatus.Succeeded || s.Status == StepStatus.Failed && s.PlanVersion < run.PlanVersion);
        var recovered = steps.Count(s => s.Status == StepStatus.Failed && s.PlanVersion < run.PlanVersion);
        if (allSteps && recovered > 0) notDone.Insert(0, $"Recovered from {recovered} failed step(s) by replanning.");
        var allChecks = checks.All(c => c.Passed);
        var anySuccess = steps.Any(s => s.Status == StepStatus.Succeeded);
        var outcome = allSteps && allChecks && budgetNote is null ? RunState.Completed : anySuccess ? RunState.PartiallyCompleted : RunState.Failed;

        var moved = ops.Count(o => o.Op == "move" && o.Status == "applied");
        var report = new RunReport(
            outcome.Name(),
            outcome switch
            {
                RunState.Completed => "Done — every planned step finished and passed its checks.",
                RunState.PartiallyCompleted => "Partly done. Some steps didn't complete or didn't pass verification — see below.",
                _ => "Not done. Nothing was completed successfully.",
            },
            checks,
            steps.Where(s => s.Status == StepStatus.Succeeded).Select(s => $"{s.Tool}: {ModelPlanner.StepSummary(s)}").ToList(),
            notDone,
            moved,
            ops.Count(o => o.Op == "create_folder" && o.Status == "applied" && o.Note == "created"),
            artifacts["duplicates"] is JsonArray d ? d.Count : 0,
            artifacts["summaries"] is JsonArray sm ? sm.Count : 0,
            artifacts["tasks_created"] is JsonArray tc ? tc.Count : 0,
            artifacts["research_report"]?["page_id"]?.GetValue<string>() ?? artifacts["report"]?["page_id"]?.GetValue<string>(),
            CanUndo: moved > 0 || artifacts["tasks_created"] is JsonArray { Count: > 0 } || artifacts["report"] is not null || artifacts["research_report"] is not null);
        Store.SaveReport(runId, report);
        Store.SetState(runId, outcome, report.Headline);
        return Store.Run(runId)!;
    }

    private RunRecord Finish(string runId, bool cancelled)
    {
        var run = Store.Run(runId)!;
        foreach (var s in Store.Steps(runId).Where(s => s.Status is StepStatus.Pending or StepStatus.AwaitingApproval or StepStatus.Running))
            Store.UpdateStep(s.Id, StepStatus.Skipped, finished: true, error: s.Status == StepStatus.Running || s.Attempts > 0
                ? "Stopped by cancellation; anything it had already done is recorded and can be undone."
                : "Cancelled before it ran.");
        Store.SetControl(runId, null);
        if (cancelled && !run.State.IsTerminal())
        {
            var applied = Store.FileOps(runId).Count(o => o.Status == "applied");
            Store.SetState(runId, RunState.Cancelled, applied > 0 ? $"Cancelled. {applied} operation(s) had already been done and can be undone." : "Cancelled. Nothing was changed.");
        }
        return Store.Run(runId)!;
    }

    // ---- recovery and undo ------------------------------------------------------------------------

    /// <summary>
    /// Call once at startup. Runs that were active when the app stopped are reconciled against the real
    /// filesystem/database and left Paused (or NeedsInput if an outcome is uncertain). Nothing is re-run blindly.
    /// </summary>
    public List<RunRecord> RecoverInterrupted(bool autoResume = false)
    {
        var recovered = new List<RunRecord>();
        foreach (var run in Store.Runs(500).Where(r => r.State.IsActive() && !IsActive(r.Id)))
        {
            var uncertain = false;
            foreach (var s in Store.Steps(run.Id).Where(s => s.Status == StepStatus.Running))
            {
                var tool = Tool(s.Tool);
                Reconciliation rec;
                var artifacts = (JsonObject)run.Artifacts.DeepClone();
                try { rec = tool.Reconcile(Context(run, s, artifacts, run.Usage, () => { })); }
                catch (Exception e) when (e is SlothBossException or IOException) { rec = new(ReconcileResult.Uncertain, e.Message); }
                Store.SaveArtifacts(run.Id, artifacts);
                switch (rec.Result)
                {
                    case ReconcileResult.Done:
                        Store.UpdateStep(s.Id, StepStatus.Succeeded, resultJson: Result(rec.Note, []), finished: true);
                        break;
                    case ReconcileResult.NotDone:
                        Store.UpdateStep(s.Id, StepStatus.Pending);
                        break;
                    default:
                        Store.UpdateStep(s.Id, StepStatus.Uncertain, error: rec.Note);
                        uncertain = true;
                        break;
                }
                Store.Log(run.Id, s.Id, uncertain ? "warning" : "info", "recovery", $"After restart, {s.Tool}: {rec.Note}");
            }
            var target = uncertain ? RunState.NeedsInput : RunState.Paused;
            var from = run.State;
            if (from == RunState.Verifying || !RunStateMachine.CanMove(from, target)) target = RunState.Paused;
            Store.SetState(run.Id, target, uncertain ? "SlothBoss closed during a step whose result is uncertain. Decide how to continue."
                                                     : "SlothBoss closed while this run was working. Resume to continue where it left off.");
            if (autoResume && target == RunState.Paused) Resume(run.Id);
            recovered.Add(Store.Run(run.Id)!);
        }
        return recovered;
    }

    /// <summary>Reverses the run's changes where it is safe: moves back unchanged files, removes created tasks/pages that weren't edited.</summary>
    public IReadOnlyList<string> Undo(string runId)
    {
        var run = Store.Run(runId)!;
        if (IsActive(runId) || !run.State.IsTerminal() && run.State is not (RunState.Paused or RunState.NeedsInput or RunState.AwaitingApproval))
            throw new SlothBossException("Pause or cancel the run before undoing it.");
        if (run.UndoneAt is not null) throw new SlothBossException("This run was already undone.");
        var notes = new List<string>();
        var artifacts = (JsonObject)run.Artifacts.DeepClone();
        foreach (var s in Store.Steps(runId).Where(s => s.Status is StepStatus.Succeeded or StepStatus.Failed or StepStatus.Uncertain).OrderByDescending(s => s.Seq))
        {
            try { notes.AddRange(Tool(s.Tool).Undo(Context(run, s, artifacts, run.Usage, () => { }))); }
            catch (Exception e) when (e is SlothBossException or IOException) { notes.Add($"{s.Tool}: {e.Message}"); }
        }
        Store.MarkUndone(runId);
        Store.Log(runId, null, notes.Count == 0 ? "info" : "warning", "undo",
            notes.Count == 0 ? "Undone: all changes were reversed and checked." : "Undone where safe. " + string.Join(" ", notes));
        if (!run.State.IsTerminal()) Finish(runId, cancelled: true);
        return notes;
    }
}
