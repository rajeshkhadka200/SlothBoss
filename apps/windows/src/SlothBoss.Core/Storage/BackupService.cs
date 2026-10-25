using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SlothBoss.Core.Documents;

namespace SlothBoss.Core.Storage;

public sealed record BackupPage(string Id, string? ParentId, string Title, PageDocument Content, double SortOrder,
    string CreatedAt, string UpdatedAt, bool IsFavorite, bool IsArchived);

public sealed record BackupTask(string Id, string Title, string? Description, string Status, string Priority, string? DueDate,
    string? PageId, string CreatedAt, string UpdatedAt, string? CompletedAt);

public sealed record ValidatedBackup(IReadOnlyList<BackupPage> Pages, IReadOnlyList<BackupTask> Tasks, IReadOnlyList<string> Warnings, string? Generator, string ExportedAt);

public sealed class BackupValidationException(IReadOnlyList<string> errors)
    : SlothBossException("The backup is not valid:\n• " + string.Join("\n• ", errors.Take(10)) + (errors.Count > 10 ? $"\n• …and {errors.Count - 10} more" : ""))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Versioned JSON backups (contracts/schemas/backup.schema.json). Restores are all-or-nothing:
/// the file is fully validated before anything is written, and the write is one transaction.
/// </summary>
public static class BackupService
{
    public const string Format = "slothboss-backup";
    public const int FormatVersion = 1;
    public const long MaxBackupBytes = 512L * 1024 * 1024;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Export(Workspace ws, string generator)
    {
        var (pages, tasks) = ws.Read(c =>
        {
            var p = c.Query("""
                SELECT id, parent_id, title, content_json, sort_order, created_at, updated_at, is_favorite, is_archived
                FROM pages ORDER BY created_at, id
                """, r => new JsonObject
            {
                ["id"] = r.GetString(0), ["parentId"] = r.NullableString(1), ["title"] = r.GetString(2),
                ["content"] = JsonNode.Parse(r.GetString(3)), ["sortOrder"] = r.GetDouble(4),
                ["createdAt"] = r.GetString(5), ["updatedAt"] = r.GetString(6),
                ["isFavorite"] = r.GetInt64(7) == 1, ["isArchived"] = r.GetInt64(8) == 1,
            });
            var t = c.Query("""
                SELECT id, title, description, status, priority, due_date, page_id, created_at, updated_at, completed_at
                FROM tasks ORDER BY created_at, id
                """, r => new JsonObject
            {
                ["id"] = r.GetString(0), ["title"] = r.GetString(1), ["description"] = r.NullableString(2),
                ["status"] = r.GetString(3), ["priority"] = r.GetString(4), ["dueDate"] = r.NullableString(5),
                ["pageId"] = r.NullableString(6), ["createdAt"] = r.GetString(7), ["updatedAt"] = r.GetString(8),
                ["completedAt"] = r.NullableString(9),
            });
            return (p, t);
        });
        var root = new JsonObject
        {
            ["format"] = Format,
            ["formatVersion"] = FormatVersion,
            ["schemaVersion"] = Workspace.SchemaVersion,
            ["exportedAt"] = Clock.Stamp(),
            ["generator"] = generator,
            ["pages"] = new JsonArray([.. pages]),
            ["tasks"] = new JsonArray([.. tasks]),
        };
        return root.ToJsonString(WriteOptions);
    }

    /// <summary>Writes atomically: temp file in the same folder, then replace.</summary>
    public static void ExportToFile(Workspace ws, string path, string generator)
    {
        var json = Export(ws, generator);
        var full = Path.GetFullPath(path);
        var temp = full + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        File.Move(temp, full, overwrite: true);
    }

