using Microsoft.Data.Sqlite;
using SlothBoss.Core.Documents;

namespace SlothBoss.Core.Storage;

public sealed record PageSummary(
    string Id, string? ParentId, string Title, double SortOrder,
    string CreatedAt, string UpdatedAt, bool IsFavorite, bool IsArchived)
{
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "Untitled" : Title;
}

public sealed record Page(
    string Id, string? ParentId, string Title, string ContentJson, string ContentText, double SortOrder,
    string CreatedAt, string UpdatedAt, bool IsFavorite, bool IsArchived)
{
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "Untitled" : Title;
    public PageDocument Document => PageDocument.Parse(ContentJson);
    public PageSummary Summary => new(Id, ParentId, Title, SortOrder, CreatedAt, UpdatedAt, IsFavorite, IsArchived);
}

/// <summary>
/// Pages form a forest (parent_id NULL = top level). Deleting a page deletes its whole subtree
/// and unlinks (does not delete) tasks attached to any page in it. Archiving applies to the subtree.
/// </summary>
public sealed class PageStore(Workspace ws)
{
    public const int MaxTitleLength = 300;

    private const string SummaryColumns = "id, parent_id, title, sort_order, created_at, updated_at, is_favorite, is_archived";
    private const string FullColumns = "id, parent_id, title, content_json, content_text, sort_order, created_at, updated_at, is_favorite, is_archived";

    private static PageSummary MapSummary(SqliteDataReader r) =>
        new(r.GetString(0), r.NullableString(1), r.GetString(2), r.GetDouble(3), r.GetString(4), r.GetString(5), r.GetInt64(6) == 1, r.GetInt64(7) == 1);

    private static Page MapPage(SqliteDataReader r) =>
        new(r.GetString(0), r.NullableString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetDouble(5),
            r.GetString(6), r.GetString(7), r.GetInt64(8) == 1, r.GetInt64(9) == 1);

    private static string CleanTitle(string title)
    {
        var t = title.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return t.Length > MaxTitleLength ? t[..MaxTitleLength] : t;
    }

    public Page Create(string title, string? parentId = null, PageDocument? content = null)
    {
        var id = Ids.New();
        ws.Write(WorkspaceChange.Pages, (c, tx) => Insert(c, tx, id, title, parentId, content));
        return Get(id)!;
    }

    internal void Insert(SqliteConnection c, SqliteTransaction tx, string id, string title, string? parentId, PageDocument? content)
    {
        if (parentId is not null && c.Scalar("SELECT 1 FROM pages WHERE id = $p", tx, ("$p", parentId)) is null)
            throw new SlothBossException("The parent page no longer exists.");
        var doc = (content ?? new PageDocument()).Normalize();
        var now = Clock.Stamp();
        var order = Convert.ToDouble(c.Scalar(
            "SELECT coalesce(max(sort_order), 0) + 1 FROM pages WHERE parent_id IS $p", tx, ("$p", parentId)));
        c.Exec("""
            INSERT INTO pages (id, parent_id, title, content_json, content_text, sort_order, created_at, updated_at)
            VALUES ($id, $parent, $title, $json, $text, $order, $now, $now)
            """, tx,
            ("$id", id), ("$parent", parentId), ("$title", CleanTitle(title)), ("$json", doc.ToJson()),
            ("$text", doc.ToPlainText()), ("$order", order), ("$now", now));
        SyncLinks(c, tx, id, doc);
    }

    public Page? Get(string id) =>
        ws.Read(c => c.Query($"SELECT {FullColumns} FROM pages WHERE id = $id", MapPage, null, ("$id", id)).FirstOrDefault());

    public bool Exists(string id) => ws.Read(c => c.Scalar("SELECT 1 FROM pages WHERE id = $id", null, ("$id", id)) is not null);

    public List<PageSummary> List(bool includeArchived = false) =>
        ws.Read(c => c.Query(
            $"SELECT {SummaryColumns} FROM pages {(includeArchived ? "" : "WHERE is_archived = 0")} ORDER BY sort_order, created_at",
            MapSummary));

