using System.Text.Json;
using System.Text.Json.Nodes;
using SlothBoss.Core.Documents;
using SlothBoss.Core.Storage;

namespace SlothBoss.Core.Actions;

public sealed class ActionValidationException(string message) : SlothBossException(message);

/// <summary>
/// Typed, validated action payloads (contracts/schemas/action.schema.json). The application builds
/// these from validated model output; they are re-parsed strictly whenever read back from the database.
/// </summary>
public abstract record ActionPayload
{
    public abstract string Type { get; }

    protected abstract void Write(JsonObject o);

    public string ToJson()
    {
        var o = new JsonObject { ["type"] = Type };
        Write(o);
        return o.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    public static ActionPayload Parse(string json)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException e) { throw new ActionValidationException($"Action payload is not valid JSON: {e.Message}"); }
        if (node is not JsonObject o) throw new ActionValidationException("Action payload must be an object.");
        var type = Str(o, "type") ?? throw new ActionValidationException("Action payload is missing 'type'.");
        ActionPayload payload = type switch
        {
            CreateTaskPayload.TypeName => CreateTaskPayload.From(o),
            CreatePagePayload.TypeName => CreatePagePayload.From(o),
            DraftPageContentPayload.TypeName => DraftPageContentPayload.From(o),
            AddPageReferencePayload.TypeName => AddPageReferencePayload.From(o),
            UpdateTaskPayload.TypeName => UpdateTaskPayload.From(o),
            _ => throw new ActionValidationException($"Unknown action type '{type}'."),
        };
        payload.Validate();
        return payload;
    }

    /// <summary>Structural validation (no database access). Throws <see cref="ActionValidationException"/>.</summary>
    public abstract void Validate();

    protected static void Only(JsonObject o, params string[] keys)
    {
        foreach (var k in o.Select(kv => kv.Key))
            if (k != "type" && !keys.Contains(k)) throw new ActionValidationException($"Unexpected action property '{k}'.");
    }

    protected static string? Str(JsonObject o, string key) => o[key] switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => throw new ActionValidationException($"Action property '{key}' must be a string."),
    };

    protected static string Req(JsonObject o, string key) =>
        Str(o, key) ?? throw new ActionValidationException($"Action payload is missing '{key}'.");

    protected static void RequireId(string? id, string what)
    {
        if (!Ids.IsValid(id)) throw new ActionValidationException($"Action references an invalid {what} id.");
    }

    protected static void RequireTitle(string title, int max)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ActionValidationException("Title must not be empty.");
        if (title.Length > max) throw new ActionValidationException($"Title must be at most {max} characters.");
    }
}

public sealed record CreateTaskPayload(string Title, string? Description, string Priority, string? DueDate, string? PageId) : ActionPayload
{
    public const string TypeName = "create_task";
    public override string Type => TypeName;

    protected override void Write(JsonObject o)
    {
        o["title"] = Title;
        o["description"] = Description;
        o["priority"] = Priority;
        o["due_date"] = DueDate;
        o["page_id"] = PageId;
    }

    internal static CreateTaskPayload From(JsonObject o)
    {
        Only(o, "title", "description", "priority", "due_date", "page_id");
        return new(Req(o, "title"), Str(o, "description"), Req(o, "priority"), Str(o, "due_date"), Str(o, "page_id"));
    }

    public override void Validate()
    {
        RequireTitle(Title, TaskStore.MaxTitleLength);
        if (Description is { Length: > TaskStore.MaxDescriptionLength }) throw new ActionValidationException("Description is too long.");
        if (!TaskPriority.IsValid(Priority)) throw new ActionValidationException($"Unknown priority '{Priority}'.");
        if (DueDate is not null && !Clock.IsValidDate(DueDate)) throw new ActionValidationException($"Invalid due date '{DueDate}'.");
        if (PageId is not null) RequireId(PageId, "page");
    }

    public TaskDraft ToDraft() => new(Title, Description, TaskState.Todo, Priority, DueDate, PageId);
}

public sealed record CreatePagePayload(string Title, string? ParentId, PageDocument Content) : ActionPayload
{
    public const string TypeName = "create_page";
    public override string Type => TypeName;

