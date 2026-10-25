using SlothBoss.Core.Actions;
using SlothBoss.Core.Documents;
using SlothBoss.Core.Storage;

namespace SlothBoss.Core.Tests;

[Collection("clock")]
public class ActionTests
{
    private static int Count(Workspace ws, string table) => ws.Read(c => Convert.ToInt32(c.Scalar($"SELECT count(*) FROM {table}")));

    [Fact]
    public void Proposals_and_rejections_do_not_mutate_the_workspace()
    {
        using var t = new TempWorkspace();
        var page = t.Ws.Pages.Create("Research");
        var batch = Ids.New();
        var p = t.Ws.Actions.Propose(batch, new CreateTaskPayload("Investigate", null, "medium", null, page.Id), "Create task", "gemma");
        Assert.Equal(0, Count(t.Ws, "tasks"));

        var rejected = t.Ws.Actions.Reject(p.Id);
        Assert.Equal(ActionStatus.Rejected, rejected.Status);
        Assert.Equal(0, Count(t.Ws, "tasks"));
        Assert.Throws<SlothBossException>(() => t.Ws.Actions.Approve(p.Id));
        Assert.Equal(0, Count(t.Ws, "tasks"));
    }

    [Fact]
    public void Approving_creates_linked_task_and_records_result()
    {
        using var t = new TempWorkspace();
        var page = t.Ws.Pages.Create("Research");
        var p = t.Ws.Actions.Propose(Ids.New(), new CreateTaskPayload("Investigate", "why", "high", "2026-11-01", page.Id), "s", "gemma");
        var applied = t.Ws.Actions.Approve(p.Id);
        Assert.Equal(ActionStatus.Applied, applied.Status);
        var task = Assert.Single(t.Ws.Tasks.ForPage(page.Id));
        Assert.Equal("Investigate", task.Title);
        Assert.Equal(task.Id, applied.CreatedRecordId);
    }

    [Fact]
    public void Approval_fails_cleanly_when_target_disappeared()
    {
        using var t = new TempWorkspace();
        var page = t.Ws.Pages.Create("Research");
        var p = t.Ws.Actions.Propose(Ids.New(), new CreateTaskPayload("Investigate", null, "medium", null, page.Id), "s", null);
        t.Ws.Pages.Delete(page.Id);
        var result = t.Ws.Actions.Approve(p.Id);
        Assert.Equal(ActionStatus.Failed, result.Status);
        Assert.Contains("no longer exists", result.Error);
        Assert.Equal(0, Count(t.Ws, "tasks"));
    }

    [Theory]
    [InlineData("""{"type":"execute_sql","sql":"DROP TABLE pages"}""")]
    [InlineData("""{"type":"create_task","title":"x","priority":"medium","sql":"1"}""")]
    [InlineData("""{"type":"create_task","title":"","priority":"medium"}""")]
    [InlineData("""{"type":"create_task","title":"x","priority":"whenever"}""")]
    [InlineData("""{"type":"create_task","title":"x","priority":"low","due_date":"next tuesday"}""")]
    [InlineData("""{"type":"create_task","title":"x","priority":"low","page_id":"'; DROP TABLE pages; --"}""")]
    [InlineData("""{"type":"update_task","task_id":"0b0c6a52-0d6f-4a37-9b67-3f0f1c2b1a10","changes":{"title":"hijack"}}""")]
    [InlineData("""{"type":"update_task","task_id":"0b0c6a52-0d6f-4a37-9b67-3f0f1c2b1a10","changes":{}}""")]
    [InlineData("""{"type":"add_page_reference","page_id":"0b0c6a52-0d6f-4a37-9b67-3f0f1c2b1a10","target_page_id":"0b0c6a52-0d6f-4a37-9b67-3f0f1c2b1a10"}""")]
    [InlineData("""{"type":"create_page","title":"x","content":{"version":1,"blocks":[{"type":"script","spans":[]}]}}""")]
    [InlineData("[1,2,3]")]
    public void Malformed_or_unsupported_payloads_are_rejected(string json)
    {
        Assert.Throws<ActionValidationException>(() => ActionPayload.Parse(json));
    }