    public List<PageSummary> Archived() =>
        ws.Read(c => c.Query(
            // Only the roots of archived subtrees: archived pages whose parent is not archived.
            $"""
            SELECT {SummaryColumns} FROM pages p WHERE is_archived = 1
              AND (parent_id IS NULL OR NOT EXISTS (SELECT 1 FROM pages q WHERE q.id = p.parent_id AND q.is_archived = 1))
            ORDER BY updated_at DESC
            """, MapSummary));

    public List<PageSummary> Favorites() =>
        ws.Read(c => c.Query($"SELECT {SummaryColumns} FROM pages WHERE is_favorite = 1 AND is_archived = 0 ORDER BY title", MapSummary));

    public List<PageSummary> Recent(int limit = 8) =>
        ws.Read(c => c.Query($"SELECT {SummaryColumns} FROM pages WHERE is_archived = 0 ORDER BY updated_at DESC LIMIT $n", MapSummary, null, ("$n", limit)));

    public List<PageSummary> Children(string id) =>
        ws.Read(c => c.Query($"SELECT {SummaryColumns} FROM pages WHERE parent_id = $id AND is_archived = 0 ORDER BY sort_order", MapSummary, null, ("$id", id)));

    /// <summary>Ancestors from the top-level page down to (and including) the page itself.</summary>
    public List<PageSummary> Breadcrumb(string id) =>
        ws.Read(c => c.Query($"""
            WITH RECURSIVE chain(id, depth) AS (
                SELECT $id, 0
                UNION ALL
                SELECT p.parent_id, chain.depth + 1 FROM pages p JOIN chain ON p.id = chain.id
                WHERE p.parent_id IS NOT NULL AND chain.depth < 1000
            )
            SELECT {string.Join(", ", SummaryColumns.Split(", ").Select(col => "p." + col))}
            FROM chain JOIN pages p ON p.id = chain.id ORDER BY chain.depth DESC
            """, MapSummary, null, ("$id", id)));

    /// <summary>Saves new content. Returns the new updated_at.</summary>
    public string UpdateContent(string id, PageDocument doc) =>
        ws.Write(WorkspaceChange.Pages, (c, tx) => UpdateContent(c, tx, id, doc));

    internal string UpdateContent(SqliteConnection c, SqliteTransaction tx, string id, PageDocument doc)
    {
        doc.Normalize();
        var now = Clock.Stamp();
        var n = c.Exec("UPDATE pages SET content_json = $json, content_text = $text, updated_at = $now WHERE id = $id", tx,
            ("$json", doc.ToJson()), ("$text", doc.ToPlainText()), ("$now", now), ("$id", id));
        if (n == 0) throw new SlothBossException("The page no longer exists.");
        SyncLinks(c, tx, id, doc);
        return now;
    }

    private static void SyncLinks(SqliteConnection c, SqliteTransaction tx, string id, PageDocument doc)
    {
        c.Exec("DELETE FROM page_links WHERE source_page_id = $id", tx, ("$id", id));
        foreach (var target in doc.PageReferences())
        {
            if (target == id) continue;
            // Links to pages that don't exist (e.g. deleted) are kept in content but not indexed.
            c.Exec("INSERT OR IGNORE INTO page_links (source_page_id, target_page_id) SELECT $s, id FROM pages WHERE id = $t", tx,
                ("$s", id), ("$t", target));
        }
    }

    public void Rename(string id, string title) =>
        ws.Write(WorkspaceChange.Pages, (c, tx) =>
        {
            if (c.Exec("UPDATE pages SET title = $t, updated_at = $now WHERE id = $id", tx,
                    ("$t", CleanTitle(title)), ("$now", Clock.Stamp()), ("$id", id)) == 0)
                throw new SlothBossException("The page no longer exists.");
        });

    public void SetFavorite(string id, bool favorite) =>
        ws.Write(WorkspaceChange.Pages, (c, tx) =>
            c.Exec("UPDATE pages SET is_favorite = $f WHERE id = $id", tx, ("$f", favorite ? 1 : 0), ("$id", id)));

