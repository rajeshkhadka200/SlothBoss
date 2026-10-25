using System.Text.Json.Nodes;
using SlothBoss.Core.Agent;
using SlothBoss.Core.Agent.Tools;

namespace SlothBoss.Core.Tests;

/// <summary>files.compare_versions: which copy is newest, what changed, and setting older versions aside.</summary>
public sealed class VersionTests
{
    [Theory]
    [InlineData("Emission_factors_corrected.xlsx", "emission factors")]
    [InlineData("Emission factors.xlsx", "emission factors")]
    [InlineData("README(4).md", "readme")]
    [InlineData("ICAAD26_Paper508_Full_Paper (3).docx", "icaad26 paper508 full paper")]
    [InlineData("report - Copy (2).docx", "report")]
    [InlineData("thesis_v3_final.docx", "thesis")]
    [InlineData("renew.txt", "renew")]
    [InlineData("Chapter 2.pptx", "chapter 2")]
    [InlineData("final.docx", "final")]
    public void Version_markers_are_ignored_when_grouping(string name, string expected) =>
        Assert.Equal(expected, CompareVersionsTool.BaseName(name));

    private static readonly IReadOnlyList<IAgentTool> Tools = ToolRegistry.Default();

    [Fact]
    public void Goal_about_the_latest_copy_adds_the_version_check_before_organizing()
    {
        var raw = new AgentPlan("", [], [new("files.list", "", []), new("files.plan_organization", "", []), new("report.create", "", [])], [], []);
        var plan = PlanValidator.Validate(raw, Tools, new AgentBudget(), true, goal: "Which copy of my paper is the latest? Tidy the rest away.");
        Assert.Equal(["files.list", "files.compare_versions", "files.plan_organization", "report.create"], plan.Steps.Select(s => s.Tool));
        var unchanged = PlanValidator.Validate(raw, Tools, new AgentBudget(), true, goal: "Organize by type");
        Assert.DoesNotContain(unchanged.Steps, s => s.Tool == "files.compare_versions");
    }

    private static AgentHarness PaperFolder()
    {
        var h = new AgentHarness(practice: false);
        void Write(string name, string text, int daysAgo)
        {
            var p = Path.Combine(h.Folder, name);
            File.WriteAllText(p, text);
            File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-daysAgo));
        }
        const string draft = "Offline function calling for farmers.\nResults: 81% accuracy on the held-out set.\n";
        Write("paper.txt", draft, 9);
        Write("paper (1).txt", draft, 5);
        Write("paper_final.txt", draft + "Camera-ready: add the CPU benchmark table.\n", 1);
        Write("notes.txt", "Unrelated notes about the conference trip.", 3);
        return h;
    }

    [Fact]
    public async Task Newest_version_is_found_with_what_changed_and_older_ones_are_set_aside()
    {
        using var h = PaperFolder();
        h.Reply(AgentHarness.Plan(AgentHarness.Step("files.list"), AgentHarness.Step("files.compare_versions"),
            AgentHarness.Step("files.plan_organization"), AgentHarness.Step("files.apply_organization")));

        var run = await h.StartAsync("Find the newest version of my paper and set old versions aside", AutonomyMode.Autopilot);

        Assert.Equal(RunState.AwaitingApproval, run.State); // moves are always asked first
        var group = Assert.Single(run.Artifacts["versions"]!.AsArray());
        Assert.Equal("paper_final.txt", group!["newest"]!.GetValue<string>());
        var files = group["files"]!.AsArray().Select(f => f!.AsObject()).ToDictionary(f => f["path"]!.GetValue<string>());
        Assert.Equal(1, files["paper.txt"]["added"]!.GetValue<int>());
        Assert.Equal(0, files["paper.txt"]["removed"]!.GetValue<int>());
        Assert.Contains("CPU benchmark", files["paper.txt"]["added_examples"]![0]!.GetValue<string>());
        Assert.False(files["paper (1).txt"]["same_as_newest"]!.GetValue<bool>());

        var ops = h.Store.FileOps(run.Id).Where(o => o.Op == "move").ToDictionary(o => o.SourceRel!);
        Assert.Equal(@"Older versions\paper.txt", ops["paper.txt"].TargetRel);
        Assert.Equal(@"Older versions\paper (1).txt", ops["paper (1).txt"].TargetRel);
        Assert.Equal(@"Documents\paper_final.txt", ops["paper_final.txt"].TargetRel);
        Assert.Equal(@"Documents\notes.txt", ops["notes.txt"].TargetRel);
        Assert.Contains("Older version of “paper_final.txt”", ops["paper.txt"].Reason);
    }

    [Fact]
    public async Task Identical_copies_are_reported_as_identical_and_nothing_moves_without_organizing()
    {
        using var h = PaperFolder();
        File.Copy(Path.Combine(h.Folder, "paper_final.txt"), Path.Combine(h.Folder, "paper_final (1).txt"));
        File.SetLastWriteTimeUtc(Path.Combine(h.Folder, "paper_final (1).txt"), DateTime.UtcNow.AddDays(-2));
        h.Reply(AgentHarness.Plan(AgentHarness.Step("files.list"), AgentHarness.Step("files.compare_versions"), AgentHarness.Step("report.create")));

        var run = await h.StartAsync("Which copy of my paper is the latest?", AutonomyMode.Autopilot);

        Assert.Equal(RunState.Completed, run.State);
        var files = run.Artifacts["versions"]![0]!["files"]!.AsArray().Select(f => f!.AsObject()).ToDictionary(f => f["path"]!.GetValue<string>());
        Assert.True(files["paper_final (1).txt"]["same_as_newest"]!.GetValue<bool>());
        Assert.Empty(h.Store.FileOps(run.Id).Where(o => o.Op == "move"));
        Assert.True(File.Exists(Path.Combine(h.Folder, "paper.txt")));
        var page = h.Ws.Pages.Get(run.Report!.ReportPageId!)!;
        Assert.Contains("Versions of the same file", page.ContentText);
        Assert.Contains("identical to the newest", page.ContentText);
    }

    [Theory]
    [InlineData("ChatGPT 图像 2026年10月19日 09_46_39-1.png", true)]
    [InlineData("ChatGPT Image 2026年9月23日 10_51_58.png", true)]
    [InlineData("ChatGPT notes for exam.png", false)]
    public void Ai_image_names_in_any_language_count_as_unclear(string name, bool unclear) =>
        Assert.Equal(unclear, PlanOrganizationTool.IsUnclearName(name));
}
