using System.Text.Json.Nodes;
using SlothBoss.Core.Storage;

namespace SlothBoss.Core.Agent.Tools;

public enum ToolScope { ApprovedFolder, Workspace }

/// <summary>Declared argument of a tool. Only declared arguments survive validation.</summary>
public sealed record ToolArg(string Name, string Type, string Description, JsonNode? Default = null,
    IReadOnlyList<string>? Allowed = null, int? Min = null, int? Max = null);

public sealed record ToolSpec(
    string Id,
    int Version,
    string Purpose,
    IReadOnlyList<ToolArg> Args,
    string ResultDescription,
    ToolRisk Risk,
    ToolScope Scope,
    TimeSpan Timeout,
    int MaxRetries,
    bool Idempotent,
    bool UsesModel,
    string Verification,
    IReadOnlyList<string> Consumes,
    IReadOnlyList<string> Produces,
    bool Replannable = true)
{
    public string CatalogLine =>
        $"- {Id}: {Purpose}" + (Args.Count == 0 ? "" : " Options: " + string.Join("; ", Args.Select(a =>
            $"{a.Name} ({(a.Allowed is null ? a.Type : string.Join("|", a.Allowed))}{(a.Min is not null ? $", {a.Min}-{a.Max}" : "")})")));
}

public sealed class ToolArgumentException(string message) : SlothBossException(message);

public enum ReconcileResult { NotDone, Done, Uncertain }

public sealed record Reconciliation(ReconcileResult Result, string Note);

/// <summary>One concrete change awaiting approval. <paramref name="Suggested"/> = false means SlothBoss recommends leaving it out.</summary>
public sealed record ApprovalItem(int Index, string Text, string? Detail, bool Suggested = true);

/// <summary>What actually happened when a tool ran. Never inferred from model text.</summary>
public sealed record ToolOutcome(bool Ok, string Summary, JsonObject Data, string? Error = null, bool Transient = false)
{
    public static ToolOutcome Success(string summary, JsonObject? data = null) => new(true, summary, data ?? []);
    public static ToolOutcome Failure(string error, bool transient = false, JsonObject? data = null) => new(false, error, data ?? [], error, transient);
}

/// <summary>Raised from inside a tool when the user asked to pause or cancel between operations.</summary>
public sealed class RunInterruptedException(bool cancel) : Exception(cancel ? "Cancelled by the user." : "Paused by the user.")
{
    public bool Cancel { get; } = cancel;
}

public interface IAgentTool
{
    ToolSpec Spec { get; }

    /// <summary>Non-null when the tool can't be used right now (e.g. web research turned off); it's then hidden from the planner.</summary>
    string? UnavailableReason => null;

    /// <summary>True when the tool sends data off this computer (shown in the UI next to the step).</summary>
    bool SendsDataOnline => false;

    /// <summary>Called before execution and when a plan is validated. Never touches the filesystem.</summary>
    JsonObject ValidateArgs(JsonObject args) => ToolArgs.Validate(Spec, args);

    /// <summary>Concrete items a user approves (and can exclude), e.g. each file move. Empty = approve the step as a whole.</summary>
    IReadOnlyList<ApprovalItem> ApprovalItems(ToolContext ctx) => [];

    /// <summary>
    /// True when this particular execution deserves a human look even if policy would allow it
    /// automatically (e.g. a task whose supporting quote wasn't found in the source).
    /// </summary>
    bool RequiresReview(ToolContext ctx) => false;

    Task<ToolOutcome> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct);

    /// <summary>Independent check of the real state after execution. All checks must pass for the step to succeed.</summary>
    IReadOnlyList<VerificationCheck> Verify(ToolContext ctx, JsonObject args, ToolOutcome outcome) =>
        [new("Tool reported success", outcome.Ok, outcome.Summary)];

    /// <summary>
    /// After an interruption while the step was running: did its side effects happen? Idempotent tools
    /// can always simply run again.
    /// </summary>
    Reconciliation Reconcile(ToolContext ctx) =>
        Spec.Idempotent ? new(ReconcileResult.NotDone, "Safe to run again.") : new(ReconcileResult.Uncertain, "The outcome of the interrupted step is unknown.");

    /// <summary>Reverses the step's side effects where safe. Returns human-readable notes.</summary>
    IReadOnlyList<string> Undo(ToolContext ctx) => [];
}