    private const string SubtreeCte = """
        WITH RECURSIVE subtree(id) AS (
            SELECT $id
            UNION
            SELECT p.id FROM pages p JOIN subtree s ON p.parent_id = s.id
        )
        """;

    public void Archive(string id) =>
        ws.Write(WorkspaceChange.Pages | WorkspaceChange.Tasks, (c, tx) =>
            c.Exec(SubtreeCte + "UPDATE pages SET is_archived = 1, updated_at = $now WHERE id IN (SELECT id FROM subtree)", tx,
                ("$id", id), ("$now", Clock.Stamp())));

    /// <summary>Restores the subtree. If the page's parent is still archived, the page moves to the top level.</summary>
    public void Restore(string id) =>
        ws.Write(WorkspaceChange.Pages, (c, tx) =>
        {
            c.Exec(SubtreeCte + "UPDATE pages SET is_archived = 0, updated_at = $now WHERE id IN (SELECT id FROM subtree)", tx,
                ("$id", id), ("$now", Clock.Stamp()));
            c.Exec("""
                UPDATE pages SET parent_id = NULL
                WHERE id = $id AND parent_id IN (SELECT id FROM pages WHERE is_archived = 1)
                """, tx, ("$id", id));
        });

    public (int Pages, int LinkedTasks) DeletionImpact(string id) =>
        ws.Read(c =>
        {
            var pages = Convert.ToInt32(c.Scalar(SubtreeCte + "SELECT count(*) FROM subtree JOIN pages USING (id)", null, ("$id", id)));
            var tasks = Convert.ToInt32(c.Scalar(SubtreeCte + "SELECT count(*) FROM tasks WHERE page_id IN (SELECT id FROM subtree)", null, ("$id", id)));
            return (pages, tasks);
        });

    /// <summary>Deletes the page and all descendants. Linked tasks are kept and unlinked (FK ON DELETE SET NULL).</summary>
    public void Delete(string id) =>
        ws.Write(WorkspaceChange.Pages | WorkspaceChange.Tasks, (c, tx) =>
        {
            if (c.Exec("DELETE FROM pages WHERE id = $id", tx, ("$id", id)) == 0)
                throw new SlothBossException("The page no longer exists.");
        });

    /// <summary>Moves a page under a new parent (null = top level). Rejects cycles.</summary>
    public void Move(string id, string? newParentId) =>
        ws.Write(WorkspaceChange.Pages, (c, tx) =>
        {
            if (newParentId == id) throw new SlothBossException("A page cannot be its own parent.");
            try
            {
                var order = Convert.ToDouble(c.Scalar("SELECT coalesce(max(sort_order), 0) + 1 FROM pages WHERE parent_id IS $p", tx, ("$p", newParentId)));
                if (c.Exec("UPDATE pages SET parent_id = $p, sort_order = $o, updated_at = $now WHERE id = $id", tx,
                        ("$p", newParentId), ("$o", order), ("$now", Clock.Stamp()), ("$id", id)) == 0)
                    throw new SlothBossException("The page no longer exists.");
            }
            catch (SqliteException e) when (e.Message.Contains("page hierarchy cycle"))
            {
                throw new SlothBossException("A page cannot be moved inside one of its own subpages.", e);
            }
        });

    public List<PageSummary> Backlinks(string id) =>
        ws.Read(c => c.Query($"""
            SELECT {string.Join(", ", SummaryColumns.Split(", ").Select(col => "p." + col))}
            FROM page_links l JOIN pages p ON p.id = l.source_page_id
            WHERE l.target_page_id = $id AND p.is_archived = 0 ORDER BY p.title
            """, MapSummary, null, ("$id", id)));

    public List<PageSummary> OutgoingLinks(string id) =>
        ws.Read(c => c.Query($"""
            SELECT {string.Join(", ", SummaryColumns.Split(", ").Select(col => "p." + col))}
            FROM page_links l JOIN pages p ON p.id = l.target_page_id
            WHERE l.source_page_id = $id ORDER BY p.title
            """, MapSummary, null, ("$id", id)));
}