    [Fact]
    public void Undo_create_task_only_when_unchanged()
    {
        using var clock = new SteppingClock();
        using var t = new TempWorkspace();
        var a = t.Ws.Actions.Approve(t.Ws.Actions.Propose(Ids.New(), new CreateTaskPayload("A", null, "low", null, null), "s", null).Id);
        var b = t.Ws.Actions.Approve(t.Ws.Actions.Propose(Ids.New(), new CreateTaskPayload("B", null, "low", null, null), "s", null).Id);

        Assert.Equal(ActionStatus.Undone, t.Ws.Actions.Undo(a.Id).Status);
        Assert.Null(t.Ws.Tasks.Get(a.CreatedRecordId!));

        t.Ws.Tasks.SetStatus(b.CreatedRecordId!, TaskState.InProgress); // user edits after approval
        var ex = Assert.Throws<SlothBossException>(() => t.Ws.Actions.Undo(b.Id));
        Assert.Contains("edited", ex.Message);
        Assert.NotNull(t.Ws.Tasks.Get(b.CreatedRecordId!));
        Assert.Equal(ActionStatus.Applied, t.Ws.Actions.Get(b.Id)!.Status);
    }

    [Fact]
    public void Draft_content_and_page_reference_append_and_undo()
    {
        using var clock = new SteppingClock();
        using var t = new TempWorkspace();
        var page = t.Ws.Pages.Create("Notes", content: new PageDocument([Block.Paragraph("original")]));
        var other = t.Ws.Pages.Create("Other");
        var original = t.Ws.Pages.Get(page.Id)!.ContentJson;

        var draft = t.Ws.Actions.Approve(t.Ws.Actions.Propose(Ids.New(),
            new DraftPageContentPayload(page.Id, [Block.Heading(2, "Summary"), new Block(BlockType.Bulleted, new Span("point"))]), "s", null).Id);
        Assert.Equal(ActionStatus.Applied, draft.Status);
        Assert.Equal("original\nSummary\npoint", t.Ws.Pages.Get(page.Id)!.ContentText);

        var link = t.Ws.Actions.Approve(t.Ws.Actions.Propose(Ids.New(), new AddPageReferencePayload(page.Id, other.Id), "s", null).Id);
        Assert.Equal(ActionStatus.Applied, link.Status);
        Assert.Single(t.Ws.Pages.Backlinks(other.Id));

        // Undoing the older action would discard the newer one's change, so it must be refused.
        Assert.Throws<SlothBossException>(() => t.Ws.Actions.Undo(draft.Id));

        t.Ws.Actions.Undo(link.Id);
        Assert.Empty(t.Ws.Pages.Backlinks(other.Id));
        t.Ws.Actions.Undo(draft.Id);
        Assert.Equal(original, t.Ws.Pages.Get(page.Id)!.ContentJson);
    }

    [Fact]
    public void Duplicate_page_reference_fails_without_change()
    {
        using var t = new TempWorkspace();
        var page = t.Ws.Pages.Create("Notes");
        var other = t.Ws.Pages.Create("Other");
        t.Ws.Actions.Approve(t.Ws.Actions.Propose(Ids.New(), new AddPageReferencePayload(page.Id, other.Id), "s", null).Id);
        var before = t.Ws.Pages.Get(page.Id)!.ContentJson;
        var second = t.Ws.Actions.Approve(t.Ws.Actions.Propose(Ids.New(), new AddPageReferencePayload(page.Id, other.Id), "s", null).Id);
        Assert.Equal(ActionStatus.Failed, second.Status);
        Assert.Equal(before, t.Ws.Pages.Get(page.Id)!.ContentJson);
    }