    public static ValidatedBackup ValidateFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new SlothBossException("The backup file does not exist.");
        if (info.Length > MaxBackupBytes) throw new SlothBossException("The backup file is too large to import.");
        return Validate(File.ReadAllText(path));
    }

    public static ValidatedBackup Validate(string json)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException e) { throw new BackupValidationException([$"Not valid JSON: {e.Message}"]); }
        if (node is not JsonObject root) throw new BackupValidationException(["The file is not a SlothBoss backup."]);

        if (Get<string>(root, "format") != Format) throw new BackupValidationException(["The file is not a SlothBoss backup (missing format marker)."]);
        var formatVersion = Get<int?>(root, "formatVersion");
        if (formatVersion != FormatVersion)
            throw new BackupValidationException([$"Unsupported backup format version {formatVersion?.ToString() ?? "(missing)"}; this app reads version {FormatVersion}."]);
        var schemaVersion = Get<int?>(root, "schemaVersion");
        if (schemaVersion is null or < 1 || schemaVersion > Workspace.SchemaVersion)
            throw new BackupValidationException([$"The backup was made with schema version {schemaVersion?.ToString() ?? "(missing)"}, which this app does not support."]);

        var errors = new List<string>();
        var warnings = new List<string>();
        var pages = new List<BackupPage>();
        var tasks = new List<BackupTask>();

        if (root["pages"] is not JsonArray pageArr) { errors.Add("Missing 'pages' list."); pageArr = []; }
        if (root["tasks"] is not JsonArray taskArr) { errors.Add("Missing 'tasks' list."); taskArr = []; }

        var pageIds = new HashSet<string>();
        for (var i = 0; i < pageArr.Count; i++)
        {
            var where = $"pages[{i}]";
            if (pageArr[i] is not JsonObject p) { errors.Add($"{where} is not an object."); continue; }
            try
            {
                var id = Get<string>(p, "id");
                if (!Ids.IsValid(id)) { errors.Add($"{where} has an invalid id."); continue; }
                if (!pageIds.Add(id!)) { errors.Add($"{where} duplicates page id {id}."); continue; }
                var parent = Get<string>(p, "parentId");
                if (parent is not null && !Ids.IsValid(parent)) errors.Add($"{where} has an invalid parentId.");
                PageDocument content;
                try { content = PageDocument.FromJsonNode(p["content"]); }
                catch (DocumentFormatException e) { errors.Add($"{where} content: {e.Message}"); continue; }
                var created = Get<string>(p, "createdAt");
                var updated = Get<string>(p, "updatedAt");
                if (!Clock.IsValidTimestamp(created) || !Clock.IsValidTimestamp(updated)) errors.Add($"{where} has invalid timestamps.");
                pages.Add(new BackupPage(id!, parent, Get<string>(p, "title") ?? "", content, Get<double?>(p, "sortOrder") ?? i,
                    created ?? "", updated ?? "", Get<bool?>(p, "isFavorite") ?? false, Get<bool?>(p, "isArchived") ?? false));
            }
            catch (InvalidOperationException e) { errors.Add($"{where}: {e.Message}"); }
        }

        foreach (var p in pages)
            if (p.ParentId is not null && !pageIds.Contains(p.ParentId)) errors.Add($"Page {p.Id} refers to a missing parent {p.ParentId}.");
        var parentOf = pages.ToDictionary(p => p.Id, p => p.ParentId);
        foreach (var p in pages)
        {
            var seen = new HashSet<string> { p.Id };
            for (var cur = p.ParentId; cur is not null && parentOf.TryGetValue(cur, out var next); cur = next)
                if (!seen.Add(cur)) { errors.Add($"Page {p.Id} is part of a parent cycle."); break; }
        }

        var taskIds = new HashSet<string>();
        for (var i = 0; i < taskArr.Count; i++)
        {
            var where = $"tasks[{i}]";
            if (taskArr[i] is not JsonObject t) { errors.Add($"{where} is not an object."); continue; }
            try
            {
                var id = Get<string>(t, "id");
                if (!Ids.IsValid(id)) { errors.Add($"{where} has an invalid id."); continue; }
                if (!taskIds.Add(id!)) { errors.Add($"{where} duplicates task id {id}."); continue; }
                var draft = new TaskDraft(Get<string>(t, "title") ?? "", Get<string>(t, "description"), Get<string>(t, "status") ?? "",
                    Get<string>(t, "priority") ?? "", Get<string>(t, "dueDate"), Get<string>(t, "pageId"));
                try { draft = TaskStore.Validate(draft); }
                catch (SlothBossException e) { errors.Add($"{where}: {e.Message}"); continue; }
                if (draft.PageId is not null && !pageIds.Contains(draft.PageId)) { errors.Add($"{where} refers to a missing page {draft.PageId}."); continue; }
                var created = Get<string>(t, "createdAt");
                var updated = Get<string>(t, "updatedAt");
                if (!Clock.IsValidTimestamp(created) || !Clock.IsValidTimestamp(updated)) { errors.Add($"{where} has invalid timestamps."); continue; }
                var completed = Get<string>(t, "completedAt");
                if (draft.Status == TaskState.Done && completed is null) { completed = updated; warnings.Add($"Task “{draft.Title}” was done without a completion time; used its last update time."); }
                if (draft.Status != TaskState.Done && completed is not null) { completed = null; warnings.Add($"Task “{draft.Title}” is not done but had a completion time; cleared it."); }
                tasks.Add(new BackupTask(id!, draft.Title, draft.Description, draft.Status, draft.Priority, draft.DueDate, draft.PageId,
                    created!, updated!, completed));
            }
            catch (InvalidOperationException e) { errors.Add($"{where}: {e.Message}"); }
        }

        if (errors.Count > 0) throw new BackupValidationException(errors);
        return new ValidatedBackup(pages, tasks, warnings, Get<string>(root, "generator"), Get<string>(root, "exportedAt") ?? "");
    }

    private static T? Get<T>(JsonObject o, string key)
    {
        var node = o[key];
        if (node is null) return default;
        try { return node.GetValue<T>(); }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            throw new InvalidOperationException($"'{key}' has the wrong type.");
        }
    }

    /// <summary>
    /// Replaces all pages, tasks, links and AI history in <paramref name="ws"/> with the backup's contents,
    /// in a single transaction. Callers must confirm with the user and snapshot the workspace first.
    /// Settings are kept.
    /// </summary>
    public static void RestoreReplacing(Workspace ws, ValidatedBackup backup)
    {
        ws.Write(WorkspaceChange.All, (c, tx) =>
        {
            c.Exec("DELETE FROM ai_actions; DELETE FROM tasks; DELETE FROM page_links; DELETE FROM pages;", tx);

            // Parents before children.
            var byParent = backup.Pages.ToLookup(p => p.ParentId);
            var ordered = new List<BackupPage>();
            var queue = new Queue<BackupPage>(byParent[null]);
            while (queue.Count > 0)
            {
                var p = queue.Dequeue();
                ordered.Add(p);
                foreach (var child in byParent[p.Id]) queue.Enqueue(child);
            }
            foreach (var p in ordered)
            {
                c.Exec("""
                    INSERT INTO pages (id, parent_id, title, content_json, content_text, sort_order, created_at, updated_at, is_favorite, is_archived)
                    VALUES ($id, $parent, $title, $json, $text, $order, $created, $updated, $fav, $arch)
                    """, tx,
                    ("$id", p.Id), ("$parent", p.ParentId), ("$title", p.Title), ("$json", p.Content.ToJson()), ("$text", p.Content.ToPlainText()),
                    ("$order", p.SortOrder), ("$created", p.CreatedAt), ("$updated", p.UpdatedAt), ("$fav", p.IsFavorite ? 1 : 0), ("$arch", p.IsArchived ? 1 : 0));
            }
            foreach (var p in ordered)
                foreach (var target in p.Content.PageReferences())
                    c.Exec("INSERT OR IGNORE INTO page_links (source_page_id, target_page_id) SELECT $s, id FROM pages WHERE id = $t AND id <> $s", tx,
                        ("$s", p.Id), ("$t", target));
            foreach (var t in backup.Tasks)
            {
                c.Exec("""
                    INSERT INTO tasks (id, title, description, status, priority, due_date, page_id, created_at, updated_at, completed_at)
                    VALUES ($id, $title, $desc, $status, $priority, $due, $page, $created, $updated, $completed)
                    """, tx,
                    ("$id", t.Id), ("$title", t.Title), ("$desc", t.Description), ("$status", t.Status), ("$priority", t.Priority),
                    ("$due", t.DueDate), ("$page", t.PageId), ("$created", t.CreatedAt), ("$updated", t.UpdatedAt), ("$completed", t.CompletedAt));
            }
        });
    }

    /// <summary>Safe file name for exports: strips path separators and characters Windows/macOS reject.</summary>
    public static string SafeFileName(string title, string extension)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var name = new string((string.IsNullOrWhiteSpace(title) ? "Untitled" : title).Select(ch => invalid.Contains(ch) || char.IsControl(ch) ? '-' : ch).ToArray()).Trim(' ', '.');
        if (name.Length == 0) name = "Untitled";
        if (name.Length > 80) name = name[..80].Trim();
        return name + extension;
    }
}
