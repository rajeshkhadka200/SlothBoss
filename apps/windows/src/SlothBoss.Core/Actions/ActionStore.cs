using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SlothBoss.Core.Documents;
using SlothBoss.Core.Storage;

namespace SlothBoss.Core.Actions;

public static class ActionStatus
{
    public const string Proposed = "proposed", Rejected = "rejected", Applied = "applied", Failed = "failed", Undone = "undone";
}

public sealed record ActionRecord(
    string Id, string BatchId, string ActionType, string PayloadJson, string Summary, string Status,
    string? ResultJson, string? UndoJson, string? Error, string? Model, string CreatedAt, string? DecidedAt, string? UndoneAt)
{
    public ActionPayload Payload => ActionPayload.Parse(PayloadJson);
    public bool IsPending => Status == ActionStatus.Proposed;
    public bool CanUndo => Status == ActionStatus.Applied;

    /// <summary>Id of the record created by an applied create_task/create_page action.</summary>
    public string? CreatedRecordId =>
        ResultJson is null ? null : (JsonNode.Parse(ResultJson)?["task_id"] ?? JsonNode.Parse(ResultJson)?["page_id"])?.GetValue<string>();
}

/// <summary>
/// The only path by which AI output can change the workspace. Proposals are stored first;
/// nothing is mutated until <see cref="Approve"/> applies the validated payload in a transaction.
/// </summary>
public sealed class ActionStore(Workspace ws)
{
    private const string Columns =
        "id, batch_id, action_type, payload_json, summary, status, result_json, undo_json, error, model, created_at, decided_at, undone_at";

    private static ActionRecord Map(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
            r.NullableString(6), r.NullableString(7), r.NullableString(8), r.NullableString(9), r.GetString(10),
            r.NullableString(11), r.NullableString(12));

    public ActionRecord Propose(string batchId, ActionPayload payload, string summary, string? model)
    {
        payload.Validate();
        var json = payload.ToJson();
        ActionPayload.Parse(json); // the stored form must round-trip through the strict parser
        var id = Ids.New();
        ws.Write(WorkspaceChange.Actions, (c, tx) => c.Exec($"""
            INSERT INTO ai_actions (id, batch_id, action_type, payload_json, summary, status, model, created_at)
            VALUES ($id, $batch, $type, $payload, $summary, 'proposed', $model, $now)
            """, tx,
            ("$id", id), ("$batch", batchId), ("$type", payload.Type), ("$payload", json), ("$summary", summary),
            ("$model", model), ("$now", Clock.Stamp())));
        return Get(id)!;
    }

    public ActionRecord? Get(string id) =>
        ws.Read(c => c.Query($"SELECT {Columns} FROM ai_actions WHERE id = $id", Map, null, ("$id", id)).FirstOrDefault());

    public List<ActionRecord> History(int limit = 200) =>
        ws.Read(c => c.Query($"SELECT {Columns} FROM ai_actions ORDER BY created_at DESC, rowid DESC LIMIT $n", Map, null, ("$n", limit)));

    public List<ActionRecord> Batch(string batchId) =>
        ws.Read(c => c.Query($"SELECT {Columns} FROM ai_actions WHERE batch_id = $b ORDER BY rowid", Map, null, ("$b", batchId)));

    public ActionRecord Reject(string id)
    {
        ws.Write(WorkspaceChange.Actions, (c, tx) =>
        {
            if (c.Exec("UPDATE ai_actions SET status = 'rejected', decided_at = $now WHERE id = $id AND status = 'proposed'", tx,
                    ("$now", Clock.Stamp()), ("$id", id)) == 0)
                throw new SlothBossException("This proposal has already been decided.");
        });
        return Get(id)!;
    }

    /// <summary>
    /// Re-validates and applies a proposal in one transaction. If anything fails, the workspace is
    /// left untouched and the proposal is marked failed with the reason.
    /// </summary>
    public ActionRecord Approve(string id)
    {
        var record = Get(id) ?? throw new SlothBossException("The proposal no longer exists.");
        if (!record.IsPending) throw new SlothBossException("This proposal has already been decided.");
        try
        {
            ws.Write(WorkspaceChange.All, (c, tx) =>
            {
                var payload = ActionPayload.Parse(record.PayloadJson);
                var (result, undo) = Apply(c, tx, payload);
                c.Exec("UPDATE ai_actions SET status = 'applied', decided_at = $now, result_json = $r, undo_json = $u, error = NULL WHERE id = $id", tx,
                    ("$now", Clock.Stamp()), ("$r", result.ToJsonString()), ("$u", undo?.ToJsonString()), ("$id", id));
            });
        }
        catch (Exception e) when (e is SlothBossException or SqliteException)
        {
            ws.Write(WorkspaceChange.Actions, (c, tx) =>
                c.Exec("UPDATE ai_actions SET status = 'failed', decided_at = $now, error = $e WHERE id = $id", tx,
                    ("$now", Clock.Stamp()), ("$e", e.Message), ("$id", id)));
        }
        return Get(id)!;
    }

