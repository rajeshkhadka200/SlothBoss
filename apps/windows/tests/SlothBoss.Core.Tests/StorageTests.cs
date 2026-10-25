using Microsoft.Data.Sqlite;
using SlothBoss.Core.Documents;
using SlothBoss.Core.Storage;

namespace SlothBoss.Core.Tests;

public class StorageTests
{
    [Fact]
    public void Migration_sets_schema_version_and_reopen_is_idempotent()
    {
        using var t = new TempWorkspace();
        var page = t.Ws.Pages.Create("Persist me", content: new PageDocument([Block.Paragraph("hello")]));
        Assert.Equal((long)Workspace.SchemaVersion, t.Ws.Read(c => (long)c.Scalar("PRAGMA user_version")!));
        t.Ws.Dispose();

        using var reopened = Workspace.Open(t.DbPath);
        var loaded = reopened.Pages.Get(page.Id)!;
        Assert.Equal("Persist me", loaded.Title);
        Assert.Equal("hello", loaded.Document.ToPlainText());
    }

    [Fact]
    public void Newer_schema_version_is_refused_without_modification()
    {
        using var t = new TempWorkspace();
        t.Ws.Read(c => c.Exec("PRAGMA user_version = 99"));
        t.Ws.Dispose();
        SqliteConnection.ClearAllPools();
        var before = File.ReadAllBytes(t.DbPath);
        var ex = Assert.Throws<SlothBossException>(() => Workspace.Open(t.DbPath));
        Assert.Contains("newer", ex.Message);
        Assert.Equal(before, File.ReadAllBytes(t.DbPath));
    }

    [Fact]
    public void Nested_pages_and_breadcrumbs()
    {
        using var t = new TempWorkspace();
        var root = t.Ws.Pages.Create("Root");
        var child = t.Ws.Pages.Create("Child", root.Id);
        var grandchild = t.Ws.Pages.Create("Grandchild", child.Id);
        Assert.Equal(["Root", "Child", "Grandchild"], t.Ws.Pages.Breadcrumb(grandchild.Id).Select(p => p.Title));
        Assert.Single(t.Ws.Pages.Children(root.Id));
    }

    [Fact]
    public void Cycles_are_prevented()
    {
        using var t = new TempWorkspace();
        var a = t.Ws.Pages.Create("A");
        var b = t.Ws.Pages.Create("B", a.Id);
        var c = t.Ws.Pages.Create("C", b.Id);
        Assert.Throws<SlothBossException>(() => t.Ws.Pages.Move(a.Id, c.Id));
        Assert.Throws<SlothBossException>(() => t.Ws.Pages.Move(a.Id, a.Id));
        Assert.Null(t.Ws.Pages.Get(a.Id)!.ParentId);
        t.Ws.Pages.Move(c.Id, null);
        Assert.Null(t.Ws.Pages.Get(c.Id)!.ParentId);
    }

    [Fact]
    public void Creating_under_missing_parent_fails()
    {
        using var t = new TempWorkspace();
        Assert.Throws<SlothBossException>(() => t.Ws.Pages.Create("x", Ids.New()));
    }

    [Fact]
    public void Deleting_page_removes_subtree_and_unlinks_tasks()
    {
        using var t = new TempWorkspace();
        var root = t.Ws.Pages.Create("Root");
        var child = t.Ws.Pages.Create("Child", root.Id);
        var task = t.Ws.Tasks.Create(new TaskDraft("Linked", PageId: child.Id));
        Assert.Equal((2, 1), t.Ws.Pages.DeletionImpact(root.Id));

        t.Ws.Pages.Delete(root.Id);

        Assert.Null(t.Ws.Pages.Get(child.Id));
        var kept = t.Ws.Tasks.Get(task.Id)!;
        Assert.Null(kept.PageId);
        Assert.Empty(t.Ws.Search.Query("Child"));
    }

