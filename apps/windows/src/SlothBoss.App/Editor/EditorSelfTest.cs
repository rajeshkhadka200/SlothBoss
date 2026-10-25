using System.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SlothBoss.Core.Documents;

namespace SlothBoss.App.Editor;

/// <summary>
/// Runs the editor converter against the real WinUI RichEditBox and writes a report, then exits.
/// Usage: SlothBoss.exe --selftest-editor &lt;contracts/fixtures dir&gt; &lt;report path&gt;
/// Used by scripts/test-windows.ps1 because TOM behavior can only be verified in a live control.
/// </summary>
public sealed class EditorSelfTest : Window
{
    private readonly string _fixtures;
    private readonly string _report;
    private readonly StringBuilder _log = new();
    private int _failures;

    public EditorSelfTest(string fixtures, string report)
    {
        _fixtures = fixtures;
        _report = report;
        Title = "SlothBoss editor self-test";
        var box = new RichEditBox { Width = 800, Height = 600 };
        Content = box;
        box.Loaded += (_, _) => Run(box);
    }

    private void Check(string name, bool ok, string detail = "")
    {
        _log.AppendLine($"{(ok ? "PASS" : "FAIL")} {name}{(ok || detail.Length == 0 ? "" : " :: " + detail)}");
        if (!ok) _failures++;
    }

