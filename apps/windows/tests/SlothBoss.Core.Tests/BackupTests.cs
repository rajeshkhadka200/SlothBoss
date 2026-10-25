using System.Text.Json.Nodes;
using SlothBoss.Core.Documents;
using SlothBoss.Core.Storage;

namespace SlothBoss.Core.Tests;

public class BackupTests
{
    [Fact]
    public void Export_then_restore_preserves_pages_tasks_hierarchy_and_links()
    {
        using var source = new TempWorkspace();
        var root = source.Ws.Pages.Create("Root");
        var child = source.Ws.Pages.Create("Child", root.Id, new PageDocument([
            new Block(BlockType.Paragraph, new Span("see "), new Span("Root") { Link = Links.ForPage(root.Id), Bold = true }),
            new Block(BlockType.Todo, new Span("check")) { Checked = true },
        ]));
        source.Ws.Pages.SetFavorite(child.Id, true);
        var archived = source.Ws.Pages.Create("Old");
        source.Ws.Pages.Archive(archived.Id);
        var task = source.Ws.Tasks.Create(new TaskDraft("Do it", "desc", TaskState.Done, TaskPriority.Urgent, "2026-10-30", child.Id));
        source.Ws.Tasks.Create(new TaskDraft("Free"));

        var file = Path.Combine(source.Dir, "backup.json");
        BackupService.ExportToFile(source.Ws, file, "test");
        var validated = BackupService.ValidateFile(file);

        using var target = new TempWorkspace();
        target.Ws.Pages.Create("Will be replaced");
        BackupService.RestoreReplacing(target.Ws, validated);

        Assert.Equal(
            source.Ws.Pages.List(includeArchived: true).OrderBy(p => p.Id),
            target.Ws.Pages.List(includeArchived: true).OrderBy(p => p.Id));
        Assert.Equal(source.Ws.Pages.Get(child.Id)!.ContentJson, target.Ws.Pages.Get(child.Id)!.ContentJson);
        Assert.Equal(source.Ws.Tasks.List().OrderBy(x => x.Id), target.Ws.Tasks.List().OrderBy(x => x.Id));
        Assert.Equal([child.Id], target.Ws.Pages.Backlinks(root.Id).Select(p => p.Id));
        Assert.Equal(task.CompletedAt, target.Ws.Tasks.Get(task.Id)!.CompletedAt);
        Assert.Empty(target.Ws.Search.Query("Old"));
        Assert.Single(target.Ws.Search.Query("Do it"));
        Assert.Empty(target.Ws.Search.Query("replaced"));
    }

    [Fact]
    public void Valid_fixture_restores()
    {
        var backup = BackupService.Validate(Fixtures.Read("backup/valid-v1.json"));
        using var t = new TempWorkspace();
        BackupService.RestoreReplacing(t.Ws, backup);
        Assert.Equal(2, t.Ws.Pages.List().Count);
        Assert.Equal(["Background", "Experiments"], t.Ws.Pages.Breadcrumb("6f1d2c3b-4a59-4e7f-8a1b-2c3d4e5f6a7b").Select(p => p.Title));
        Assert.Single(t.Ws.Tasks.ForPage("6f1d2c3b-4a59-4e7f-8a1b-2c3d4e5f6a7b"));
        Assert.Single(t.Ws.Pages.Backlinks("0b0c6a52-0d6f-4a37-9b67-3f0f1c2b1a10"));
    }

    public static IEnumerable<object[]> InvalidCases() =>
        JsonNode.Parse(Fixtures.Read("backup/invalid-cases.json"))!["cases"]!.AsArray()
            .Select(c => new object[] { c!["name"]!.GetValue<string>(), c["backup"]!.ToJsonString() });

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public void Invalid_backups_are_rejected_before_any_write(string name, string json)
    {
        using var t = new TempWorkspace();
        var existing = t.Ws.Pages.Create("Keep me");
        var ex = Assert.ThrowsAny<SlothBossException>(() => BackupService.RestoreReplacing(t.Ws, BackupService.Validate(json)));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message), name);
        Assert.NotNull(t.Ws.Pages.Get(existing.Id));
    }

    [Fact]
    public void Snapshot_refuses_to_overwrite()
    {
        using var t = new TempWorkspace();
        var snap = Path.Combine(t.Dir, "snap.db");
        t.Ws.SnapshotTo(snap);
        Assert.True(File.Exists(snap));
        Assert.Throws<SlothBossException>(() => t.Ws.SnapshotTo(snap));
    }

    [Theory]
    [InlineData("../../evil", "-..-evil.md")]
    [InlineData("C:\\Windows\\x", "C--Windows-x.md")]
    [InlineData("   ", "Untitled.md")]
    [InlineData("Plan: v2?", "Plan- v2-.md")]
    public void Export_file_names_are_sanitized(string title, string expected)
    {
        var name = BackupService.SafeFileName(title, ".md");
        Assert.Equal(expected, name);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, name);
    }
}