public static class ToolArgs
{
    /// <summary>Keeps declared arguments only, applies defaults, enforces types, enums and ranges.</summary>
    public static JsonObject Validate(ToolSpec spec, JsonObject args)
    {
        var result = new JsonObject();
        foreach (var a in spec.Args)
        {
            var node = args[a.Name];
            if (node is null || (node is JsonValue sv && sv.TryGetValue<string>(out var str) && str.Length == 0 && a.Type != "string"))
            {
                if (a.Default is not null) result[a.Name] = a.Default.DeepClone();
                continue;
            }
            switch (a.Type)
            {
                case "string":
                    if (node is not JsonValue v || !v.TryGetValue<string>(out var s)) throw new ToolArgumentException($"{spec.Id}: '{a.Name}' must be text.");
                    s = s.Trim();
                    if (s.Length == 0) { if (a.Default is not null) result[a.Name] = a.Default.DeepClone(); continue; }
                    if (a.Allowed is not null && !a.Allowed.Contains(s)) throw new ToolArgumentException($"{spec.Id}: '{a.Name}' must be one of {string.Join(", ", a.Allowed)}.");
                    if (s.Length > 500) throw new ToolArgumentException($"{spec.Id}: '{a.Name}' is too long.");
                    result[a.Name] = s;
                    break;
                case "integer":
                    int i;
                    if (node is JsonValue iv && iv.TryGetValue<int>(out i)) { }
                    else if (node is JsonValue dv && dv.TryGetValue<double>(out var d) && Math.Abs(d - Math.Round(d)) < 1e-9) i = (int)d;
                    else throw new ToolArgumentException($"{spec.Id}: '{a.Name}' must be a whole number.");
                    if (a.Min is not null && i < a.Min || a.Max is not null && i > a.Max) throw new ToolArgumentException($"{spec.Id}: '{a.Name}' must be between {a.Min} and {a.Max}.");
                    result[a.Name] = i;
                    break;
                case "boolean":
                    if (node is not JsonValue bv || !bv.TryGetValue<bool>(out var b)) throw new ToolArgumentException($"{spec.Id}: '{a.Name}' must be true or false.");
                    result[a.Name] = b;
                    break;
                default:
                    throw new InvalidOperationException("Unknown arg type " + a.Type);
            }
        }
        return result;
    }

    public static string Str(JsonObject args, string name) => args[name]?.GetValue<string>() ?? "";
    public static int Int(JsonObject args, string name, int fallback) => args[name]?.GetValue<int>() ?? fallback;
    public static bool Bool(JsonObject args, string name) => args[name]?.GetValue<bool>() ?? false;
}

/// <summary>Everything a tool may use. Tools get no other access to the system.</summary>
public sealed class ToolContext
{
    private readonly FileSandbox? _sandbox;

    public required Workspace Ws { get; init; }
    public required RunRecord Run { get; init; }
    public required StepRecord Step { get; init; }
    public required JsonObject Artifacts { get; init; }
    public required AgentUsage Usage { get; init; }
    public required AgentBudget Budget { get; init; }
    public IAgentModel? Model { get; init; }
    public required Action<string, string> Log { get; init; }
    /// <summary>Throws <see cref="RunInterruptedException"/> if pause/cancel was requested.</summary>
    public required Action CheckStop { get; init; }
    public required Action PersistProgress { get; init; }

    public FileSandbox? MaybeSandbox => _sandbox;
    public FileSandbox Sandbox => _sandbox ?? throw new SandboxViolationException("This step needs an approved folder, and the run has none.");

    public ToolContext(FileSandbox? sandbox) => _sandbox = sandbox;

    public IAgentModel RequireModel() =>
        Model ?? throw new SlothBossException("This step needs the local AI model, which is not available. Start Ollama and choose a Gemma model, then retry.");

    /// <summary>Counts one model call against the run budget (throws when exhausted).</summary>
    public void CountModelCall()
    {
        if (Usage.ModelCalls >= Budget.MaxModelCalls) throw new BudgetExceededException("model calls");
        Usage.ModelCalls++;
        PersistProgress();
    }

    public void CountFileOp()
    {
        if (Usage.FileOps >= Budget.MaxFileOps) throw new BudgetExceededException("file operations");
        Usage.FileOps++;
    }

    public Task<T> AskModelAsync<T>(string prompt, string schema, Func<string, T> parse, CancellationToken ct) =>
        ModelCalls.StructuredAsync(RequireModel(), prompt, schema, parse, CountModelCall, ct);

    public JsonArray ArtifactArray(string name) => Artifacts[name] as JsonArray ?? [];
}