    private void Run(RichEditBox box)
    {
        try
        {
            var font = box.Document.GetDefaultCharacterFormat().Name;
            if (string.IsNullOrWhiteSpace(font)) font = "Segoe UI Variable Text";

            foreach (var file in Directory.GetFiles(Path.Combine(_fixtures, "content"), "*.json"))
            {
                var doc = PageDocument.Parse(File.ReadAllText(file));
                RichTextConverter.Load(box, doc, font);
                var back = RichTextConverter.Read(box);
                var expected = doc.ToJson();
                var actual = back.ToJson();
                Check($"round-trip {Path.GetFileName(file)}", expected == actual, $"\n  expected {expected}\n  actual   {actual}");
                box.Document.GetText(TextGetOptions.IncludeNumbering, out var numbered);
                Check("numbered list renders 1. and 2.", numbered.Contains("1.") && numbered.Contains("2."), numbered.Replace("\r", "⏎"));
            }

            var cmd = new EditorCommands(box, font);
            RichTextConverter.Load(box, new PageDocument([Block.Paragraph("Title here"), Block.Paragraph("body")]), font);

            box.Document.Selection.SetRange(2, 2);
            cmd.SetBlock(BlockType.Heading, 2);
            var d1 = RichTextConverter.Read(box);
            Check("SetBlock heading 2", d1.Blocks[0].Type == BlockType.Heading && d1.Blocks[0].Level == 2 && d1.Blocks[1].Type == BlockType.Paragraph, d1.ToJson());

            cmd.SetBlock(BlockType.Heading, 2);
            Check("SetBlock same type reverts", RichTextConverter.Read(box).Blocks[0].Type == BlockType.Paragraph, RichTextConverter.Read(box).ToJson());

            box.Document.Selection.SetRange(12, 12);
            cmd.SetBlock(BlockType.Todo);
            var d2 = RichTextConverter.Read(box);
            Check("SetBlock todo", d2.Blocks[1].Type == BlockType.Todo && d2.Blocks[1].Text == "body" && !d2.Blocks[1].Checked, d2.ToJson());
            cmd.ToggleChecked();
            Check("ToggleChecked", RichTextConverter.Read(box).Blocks[1].Checked, RichTextConverter.Read(box).ToJson());

            foreach (var (type, label) in new[] { (BlockType.Bulleted, "bulleted"), (BlockType.Numbered, "numbered"), (BlockType.Quote, "quote"), (BlockType.Code, "code") })
            {
                RichTextConverter.Load(box, new PageDocument([Block.Paragraph("x y z")]), font);
                box.Document.Selection.SetRange(1, 1);
                cmd.SetBlock(type);
                var d = RichTextConverter.Read(box);
                Check($"SetBlock {label}", d.Blocks.Count == 1 && d.Blocks[0].Type == type && d.Blocks[0].Text == "x y z", d.ToJson());
            }

            RichTextConverter.Load(box, new PageDocument([Block.Paragraph("plain words")]), font);
            box.Document.Selection.SetRange(0, 5);
            cmd.ToggleBold();
            box.Document.Selection.SetRange(6, 11);
            cmd.ToggleInlineCode();
            var d3 = RichTextConverter.Read(box);
            Check("bold + inline code", d3.Blocks[0].Spans.Count == 3 && d3.Blocks[0].Spans[0].Bold && d3.Blocks[0].Spans[2].Code, d3.ToJson());

            RichTextConverter.Load(box, new PageDocument([Block.Paragraph("go here now")]), font);
            box.Document.Selection.SetRange(3, 7);
            cmd.ApplyLink("https://example.com/", "unused");
            var d4 = RichTextConverter.Read(box);
            Check("apply link", d4.Blocks[0].Spans.Any(s => s.Link == "https://example.com/" && s.Text == "here") && d4.Blocks[0].Text == "go here now", d4.ToJson());

            RichTextConverter.Load(box, new PageDocument(), font);
            box.Document.Selection.SetText(TextSetOptions.None, "## ");
            box.Document.Selection.Collapse(false);
            var converted = cmd.TryMarkdownShortcut();
            box.Document.Selection.TypeText("Typed heading");
            var d5 = RichTextConverter.Read(box);
            Check("markdown shortcut '## '", converted && d5.Blocks[0].Type == BlockType.Heading && d5.Blocks[0].Level == 2 && d5.Blocks[0].Text == "Typed heading", $"converted={converted} {d5.ToJson()}");

            RichTextConverter.Load(box, new PageDocument([Block.Heading(1, "Big")]), font);
            box.Document.Selection.SetRange(3, 3);
            cmd.HandleEnter();
            box.Document.Selection.TypeText("after");
            var d7 = RichTextConverter.Read(box);
            Check("Enter after heading gives body text", d7.Blocks.Count == 2 && d7.Blocks[0].Type == BlockType.Heading && d7.Blocks[1].Type == BlockType.Paragraph && d7.Blocks[1].Text == "after", d7.ToJson());

            RichTextConverter.Load(box, new PageDocument([new Block(BlockType.Bulleted, new Span("item")), new Block(BlockType.Bulleted)]), font);
            box.Document.Selection.SetRange(5, 5);
            cmd.HandleEnter();
            var d8 = RichTextConverter.Read(box);
            Check("Enter on empty list item exits list", d8.Blocks[0].Type == BlockType.Bulleted && (d8.Blocks.Count == 1 || d8.Blocks[1].Type == BlockType.Paragraph), d8.ToJson());

            RichTextConverter.Load(box, new PageDocument([Block.Paragraph("abc")]), font);
            box.Document.Selection.SetRange(3, 3);
            cmd.PastePlain("line one\r\nline two");
            var d9 = RichTextConverter.Read(box);
            Check("plain paste splits paragraphs", d9.Blocks.Count == 2 && d9.Blocks[0].Text == "abcline one" && d9.Blocks[1].Text == "line two", d9.ToJson());

            RichTextConverter.Load(box, new PageDocument([new Block(BlockType.Todo, new Span("first"))]), font);
            box.Document.Selection.SetRange(7, 7);
            cmd.HandleEnter();
            box.Document.Selection.TypeText("second");
            var d6 = RichTextConverter.Read(box);
            Check("Enter continues checklist", d6.Blocks.Count == 2 && d6.Blocks[1].Type == BlockType.Todo && d6.Blocks[1].Text == "second", d6.ToJson());
        }
        catch (Exception e)
        {
            Check("self-test crashed", false, e.ToString());
        }

        _log.AppendLine(_failures == 0 ? "ALL PASSED" : $"{_failures} FAILED");
        File.WriteAllText(_report, _log.ToString());
        Application.Current.Exit();
    }
}
