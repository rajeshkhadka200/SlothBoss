using Microsoft.Data.Sqlite;

namespace SlothBoss.Core.Storage;

public static class TaskState
{
    public const string Todo = "todo", InProgress = "in_progress", Done = "done";
    public static readonly string[] All = [Todo, InProgress, Done];
    public static bool IsValid(string? s) => s is not null && All.Contains(s);
    public static string Label(string s) => s switch { Todo => "To do", InProgress => "In progress", Done => "Done", _ => s };
}

public static class TaskPriority
{
    public const string Low = "low", Medium = "medium", High = "high", Urgent = "urgent";
    public static readonly string[] All = [Low, Medium, High, Urgent];
    public static bool IsValid(string? s) => s is not null && All.Contains(s);
    public static string Label(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

public sealed record TaskItem(
    string Id, string Title, string? Description, string Status, string Priority, string? DueDate, string? PageId,
    string CreatedAt, string UpdatedAt, string? CompletedAt)
{
    public bool IsDone => Status == TaskState.Done;
}

/// <summary>Fields a user (or an approved AI action) supplies when creating or editing a task.</summary>
public sealed record TaskDraft(
    string Title, string? Description = null, string Status = TaskState.Todo, string Priority = TaskPriority.Medium,
    string? DueDate = null, string? PageId = null);

public enum DueFilter { Any, Overdue, Today, Next7Days, NoDate }

public sealed record TaskFilter(string? Status = null, string? Priority = null, DueFilter Due = DueFilter.Any, string? PageId = null, bool HideDone = false);

public sealed class TaskStore(Workspace ws)
{
    public const int MaxTitleLength = 200, MaxDescriptionLength = 4000;

    private const string Columns = "id, title, description, status, priority, due_date, page_id, created_at, updated_at, completed_at";

    private static TaskItem Map(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.NullableString(2), r.GetString(3), r.GetString(4), r.NullableString(5),
            r.NullableString(6), r.GetString(7), r.GetString(8), r.NullableString(9));

    /// <summary>Validates and normalizes a draft. Throws <see cref="SlothBossException"/> with a user-readable message.</summary>
    public static TaskDraft Validate(TaskDraft d)
    {
        var title = (d.Title ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (title.Length == 0) throw new SlothBossException("A task needs a title.");
        if (title.Length > MaxTitleLength) throw new SlothBossException($"Task titles are limited to {MaxTitleLength} characters.");
        var desc = string.IsNullOrWhiteSpace(d.Description) ? null : d.Description.Trim();
        if (desc is { Length: > MaxDescriptionLength }) throw new SlothBossException($"Task descriptions are limited to {MaxDescriptionLength} characters.");
        if (!TaskState.IsValid(d.Status)) throw new SlothBossException($"Unknown task status '{d.Status}'.");
        if (!TaskPriority.IsValid(d.Priority)) throw new SlothBossException($"Unknown task priority '{d.Priority}'.");
        var due = string.IsNullOrWhiteSpace(d.DueDate) ? null : d.DueDate.Trim();
        if (due is not null && !Clock.IsValidDate(due)) throw new SlothBossException($"'{due}' is not a valid date (expected YYYY-MM-DD).");
        return d with { Title = title, Description = desc, DueDate = due };
    }

    public TaskItem Create(TaskDraft draft)
    {
        var id = Ids.New();
        ws.Write(WorkspaceChange.Tasks, (c, tx) => Insert(c, tx, id, draft));
        return Get(id)!;
    }

    internal void Insert(SqliteConnection c, SqliteTransaction tx, string id, TaskDraft draft)
    {
        var d = Validate(draft);
        if (d.PageId is not null && c.Scalar("SELECT 1 FROM pages WHERE id = $p", tx, ("$p", d.PageId)) is null)
            throw new SlothBossException("The linked page no longer exists.");
        var now = Clock.Stamp();
        c.Exec($"""
            INSERT INTO tasks ({Columns})
            VALUES ($id, $title, $desc, $status, $priority, $due, $page, $now, $now, $completed)
            """, tx,
            ("$id", id), ("$title", d.Title), ("$desc", d.Description), ("$status", d.Status), ("$priority", d.Priority),
            ("$due", d.DueDate), ("$page", d.PageId), ("$now", now), ("$completed", d.Status == TaskState.Done ? now : null));
    }

    public TaskItem? Get(string id) =>
        ws.Read(c => c.Query($"SELECT {Columns} FROM tasks WHERE id = $id", Map, null, ("$id", id)).FirstOrDefault());

    internal static TaskItem? Get(SqliteConnection c, SqliteTransaction tx, string id) =>
        c.Query($"SELECT {Columns} FROM tasks WHERE id = $id", Map, tx, ("$id", id)).FirstOrDefault();

    /// <summary>Replaces all editable fields. completed_at is set when a task becomes done and cleared when reopened.</summary>
    public TaskItem Update(string id, TaskDraft draft)
    {
        ws.Write(WorkspaceChange.Tasks, (c, tx) => Update(c, tx, id, draft));
        return Get(id)!;
    }

    internal string Update(SqliteConnection c, SqliteTransaction tx, string id, TaskDraft draft)
    {
        var d = Validate(draft);
        var existing = Get(c, tx, id) ?? throw new SlothBossException("The task no longer exists.");
        if (d.PageId is not null && c.Scalar("SELECT 1 FROM pages WHERE id = $p", tx, ("$p", d.PageId)) is null)
            throw new SlothBossException("The linked page no longer exists.");
        var now = Clock.Stamp();
        var completed = d.Status == TaskState.Done ? existing.CompletedAt ?? now : null;
        c.Exec("""
            UPDATE tasks SET title = $title, description = $desc, status = $status, priority = $priority,
                due_date = $due, page_id = $page, updated_at = $now, completed_at = $completed
            WHERE id = $id
            """, tx,
            ("$title", d.Title), ("$desc", d.Description), ("$status", d.Status), ("$priority", d.Priority),
            ("$due", d.DueDate), ("$page", d.PageId), ("$now", now), ("$completed", completed), ("$id", id));
        return now;
    }

    public TaskItem SetStatus(string id, string status)
    {
        var t = Get(id) ?? throw new SlothBossException("The task no longer exists.");
        return Update(id, ToDraft(t) with { Status = status });
    }

    public static TaskDraft ToDraft(TaskItem t) => new(t.Title, t.Description, t.Status, t.Priority, t.DueDate, t.PageId);

    public void Delete(string id) =>
        ws.Write(WorkspaceChange.Tasks, (c, tx) =>
        {
            if (c.Exec("DELETE FROM tasks WHERE id = $id", tx, ("$id", id)) == 0)
                throw new SlothBossException("The task no longer exists.");
        });

    public List<TaskItem> List(TaskFilter? filter = null)
    {
        var f = filter ?? new TaskFilter();
        var where = new List<string>();
        var args = new List<(string, object?)>();
        if (f.Status is not null) { where.Add("status = $status"); args.Add(("$status", f.Status)); }
        if (f.HideDone) where.Add("status <> 'done'");
        if (f.Priority is not null) { where.Add("priority = $priority"); args.Add(("$priority", f.Priority)); }
        if (f.PageId is not null) { where.Add("page_id = $page"); args.Add(("$page", f.PageId)); }
        var today = Clock.Today();
        switch (f.Due)
        {
            case DueFilter.Overdue: where.Add("due_date < $today AND status <> 'done'"); args.Add(("$today", today)); break;
            case DueFilter.Today: where.Add("due_date = $today"); args.Add(("$today", today)); break;
            case DueFilter.Next7Days:
                where.Add("due_date >= $today AND due_date <= $week");
                args.Add(("$today", today));
                args.Add(("$week", DateOnly.ParseExact(today, "yyyy-MM-dd").AddDays(7).ToString("yyyy-MM-dd")));
                break;
            case DueFilter.NoDate: where.Add("due_date IS NULL"); break;
        }
        var sql = $"""
            SELECT {Columns} FROM tasks
            {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")}
            ORDER BY CASE status WHEN 'in_progress' THEN 0 WHEN 'todo' THEN 1 ELSE 2 END,
                     due_date IS NULL, due_date,
                     CASE priority WHEN 'urgent' THEN 0 WHEN 'high' THEN 1 WHEN 'medium' THEN 2 ELSE 3 END,
                     created_at
            """;
        return ws.Read(c => c.Query(sql, Map, null, [.. args]));
    }

    public List<TaskItem> ForPage(string pageId) => List(new TaskFilter(PageId: pageId));
}

public sealed class SettingsStore(Workspace ws)
{
    public const string SelectedModel = "ai.model";
    public const string OllamaEndpoint = "ai.endpoint";
    public const string AiTimeoutSeconds = "ai.timeout_seconds";
    public const string Theme = "ui.theme";
    public const string EditorFontSize = "editor.font_size";

    public string? Get(string key) => ws.Read(c => c.Scalar("SELECT value FROM settings WHERE key = $k", null, ("$k", key)) as string);

    public string Get(string key, string fallback) => Get(key) ?? fallback;

    public void Set(string key, string? value) =>
        ws.Write(WorkspaceChange.Settings, (c, tx) =>
        {
            if (value is null) c.Exec("DELETE FROM settings WHERE key = $k", tx, ("$k", key));
            else c.Exec("INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", tx,
                ("$k", key), ("$v", value));
        });
}
