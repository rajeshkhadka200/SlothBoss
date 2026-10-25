using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using SlothBoss.Core.Agent;
using SlothBoss.Core.Agent.Tools;

namespace SlothBoss.Core.Tests;

/// <summary>Slides (.pptx), spreadsheets (.xlsx) and images (via the local vision model).</summary>
public sealed class ContentReadingTests
{
    private static void Zip(string path, params (string Name, string Xml)[] parts)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, xml) in parts)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            w.Write(xml);
        }
    }

    private const string A = "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
    private const string RelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string RelType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";

    private static string Shape(string? placeholder, params string[] paragraphs) =>
        $"<p:sp><p:nvSpPr><p:nvPr>{(placeholder is null ? "" : $"<p:ph type=\"{placeholder}\"/>")}</p:nvPr></p:nvSpPr><p:txBody>{string.Concat(paragraphs.Select(p => $"<a:p><a:r><a:t>{p}</a:t></a:r></a:p>"))}</p:txBody></p:sp>";
    private static string Slide(params string[] shapes) => $"<p:sld {A}><p:cSld><p:spTree>{string.Concat(shapes)}</p:spTree></p:cSld></p:sld>";
    private static string Rels(params (string Id, string Type, string Target)[] rels) =>
        $"<Relationships xmlns=\"{RelNs}\">{string.Concat(rels.Select(r => $"<Relationship Id=\"{r.Id}\" Type=\"{RelType}{r.Type}\" Target=\"{r.Target}\"/>"))}</Relationships>";

    [Fact]
    public void Pptx_follows_presentation_order_and_each_slides_own_notes()
    {
        using var t = new TempWorkspace();
        var path = Path.Combine(t.Dir, "deck.pptx");
        // Presentation order is slide3, slide1, slide2 (as after reordering in PowerPoint); notes numbering doesn't match slides.
        Zip(path,
            ("ppt/presentation.xml", $"<p:presentation {A}><p:sldIdLst><p:sldId id=\"256\" r:id=\"rId7\"/><p:sldId id=\"257\" r:id=\"rId5\"/><p:sldId id=\"258\" r:id=\"rId6\"/></p:sldIdLst></p:presentation>"),
            ("ppt/_rels/presentation.xml.rels", Rels(("rId5", "slide", "slides/slide1.xml"), ("rId6", "slide", "slides/slide2.xml"), ("rId7", "slide", "slides/slide3.xml"))),
            ("ppt/slides/slide3.xml", Slide(Shape("ctrTitle", "SmartBin pitch"), Shape("sldNum", "1"))),
            ("ppt/slides/slide1.xml", Slide(Shape("title", "Budget"), Shape(null, "Hardware: 1,200 EUR"))),
            ("ppt/slides/slide2.xml", Slide(Shape("title", "Questions?"))),
            ("ppt/slides/_rels/slide1.xml.rels", Rels(("rId2", "notesSlide", "../notesSlides/notesSlide1.xml"))),
            ("ppt/notesSlides/notesSlide1.xml", Slide(Shape("body", "Mention the grant deadline 2026-11-15"), Shape("sldNum", "2"))));

        var text = DocumentText.Extract(path);
        Assert.Equal(3, text.Pages);
        Assert.Equal("Slide 1:\nSmartBin pitch\n\nSlide 2:\nBudget\nHardware: 1,200 EUR\nNotes: Mention the grant deadline 2026-11-15\n\nSlide 3:\nQuestions?", text.Text);
    }

    [Fact]
    public void Xlsx_shows_dates_as_dates_and_keeps_blank_cells_aligned()
    {
        using var t = new TempWorkspace();
        var path = Path.Combine(t.Dir, "dates.xlsx");
        const string ns = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"";
        Zip(path,
            ("xl/workbook.xml", $"<workbook {ns} xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Bills\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", Rels(("rId1", "worksheet", "worksheets/sheet1.xml"))),
            ("xl/styles.xml", $"<styleSheet {ns}><numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"dd/mm/yyyy\"/><numFmt numFmtId=\"165\" formatCode=\"&quot;EUR &quot;#,##0.00\"/></numFmts>" +
                              "<cellXfs count=\"4\"><xf numFmtId=\"0\"/><xf numFmtId=\"14\"/><xf numFmtId=\"164\"/><xf numFmtId=\"165\"/></cellXfs></styleSheet>"),
            ("xl/worksheets/sheet1.xml", $"<worksheet {ns}><sheetData>" +
                "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Rent</t></is></c><c r=\"C1\" s=\"1\"><v>46327</v></c><c r=\"D1\" s=\"3\"><v>950</v></c></row>" +
                "<row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>Paid</t></is></c><c r=\"B2\" t=\"b\"><v>1</v></c><c r=\"C2\" s=\"2\"><v>46338</v></c><c r=\"D2\"><f>D1*2</f><v>1900</v></c></row>" +
                "</sheetData></worksheet>"));

        Assert.Equal("Sheet: Bills\nRent\t\t2026-11-01\t950\nPaid\tTRUE\t2026-11-12\t1900", DocumentText.Extract(path).Text);
    }

    [Fact]
    public void Xlsx_resolves_shared_strings_and_names_each_sheet()
    {
        using var t = new TempWorkspace();
        var path = Path.Combine(t.Dir, "budget.xlsx");
        const string ns = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"";
        Zip(path,
            ("xl/workbook.xml", $"<workbook {ns} xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"October\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Notes\" sheetId=\"2\" r:id=\"rId2\"/></sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", Rels(("rId1", "worksheet", "worksheets/sheet1.xml"), ("rId2", "worksheet", "worksheets/sheet2.xml"))),
            ("xl/sharedStrings.xml", $"<sst {ns}><si><t>Item</t></si><si><t>Rent</t></si><si><r><t>Pay </t></r><r><t>by Nov 1</t></r></si></sst>"),
            ("xl/worksheets/sheet1.xml", $"<worksheet {ns}><sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"inlineStr\"><is><t>Amount</t></is></c></row>" +
                                          "<row r=\"2\"><c r=\"A2\" t=\"s\"><v>1</v></c><c r=\"B2\"><v>950</v></c></row></sheetData></worksheet>"),
            ("xl/worksheets/sheet2.xml", $"<worksheet {ns}><sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>2</v></c></row></sheetData></worksheet>"));

        var s = DocumentText.Extract(path).Text;
        Assert.Contains("Sheet: October\nItem\tAmount\nRent\t950", s);
        Assert.Contains("Sheet: Notes\nPay by Nov 1", s);
    }

    [Theory]
    [InlineData("WhatsApp Image 2026-01-02 at 10.11.12.jpeg", true)]
    [InlineData("ChatGPT Image Mar 3, 2026, 09_41_10 PM.png", true)]
    [InlineData("Screenshot 2026-05-01 101112.png", true)]
    [InlineData("IMG_20250101_123456.jpg", true)]
    [InlineData("3f2b8c1e-9a4d-4e7b-8c2a-1d5e6f7a8b9c.png", true)]
    [InlineData("download (3).pdf", true)]
    [InlineData("SmartBin poster.png", false)]
    [InlineData("Lease renewal letter.docx", false)]
    public void Generated_file_names_count_as_unclear(string name, bool unclear) =>
        Assert.Equal(unclear, PlanOrganizationTool.IsUnclearName(name));

    private static AgentHarness ImageFolder(bool vision)
    {
        var h = new AgentHarness(practice: false);
        if (vision) h.Fake.Capabilities = ["completion", "vision"];
        File.WriteAllBytes(Path.Combine(h.Folder, "WhatsApp Image 2026-01-02 at 10.11.12.jpeg"), [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 0xFF, 0xD9]);
        File.WriteAllText(Path.Combine(h.Folder, "notes.txt"), "Call the dentist to move the appointment to next week.");
        return h;
    }

    private static List<JsonObject> ImageRequests(AgentHarness h) =>
        h.Fake.ChatRequests.Where(r => r["messages"]!.AsArray().Any(m => m?["images"] is JsonArray)).ToList();

    [Fact]
    public async Task Images_are_read_by_the_vision_model_and_summarized()
    {
        using var h = ImageFolder(vision: true);
        h.Reply(AgentHarness.Plan(AgentHarness.Step("files.list"), AgentHarness.Step("docs.summarize", ("selection", "all"))));
        // Files are read in name order: notes.txt, then the image.
        h.Reply(AgentHarness.Summary("A reminder to call the dentist and move an appointment."));
        h.Fake.ChatReplies.Enqueue("A photo of a printed notice from the landlord.\nText: Water will be off on 2026-11-03 from 9:00 to 12:00.");
        h.Reply(AgentHarness.Summary("A landlord notice: the water is off on 2026-11-03 from 9:00 to 12:00."));

        var run = await h.StartAsync("Summarize my files", AutonomyMode.Autopilot);

        Assert.Equal(RunState.Completed, run.State);
        var image = Assert.Single(ImageRequests(h));
        Assert.Equal(Convert.ToBase64String([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 0xFF, 0xD9]), image["messages"]![1]!["images"]![0]!.GetValue<string>());
        // The description (not the raw bytes) is what the summary prompt reads.
        var summaryPrompt = h.Fake.ChatRequests.Skip(1).Select(r => r["messages"]![1]!["content"]!.GetValue<string>()).First(c => c.Contains("WhatsApp Image"));
        Assert.True(summaryPrompt.Contains("Water will be off on 2026-11-03"), summaryPrompt);
        Assert.Equal(2, run.Artifacts["summaries"]!.AsArray().Count);
    }

    [Fact]
    public async Task Without_a_vision_model_images_are_skipped_honestly()
    {
        using var h = ImageFolder(vision: false);
        h.Reply(AgentHarness.Plan(AgentHarness.Step("files.list"), AgentHarness.Step("docs.summarize", ("selection", "all"))));
        h.Reply(AgentHarness.Summary("A reminder to call the dentist and move an appointment."));

        var run = await h.StartAsync("Summarize my files", AutonomyMode.Autopilot);

        Assert.Equal(RunState.Completed, run.State);
        Assert.Empty(ImageRequests(h));
        Assert.Single(run.Artifacts["summaries"]!.AsArray());
        Assert.Contains(run.Artifacts["summary_errors"]!.AsArray(), e => e!.GetValue<string>().Contains("can't look at images"));
    }

    [Fact]
    public async Task Organizing_by_topic_uses_what_the_image_shows_and_looks_at_it_once()
    {
        using var h = ImageFolder(vision: true);
        h.Reply(AgentHarness.Plan(AgentHarness.Step("files.list"),
            AgentHarness.Step("files.plan_organization", ("strategy", "by_topic"), ("rename_unclear", true)), AgentHarness.Step("files.apply_organization")));
        h.Fake.ChatReplies.Enqueue("A photo of a printed notice from the landlord.\nText: Water will be off on 2026-11-03.");
        h.Reply(new JsonObject
        {
            ["assignments"] = new JsonArray(
                new JsonObject { ["file"] = "WhatsApp Image 2026-01-02 at 10.11.12.jpeg", ["folder"] = "Home", ["new_name"] = "Landlord water notice" },
                new JsonObject { ["file"] = "notes.txt", ["folder"] = "Health", ["new_name"] = "" }),
        });

        var run = await h.StartAsync("Organize my folder by topic", AutonomyMode.Autopilot);

        Assert.Equal(RunState.AwaitingApproval, run.State); // moving files is always asked first in Autopilot
        Assert.Single(ImageRequests(h));
        var organizePrompt = h.Fake.ChatRequests.Skip(1).Select(r => r["messages"]![1]!["content"]!.GetValue<string>()).First(c => c.Contains("notes.txt"));
        Assert.Contains("(image) A photo of a printed notice", organizePrompt);
        Assert.True(h.Store.FileOps(run.Id).Any(o => o.TargetRel == @"Home\Landlord water notice.jpeg"), string.Join(" | ", h.Store.FileOps(run.Id).Select(o => $"{o.Op} {o.SourceRel} -> {o.TargetRel}")));
    }
}