    protected override void Write(JsonObject o)
    {
        o["title"] = Title;
        o["parent_id"] = ParentId;
        o["content"] = Content.ToJsonNode();
    }

    internal static CreatePagePayload From(JsonObject o)
    {
        Only(o, "title", "parent_id", "content");
        PageDocument content;
        try { content = PageDocument.FromJsonNode(o["content"]); }
        catch (DocumentFormatException e) { throw new ActionValidationException("Invalid page content: " + e.Message); }
        return new(Req(o, "title"), Str(o, "parent_id"), content);
    }

    public override void Validate()
    {
        RequireTitle(Title, PageStore.MaxTitleLength);
        if (ParentId is not null) RequireId(ParentId, "page");
    }
}

public sealed record DraftPageContentPayload(string PageId, IReadOnlyList<Block> Blocks) : ActionPayload
{
    public const string TypeName = "draft_page_content";
    public override string Type => TypeName;

    protected override void Write(JsonObject o)
    {
        o["page_id"] = PageId;
        o["blocks"] = new PageDocument(Blocks).ToJsonNode()["blocks"]!.DeepClone();
    }

    internal static DraftPageContentPayload From(JsonObject o)
    {
        Only(o, "page_id", "blocks");
        PageDocument doc;
        try { doc = PageDocument.FromJsonNode(new JsonObject { ["version"] = 1, ["blocks"] = o["blocks"]?.DeepClone() }); }
        catch (DocumentFormatException e) { throw new ActionValidationException("Invalid blocks: " + e.Message); }
        return new(Req(o, "page_id"), doc.Blocks);
    }

    public override void Validate()
    {
        RequireId(PageId, "page");
        if (Blocks.Count == 0) throw new ActionValidationException("Draft content must contain at least one block.");
    }
}

public sealed record AddPageReferencePayload(string PageId, string TargetPageId) : ActionPayload
{
    public const string TypeName = "add_page_reference";
    public override string Type => TypeName;

    protected override void Write(JsonObject o)
    {
        o["page_id"] = PageId;
        o["target_page_id"] = TargetPageId;
    }

    internal static AddPageReferencePayload From(JsonObject o)
    {
        Only(o, "page_id", "target_page_id");
        return new(Req(o, "page_id"), Req(o, "target_page_id"));
    }

    public override void Validate()
    {
        RequireId(PageId, "page");
        RequireId(TargetPageId, "target page");
        if (PageId == TargetPageId) throw new ActionValidationException("A page cannot reference itself.");
    }
}

/// <summary>Only status, priority and due date are changeable by AI actions.</summary>
public sealed record UpdateTaskPayload(string TaskId, string? Status, string? Priority, bool ChangesDueDate, string? DueDate) : ActionPayload
{
    public const string TypeName = "update_task";
    public override string Type => TypeName;

    protected override void Write(JsonObject o)
    {
        o["task_id"] = TaskId;
        var changes = new JsonObject();
        if (Status is not null) changes["status"] = Status;
        if (Priority is not null) changes["priority"] = Priority;
        if (ChangesDueDate) changes["due_date"] = DueDate;
        o["changes"] = changes;
    }

    internal static UpdateTaskPayload From(JsonObject o)
    {
        Only(o, "task_id", "changes");
        if (o["changes"] is not JsonObject ch) throw new ActionValidationException("update_task requires a 'changes' object.");
        foreach (var k in ch.Select(kv => kv.Key))
            if (k is not ("status" or "priority" or "due_date"))
                throw new ActionValidationException($"AI actions may not change the task field '{k}'.");
        return new(Req(o, "task_id"), Str(ch, "status"), Str(ch, "priority"), ch.ContainsKey("due_date"), Str(ch, "due_date"));
    }

    public override void Validate()
    {
        RequireId(TaskId, "task");
        if (Status is null && Priority is null && !ChangesDueDate) throw new ActionValidationException("update_task must change at least one field.");
        if (Status is not null && !TaskState.IsValid(Status)) throw new ActionValidationException($"Unknown status '{Status}'.");
        if (Priority is not null && !TaskPriority.IsValid(Priority)) throw new ActionValidationException($"Unknown priority '{Priority}'.");
        if (DueDate is not null && !Clock.IsValidDate(DueDate)) throw new ActionValidationException($"Invalid due date '{DueDate}'.");
    }
}