    private (JsonObject Result, JsonObject? Undo) Apply(SqliteConnection c, SqliteTransaction tx, ActionPayload payload)
    {
        switch (payload)
        {
            case CreateTaskPayload p:
            {
                var taskId = Ids.New();
                ws.Tasks.Insert(c, tx, taskId, p.ToDraft());
                return (new JsonObject { ["task_id"] = taskId, ["updated_at"] = UpdatedAt(c, tx, "tasks", taskId) }, null);
            }
            case CreatePagePayload p:
            {
                var pageId = Ids.New();
                ws.Pages.Insert(c, tx, pageId, p.Title, p.ParentId, p.Content);
                return (new JsonObject { ["page_id"] = pageId, ["updated_at"] = UpdatedAt(c, tx, "pages", pageId) }, null);
            }
            case DraftPageContentPayload p:
            {
                var previous = ContentJson(c, tx, p.PageId);
                var previousUpdated = UpdatedAt(c, tx, "pages", p.PageId);
                var doc = PageDocument.Parse(previous);
                doc.Blocks.AddRange(p.Blocks.Select(CloneBlock));
                var updated = ws.Pages.UpdateContent(c, tx, p.PageId, doc);
                return (new JsonObject { ["page_id"] = p.PageId, ["updated_at"] = updated },
                        new JsonObject { ["previous_content_json"] = previous, ["previous_updated_at"] = previousUpdated });
            }
            case AddPageReferencePayload p:
            {
                var previous = ContentJson(c, tx, p.PageId);
                var previousUpdated = UpdatedAt(c, tx, "pages", p.PageId);
                var targetTitle = c.Scalar("SELECT title FROM pages WHERE id = $id", tx, ("$id", p.TargetPageId)) as string
                    ?? throw new SlothBossException("The referenced page no longer exists.");
                var doc = PageDocument.Parse(previous);
                if (doc.PageReferences().Contains(p.TargetPageId))
                    throw new SlothBossException("The page already links to that page.");
                doc.Blocks.Add(new Block(BlockType.Paragraph,
                    new Span("Related: "),
                    new Span(string.IsNullOrWhiteSpace(targetTitle) ? "Untitled" : targetTitle) { Link = Links.ForPage(p.TargetPageId) }));
                var updated = ws.Pages.UpdateContent(c, tx, p.PageId, doc);
                return (new JsonObject { ["page_id"] = p.PageId, ["updated_at"] = updated },
                        new JsonObject { ["previous_content_json"] = previous, ["previous_updated_at"] = previousUpdated });
            }
            case UpdateTaskPayload p:
            {
                var task = TaskStore.Get(c, tx, p.TaskId) ?? throw new SlothBossException("The task no longer exists.");
                var draft = TaskStore.ToDraft(task);
                var changed = draft with
                {
                    Status = p.Status ?? draft.Status,
                    Priority = p.Priority ?? draft.Priority,
                    DueDate = p.ChangesDueDate ? p.DueDate : draft.DueDate,
                };
                var updated = ws.Tasks.Update(c, tx, p.TaskId, changed);
                return (new JsonObject { ["task_id"] = p.TaskId, ["updated_at"] = updated },
                        new JsonObject
                        {
                            ["status"] = task.Status, ["priority"] = task.Priority, ["due_date"] = task.DueDate,
                            ["previous_updated_at"] = task.UpdatedAt,
                        });
            }
            default:
                throw new ActionValidationException("Unsupported action.");
        }
    }

    private static Block CloneBlock(Block b) =>
        new() { Type = b.Type, Level = b.Level, Checked = b.Checked, Spans = b.Spans.Select(s => s.Clone()).ToList() };

    private static string UpdatedAt(SqliteConnection c, SqliteTransaction tx, string table, string id) =>
        (string)c.Scalar($"SELECT updated_at FROM {table} WHERE id = $id", tx, ("$id", id))!;

    private static string ContentJson(SqliteConnection c, SqliteTransaction tx, string pageId) =>
        c.Scalar("SELECT content_json FROM pages WHERE id = $id", tx, ("$id", pageId)) as string
        ?? throw new SlothBossException("The target page no longer exists.");