/// <summary>Runs only when SLOTHBOSS_OLLAMA_MODEL names a local vision model (e.g. gemma4:e2b-it-qat).</summary>
public class LiveContentTests
{
    private static string? Model => Environment.GetEnvironmentVariable("SLOTHBOSS_OLLAMA_MODEL");

    [Fact]
    public async Task Live_gemma_reads_the_text_in_a_photo_of_a_notice()
    {
        if (Model is null) return;
        using var t = new TempWorkspace();
        var folder = Directory.CreateDirectory(Path.Combine(t.Dir, "files")).FullName;
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "water-notice.png"), Path.Combine(folder, "IMG_20261020_101112.png"));
        var approved = t.Ws.Agent.ApproveFolder(folder);
        var model = new SlothBoss.Core.Agent.OllamaAgentModel(new SlothBoss.Core.Ai.OllamaClient { Timeout = TimeSpan.FromMinutes(5) }, Model);
        var rt = new AgentRuntime(t.Ws, () => model);

        var run = rt.Create("Summarize the files in this folder and write a report.", AutonomyMode.Autopilot, approved.Id);
        run = await rt.ExecuteAsync(run.Id);

        Assert.True(run.State is RunState.Completed or RunState.PartiallyCompleted, $"{run.State}: {run.StateReason}");
        var descriptions = string.Join("\n", (run.Artifacts["image_descriptions"] as JsonObject ?? []).Select(kv => kv.Value!.GetValue<string>()));
        if (Environment.GetEnvironmentVariable("SLOTHBOSS_EVIDENCE_DIR") is { } dir)
        {
            Directory.CreateDirectory(dir);
            var summaries = string.Join("\n", run.Artifacts["summaries"]?.AsArray().Select(s => $"- {s!["path"]}: {s["summary"]}") ?? []);
            File.WriteAllText(Path.Combine(dir, $"live-image-read-{DateTime.Now:yyyyMMdd-HHmmss}.md"),
                $"# Live image reading ({Model})\n\nInput: a PNG notice (\"NOTICE TO RESIDENTS — Water will be shut off on 3 November 2026, 9:00 - 12:00 — Maple Street Apartments, Block B\").\n\n## What Gemma saw\n\n{descriptions}\n\n## Summary\n\n{summaries}\n\nRun state: {run.State}\n");
        }
        Assert.Contains("November", descriptions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("water", descriptions, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Live_gemma_finds_deadlines_in_slides_a_spreadsheet_and_a_photo()
    {
        if (Model is null) return;
        using var t = new TempWorkspace();
        var folder = Directory.CreateDirectory(Path.Combine(t.Dir, "files")).FullName;
        foreach (var (asset, name) in new[] { ("pitch.pptx", "pitch.pptx"), ("budget.xlsx", "budget.xlsx"), ("water-notice.png", "IMG_20261020_101112.png") })
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", asset), Path.Combine(folder, name));
        var approved = t.Ws.Agent.ApproveFolder(folder);
        var model = new SlothBoss.Core.Agent.OllamaAgentModel(new SlothBoss.Core.Ai.OllamaClient { Timeout = TimeSpan.FromMinutes(5) }, Model);
        var rt = new AgentRuntime(t.Ws, () => model);

        var run = rt.Create("Read every file in this folder (slides, spreadsheet, photo), summarize them and list what I need to do with deadlines.", AutonomyMode.Autopilot, approved.Id);
        run = await rt.ExecuteAsync(run.Id);
        for (var i = 0; i < 20 && rt.IsActive(run.Id); i++) await rt.WaitIdleAsync(run.Id);
        run = t.Ws.Agent.Run(run.Id)!;

        var summaries = run.Artifacts["summaries"]?.AsArray().Select(s => $"- {s!["path"]}: {s["summary"]}").ToList() ?? [];
        var actions = run.Artifacts["actions"]?.AsArray().Select(a => $"- {a!["title"]} (due {a["due_date"]?.GetValue<string>() ?? "none"}; from {a["source"]}; quote verified: {a["quote_verified"]})").ToList() ?? [];
        if (Environment.GetEnvironmentVariable("SLOTHBOSS_EVIDENCE_DIR") is { } dir)
        {
            Directory.CreateDirectory(dir);
            var steps = string.Join('\n', t.Ws.Agent.Steps(run.Id).Select(s => $"- [{s.Status}] {s.Tool}"));
            File.WriteAllText(Path.Combine(dir, $"live-office-image-run-{DateTime.Now:yyyyMMdd-HHmmss}.md"), $"""
                # Live run: slides + spreadsheet + photo ({Model})

                Goal: {run.Goal}

                Files: pitch.pptx (2 slides + speaker notes), budget.xlsx (2 sheets, date cells), IMG_20261020_101112.png (photo of a notice)

                Run state: {run.State} — {run.StateReason}

                ## Steps

                {steps}

                ## Summaries

                {string.Join('\n', summaries)}

                ## Action items

                {string.Join('\n', actions)}

                """);
        }
        Assert.True(run.State is RunState.Completed or RunState.PartiallyCompleted or RunState.AwaitingApproval, $"{run.State}: {run.StateReason}");
        Assert.Equal(3, summaries.Count);
        var all = string.Join('\n', summaries.Concat(actions));
        Assert.Contains("2026-11-15", all.Replace("November 15, 2026", "2026-11-15"));
    }
}