    [Fact]
    public void Archive_hides_subtree_and_restore_moves_to_top_level_when_parent_archived()
    {
        using var t = new TempWorkspace();
        var root = t.Ws.Pages.Create("Root");
        var child = t.Ws.Pages.Create("Archivable child", root.Id);
        t.Ws.Pages.Archive(root.Id);
        Assert.DoesNotContain(t.Ws.Pages.List(), p => p.Id == child.Id);
        Assert.Empty(t.Ws.Search.Query("Archivable"));
        Assert.Equal([root.Id], t.Ws.Pages.Archived().Select(p => p.Id));

        t.Ws.Pages.Restore(child.Id);
        var restored = t.Ws.Pages.Get(child.Id)!;
        Assert.False(restored.IsArchived);
        Assert.Null(restored.ParentId);
        Assert.Single(t.Ws.Search.Query("Archivable"));
    }

    [Fact]
    public void Content_save_updates_links_backlinks_and_search()
    {
        using var t = new TempWorkspace();
        var target = t.Ws.Pages.Create("Target");
        var source = t.Ws.Pages.Create("Source");
        t.Ws.Pages.UpdateContent(source.Id, new PageDocument([
            new Block(BlockType.Paragraph, new Span("see "), new Span("Target") { Link = Links.ForPage(target.Id) }, new Span(" about zeppelins")),
        ]));
        Assert.Equal([source.Id], t.Ws.Pages.Backlinks(target.Id).Select(p => p.Id));
        Assert.Equal([target.Id], t.Ws.Pages.OutgoingLinks(source.Id).Select(p => p.Id));
        Assert.Single(t.Ws.Search.Query("zeppelin"));

        t.Ws.Pages.UpdateContent(source.Id, new PageDocument([Block.Paragraph("now about airships")]));
        Assert.Empty(t.Ws.Pages.Backlinks(target.Id));
        Assert.Empty(t.Ws.Search.Query("zeppelin"));
        Assert.Single(t.Ws.Search.Query("airsh"));
    }

    [Fact]
    public void Favorites_and_rename()
    {
        using var t = new TempWorkspace();
        var p = t.Ws.Pages.Create("Old");
        t.Ws.Pages.Rename(p.Id, "  New\nname ");
        t.Ws.Pages.SetFavorite(p.Id, true);
        Assert.Equal("New name", t.Ws.Pages.Get(p.Id)!.Title);
        Assert.Single(t.Ws.Pages.Favorites());
        Assert.Single(t.Ws.Search.Query("new name"));
    }

    [Fact]
    public void Task_validation_and_completion_lifecycle()
    {
        using var t = new TempWorkspace();
        Assert.Throws<SlothBossException>(() => t.Ws.Tasks.Create(new TaskDraft("   ")));
        Assert.Throws<SlothBossException>(() => t.Ws.Tasks.Create(new TaskDraft("x", Priority: "critical")));
        Assert.Throws<SlothBossException>(() => t.Ws.Tasks.Create(new TaskDraft("x", DueDate: "2026-13-01")));
        Assert.Throws<SlothBossException>(() => t.Ws.Tasks.Create(new TaskDraft("x", PageId: Ids.New())));

        var task = t.Ws.Tasks.Create(new TaskDraft("Write report", DueDate: "2026-10-25"));
        Assert.Null(task.CompletedAt);
        var done = t.Ws.Tasks.SetStatus(task.Id, TaskState.Done);
        Assert.NotNull(done.CompletedAt);
        var reopened = t.Ws.Tasks.SetStatus(task.Id, TaskState.Todo);
        Assert.Null(reopened.CompletedAt);
    }