    /// <summary>
    /// Reverses an applied action, but only if the affected record is exactly as the action left it.
    /// Never overwrites edits the user made afterwards; throws with an explanation instead.
    /// </summary>
    public ActionRecord Undo(string id)
    {
        var record = Get(id) ?? throw new SlothBossException("The action no longer exists.");
        if (!record.CanUndo) throw new SlothBossException("Only applied actions can be undone.");
        var result = JsonNode.Parse(record.ResultJson!)!.AsObject();
        var undo = record.UndoJson is null ? null : JsonNode.Parse(record.UndoJson)!.AsObject();
        var expected = result["updated_at"]!.GetValue<string>();

        ws.Write(WorkspaceChange.All, (c, tx) =>
        {
            switch (record.ActionType)
            {
                case CreateTaskPayload.TypeName:
                {
                    var taskId = result["task_id"]!.GetValue<string>();
                    RequireUnchanged(c, tx, "tasks", taskId, expected, "task");
                    c.Exec("DELETE FROM tasks WHERE id = $id", tx, ("$id", taskId));
                    break;
                }
                case CreatePagePayload.TypeName:
                {
                    var pageId = result["page_id"]!.GetValue<string>();
                    RequireUnchanged(c, tx, "pages", pageId, expected, "page");
                    if (c.Scalar("SELECT 1 FROM pages WHERE parent_id = $id", tx, ("$id", pageId)) is not null)
                        throw new SlothBossException("Cannot undo: the page now has subpages.");
                    if (c.Scalar("SELECT 1 FROM tasks WHERE page_id = $id", tx, ("$id", pageId)) is not null)
                        throw new SlothBossException("Cannot undo: tasks are now linked to the page.");
                    c.Exec("DELETE FROM pages WHERE id = $id", tx, ("$id", pageId));
                    break;
                }
                case DraftPageContentPayload.TypeName:
                case AddPageReferencePayload.TypeName:
                {
                    var pageId = result["page_id"]!.GetValue<string>();
                    RequireUnchanged(c, tx, "pages", pageId, expected, "page");
                    var restoredAt = ws.Pages.UpdateContent(c, tx, pageId, PageDocument.Parse(undo!["previous_content_json"]!.GetValue<string>()));
                    Rebase(c, tx, id, pageId, undo["previous_updated_at"]?.GetValue<string>(), restoredAt);
                    break;
                }
                case UpdateTaskPayload.TypeName:
                {
                    var taskId = result["task_id"]!.GetValue<string>();
                    RequireUnchanged(c, tx, "tasks", taskId, expected, "task");
                    var task = TaskStore.Get(c, tx, taskId)!;
                    var restoredAt = ws.Tasks.Update(c, tx, taskId, TaskStore.ToDraft(task) with
                    {
                        Status = undo!["status"]!.GetValue<string>(),
                        Priority = undo["priority"]!.GetValue<string>(),
                        DueDate = undo["due_date"]?.GetValue<string>(),
                    });
                    Rebase(c, tx, id, taskId, undo["previous_updated_at"]?.GetValue<string>(), restoredAt);
                    break;
                }
                default:
                    throw new SlothBossException("This action type cannot be undone.");
            }
            c.Exec("UPDATE ai_actions SET status = 'undone', undone_at = $now WHERE id = $id", tx, ("$now", Clock.Stamp()), ("$id", id));
        });
        return Get(id)!;
    }

    /// <summary>
    /// An undo that restores a record to exactly the state an earlier applied action left it in
    /// (same previous updated_at) re-bases that action's expectation, so actions can be undone in
    /// reverse order. Records edited by the user in between never match and stay protected.
    /// </summary>
    private static void Rebase(SqliteConnection c, SqliteTransaction tx, string undoneId, string recordId, string? previousUpdatedAt, string restoredAt)
    {
        if (previousUpdatedAt is null) return;
        c.Exec("""
            UPDATE ai_actions SET result_json = json_set(result_json, '$.updated_at', $restored)
            WHERE status = 'applied' AND id <> $undone
              AND coalesce(json_extract(result_json, '$.page_id'), json_extract(result_json, '$.task_id')) = $record
              AND json_extract(result_json, '$.updated_at') = $previous
            """, tx, ("$restored", restoredAt), ("$undone", undoneId), ("$record", recordId), ("$previous", previousUpdatedAt));
    }

    private static void RequireUnchanged(SqliteConnection c, SqliteTransaction tx, string table, string id, string expected, string what)
    {
        var actual = c.Scalar($"SELECT updated_at FROM {table} WHERE id = $id", tx, ("$id", id)) as string
            ?? throw new SlothBossException($"Cannot undo: the {what} has already been deleted.");
        if (actual != expected)
            throw new SlothBossException($"Cannot undo: the {what} was edited after the action was applied, and undoing would discard those edits.");
    }
}