    [Fact]
    public void Update_task_applies_whitelisted_fields_and_undoes()
    {
        using var clock = new SteppingClock();
        using var t = new TempWorkspace();
        var task = t.Ws.Tasks.Create(new TaskDraft("T", Priority: TaskPriority.Low));
        var a = t.Ws.Actions.Approve(t.Ws.Actions.Propose(Ids.New(), new UpdateTaskPayload(task.Id, TaskState.Done, TaskPriority.High, true, "2026-12-01"), "s", null).Id);
        var updated = t.Ws.Tasks.Get(task.Id)!;
        Assert.Equal((TaskState.Done, TaskPriority.High, "2026-12-01"), (updated.Status, updated.Priority, updated.DueDate));
        Assert.NotNull(updated.CompletedAt);

        t.Ws.Actions.Undo(a.Id);
        var reverted = t.Ws.Tasks.Get(task.Id)!;
        Assert.Equal((TaskState.Todo, TaskPriority.Low, (string?)null, (string?)null), (reverted.Status, reverted.Priority, reverted.DueDate, reverted.CompletedAt));
    }

    [Fact]
    public void Chained_actions_undo_in_reverse_order_but_user_edits_block()
    {
        using var clock = new SteppingClock();
        using var t = new TempWorkspace();
        var create = t.Ws.Actions.Approve(t.Ws.Actions.Propose(Ids.New(), new CreateTaskPayload("T", null, "low", null, null), "s", null).Id);
        var taskId = create.CreatedRecordId!;
        var update = t.Ws.Actions.Approve(t.Ws.Actions.Propose(Ids.New(), new UpdateTaskPayload(taskId, null, "high", false, null), "s", null).Id);
        t.Ws.Actions.Undo(update.Id);
        t.Ws.Actions.Undo(create.Id);
        Assert.Null(t.Ws.Tasks.Get(taskId));

        var create2 = t.Ws.Actions.Approve(t.Ws.Actions.Propose(Ids.New(), new CreateTaskPayload("U", null, "low", null, null), "s", null).Id);
        var id2 = create2.CreatedRecordId!;
        var update2 = t.Ws.Actions.Approve(t.Ws.Actions.Propose(Ids.New(), new UpdateTaskPayload(id2, null, "high", false, null), "s", null).Id);
        t.Ws.Tasks.Update(id2, TaskStore.ToDraft(t.Ws.Tasks.Get(id2)!) with { Title = "U edited" });
        Assert.Throws<SlothBossException>(() => t.Ws.Actions.Undo(update2.Id));
        Assert.Throws<SlothBossException>(() => t.Ws.Actions.Undo(create2.Id));
        Assert.Equal("U edited", t.Ws.Tasks.Get(id2)!.Title);
    }

    [Fact]
    public void Create_page_action_and_undo()
    {
        using var t = new TempWorkspace();
        var a = t.Ws.Actions.Approve(t.Ws.Actions.Propose(Ids.New(),
            new CreatePagePayload("Drafted", null, new PageDocument([Block.Paragraph("body")])), "s", null).Id);
        var pageId = a.CreatedRecordId!;
        Assert.Equal("body", t.Ws.Pages.Get(pageId)!.ContentText);
        t.Ws.Actions.Undo(a.Id);
        Assert.Null(t.Ws.Pages.Get(pageId));
        Assert.Throws<SlothBossException>(() => t.Ws.Actions.Undo(a.Id));
    }

    [Fact]
    public void History_records_every_decision()
    {
        using var t = new TempWorkspace();
        var batch = Ids.New();
        var p1 = t.Ws.Actions.Propose(batch, new CreateTaskPayload("A", null, "low", null, null), "s", null);
        var p2 = t.Ws.Actions.Propose(batch, new CreateTaskPayload("B", null, "low", null, null), "s", null);
        t.Ws.Actions.Approve(p1.Id);
        t.Ws.Actions.Reject(p2.Id);
        Assert.Equal([ActionStatus.Applied, ActionStatus.Rejected], t.Ws.Actions.Batch(batch).Select(a => a.Status));
        Assert.Equal(2, t.Ws.Actions.History().Count);
    }
}

[CollectionDefinition("clock", DisableParallelization = true)]
public class ClockCollection;