    [Fact]
    public void Task_filters()
    {
        using var t = new TempWorkspace();
        var page = t.Ws.Pages.Create("P");
        var today = Clock.Today();
        var yesterday = DateOnly.ParseExact(today, "yyyy-MM-dd").AddDays(-1).ToString("yyyy-MM-dd");
        t.Ws.Tasks.Create(new TaskDraft("overdue", DueDate: yesterday, Priority: TaskPriority.High));
        t.Ws.Tasks.Create(new TaskDraft("today", DueDate: today, PageId: page.Id));
        t.Ws.Tasks.Create(new TaskDraft("nodate", Status: TaskState.Done));

        Assert.Equal(["overdue"], t.Ws.Tasks.List(new TaskFilter(Due: DueFilter.Overdue)).Select(x => x.Title));
        Assert.Equal(["today"], t.Ws.Tasks.List(new TaskFilter(Due: DueFilter.Today)).Select(x => x.Title));
        Assert.Equal(["nodate"], t.Ws.Tasks.List(new TaskFilter(Due: DueFilter.NoDate)).Select(x => x.Title));
        Assert.Equal(["overdue"], t.Ws.Tasks.List(new TaskFilter(Priority: TaskPriority.High)).Select(x => x.Title));
        Assert.Equal(["today"], t.Ws.Tasks.ForPage(page.Id).Select(x => x.Title));
        Assert.Equal(2, t.Ws.Tasks.List(new TaskFilter(HideDone: true)).Count);
        Assert.Equal(["nodate"], t.Ws.Tasks.List(new TaskFilter(Status: TaskState.Done)).Select(x => x.Title));
    }

    [Fact]
    public void Search_covers_tasks_and_handles_hostile_or_empty_queries()
    {
        using var t = new TempWorkspace();
        var task = t.Ws.Tasks.Create(new TaskDraft("Calibrate telescope", "Use the quasar reference"));
        Assert.Single(t.Ws.Search.Query("quasar"));
        Assert.Empty(t.Ws.Search.Query(""));
        Assert.Empty(t.Ws.Search.Query("   \"*() "));
        _ = t.Ws.Search.Query("\" OR 1=1 -- NEAR( title:*");
        _ = t.Ws.Search.Query("'; DROP TABLE pages; --");
        Assert.NotNull(t.Ws.Pages.List());

        t.Ws.Tasks.Update(task.Id, new TaskDraft("Calibrate telescope", "Use a pulsar instead"));
        Assert.Empty(t.Ws.Search.Query("quasar"));
        t.Ws.Tasks.Delete(task.Id);
        Assert.Empty(t.Ws.Search.Query("telescope"));
    }

    [Fact]
    public void Search_snippets_mark_matches_and_titles_rank_higher()
    {
        using var t = new TempWorkspace();
        t.Ws.Pages.Create("Mentions", content: new PageDocument([Block.Paragraph("a long paragraph that mentions gemma once")]));
        t.Ws.Pages.Create("Gemma notes", content: new PageDocument([Block.Paragraph("unrelated")]));
        var hits = t.Ws.Search.Query("gemma");
        Assert.Equal("Gemma notes", hits[0].Title);
        Assert.Contains(SearchHit.MatchStart, hits[1].Snippet);
    }

    [Fact]
    public void Settings_round_trip()
    {
        using var t = new TempWorkspace();
        t.Ws.Settings.Set(SettingsStore.SelectedModel, "gemma4:e2b-it-qat");
        Assert.Equal("gemma4:e2b-it-qat", t.Ws.Settings.Get(SettingsStore.SelectedModel));
        t.Ws.Settings.Set(SettingsStore.SelectedModel, null);
        Assert.Null(t.Ws.Settings.Get(SettingsStore.SelectedModel));
    }

    [Fact]
    public void Foreign_keys_are_enforced_at_the_database_level()
    {
        using var t = new TempWorkspace();
        Assert.Throws<SqliteException>(() => t.Ws.Read(c => c.Exec(
            "INSERT INTO tasks (id, title, created_at, updated_at, page_id) VALUES ('a', 'b', 'c', 'd', 'missing')")));
        Assert.Throws<SqliteException>(() => t.Ws.Read(c => c.Exec(
            "INSERT INTO tasks (id, title, status, created_at, updated_at) VALUES ('a', 'b', 'done', 'c', 'd')")));
    }
}
