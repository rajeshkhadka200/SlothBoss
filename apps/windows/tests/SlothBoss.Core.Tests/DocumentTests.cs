using SlothBoss.Core.Documents;

namespace SlothBoss.Core.Tests;

public class DocumentTests
{
    private static PageDocument KitchenSink() => PageDocument.Parse(Fixtures.Read("content/kitchen-sink.json"));

    [Fact]
    public void Canonical_json_round_trips_fixture_exactly()
    {
        Assert.Equal(Fixtures.Read("content/kitchen-sink.json").Trim(), KitchenSink().ToJson());
    }

    [Fact]
    public void Markdown_export_matches_fixture()
    {
        Assert.Equal(Fixtures.Read("content/kitchen-sink.md").Replace("\r\n", "\n").TrimEnd(), Markdown.Export(KitchenSink()));
    }

    [Fact]
    public void Page_export_prepends_escaped_title()
    {
        Assert.StartsWith("# Plan \\*v2\\*\n\n# Research plan", Markdown.ExportPage("Plan *v2*", KitchenSink()));
        Assert.Equal("# Untitled\n", Markdown.ExportPage("  ", new PageDocument()));
    }

    [Fact]
    public void Plain_text_projection_matches_fixture()
    {
        Assert.Equal(Fixtures.Read("content/kitchen-sink.txt").Replace("\r\n", "\n").TrimEnd(), KitchenSink().ToPlainText());
    }

    [Fact]
    public void Markdown_parse_of_export_reproduces_document()
    {
        var doc = KitchenSink();
        var reparsed = Markdown.Parse(Markdown.Export(doc));
        Assert.Equal(doc.ToJson(), reparsed.ToJson());
    }

    [Fact]
    public void Page_references_are_extracted()
    {
        Assert.Equal(["0b0c6a52-0d6f-4a37-9b67-3f0f1c2b1a10"], KitchenSink().PageReferences());
    }

    [Theory]
    [InlineData("{\"version\":2,\"blocks\":[]}")]
    [InlineData("{\"version\":1}")]
    [InlineData("{\"version\":1,\"blocks\":[{\"type\":\"video\",\"spans\":[]}]}")]
    [InlineData("{\"version\":1,\"blocks\":[{\"type\":\"heading\",\"level\":7,\"spans\":[]}]}")]
    [InlineData("{\"version\":1,\"blocks\":[],\"extra\":true}")]
    [InlineData("not json")]
    public void Invalid_content_is_rejected(string json)
    {
        Assert.Throws<DocumentFormatException>(() => PageDocument.Parse(json));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/system.ini")]
    [InlineData("slothboss://page/not-a-uuid")]
    public void Unsafe_links_are_stripped(string url)
    {
        var doc = PageDocument.Parse($"{{\"version\":1,\"blocks\":[{{\"type\":\"paragraph\",\"spans\":[{{\"text\":\"x\",\"link\":\"{url}\"}}]}}]}}");
        Assert.Null(doc.Blocks[0].Spans[0].Link);
    }

    [Fact]
    public void Normalize_merges_adjacent_spans_and_drops_empty_ones()
    {
        var doc = new PageDocument([new Block(BlockType.Paragraph, new Span("a"), new Span(""), new Span("b"), new Span("c") { Bold = true })]).Normalize();
        Assert.Equal(2, doc.Blocks[0].Spans.Count);
        Assert.Equal("ab", doc.Blocks[0].Spans[0].Text);
    }

    [Fact]
    public void Markdown_parser_handles_drafted_note_shapes()
    {
        var doc = Markdown.Parse("## Findings\nFirst line\ncontinues here.\n\n- [ ] Check *this*\n3) Third\n> quoted\n[bad](javascript:x)");
        Assert.Equal(BlockType.Heading, doc.Blocks[0].Type);
        Assert.Equal(2, doc.Blocks[0].Level);
        Assert.Equal("First line continues here.", doc.Blocks[1].Text);
        Assert.Equal(BlockType.Todo, doc.Blocks[2].Type);
        Assert.True(doc.Blocks[2].Spans[1].Italic);
        Assert.Equal(BlockType.Numbered, doc.Blocks[3].Type);
        Assert.Equal(BlockType.Quote, doc.Blocks[4].Type);
        Assert.Null(doc.Blocks[5].Spans[0].Link);
    }
}
