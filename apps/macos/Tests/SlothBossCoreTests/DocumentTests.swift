import XCTest
@testable import SlothBossCore

/// Content conformance against contracts/fixtures/content (shared with the Windows client).
final class DocumentTests: XCTestCase {
    private func kitchenSink() throws -> PageDocument {
        try PageDocument.parse(Fixtures.read("content/kitchen-sink.json"))
    }

    func testCanonicalJSONRoundTripsFixtureExactly() throws {
        XCTAssertEqual(try Fixtures.read("content/kitchen-sink.json").trimmed, try kitchenSink().toJSON())
    }

    func testMarkdownExportMatchesFixture() throws {
        let expected = try Fixtures.read("content/kitchen-sink.md").replacingOccurrences(of: "\r\n", with: "\n").trimmingTrailingWhitespace
        XCTAssertEqual(expected, Markdown.export(try kitchenSink()))
    }

    func testPageExportPrependsEscapedTitle() throws {
        XCTAssertTrue(Markdown.exportPage(title: "Plan *v2*", try kitchenSink()).hasPrefix("# Plan \\*v2\\*\n\n# Research plan"))
        XCTAssertEqual("# Untitled\n", Markdown.exportPage(title: "  ", PageDocument()))
    }

    func testPlainTextProjectionMatchesFixture() throws {
        let expected = try Fixtures.read("content/kitchen-sink.txt").replacingOccurrences(of: "\r\n", with: "\n").trimmingTrailingWhitespace
        XCTAssertEqual(expected, try kitchenSink().plainText())
    }

    func testMarkdownParseOfExportReproducesDocument() throws {
        let doc = try kitchenSink()
        XCTAssertEqual(doc.toJSON(), Markdown.parse(Markdown.export(doc)).toJSON())
    }

    func testPageReferencesAreExtracted() throws {
        XCTAssertEqual(["0b0c6a52-0d6f-4a37-9b67-3f0f1c2b1a10"], try kitchenSink().pageReferences())
    }

    func testInvalidContentIsRejected() {
        let cases = [
            "{\"version\":2,\"blocks\":[]}",
            "{\"version\":1}",
            "{\"version\":1,\"blocks\":[{\"type\":\"video\",\"spans\":[]}]}",
            "{\"version\":1,\"blocks\":[{\"type\":\"heading\",\"level\":7,\"spans\":[]}]}",
            "{\"version\":1,\"blocks\":[],\"extra\":true}",
            "{\"version\":1,\"blocks\":[{\"type\":\"paragraph\",\"spans\":[{\"text\":1}]}]}",
            "{\"version\":1,\"blocks\":[{\"type\":\"todo\",\"checked\":1,\"spans\":[]}]}",
            "not json",
        ]
        for json in cases {
            assertThrowsKind(.documentFormat) { _ = try PageDocument.parse(json) }
        }
    }

    func testUnsafeLinksAreStripped() throws {
        for url in ["javascript:alert(1)", "file:///C:/Windows/system.ini", "slothboss://page/not-a-uuid", "data:text/html,hi"] {
            let doc = try PageDocument.parse("{\"version\":1,\"blocks\":[{\"type\":\"paragraph\",\"spans\":[{\"text\":\"x\",\"link\":\"\(url)\"}]}]}")
            XCTAssertNil(doc.blocks[0].spans[0].link, url)
        }
        XCTAssertTrue(Links.isAllowed("https://ollama.com/"))
        XCTAssertTrue(Links.isAllowed("mailto:someone@example.com"))
        XCTAssertTrue(Links.isAllowed("SLOTHBOSS://page/0b0c6a52-0d6f-4a37-9b67-3f0f1c2b1a10"))
    }

    func testNormalizeMergesAdjacentSpansAndDropsEmptyOnes() {
        let doc = PageDocument([Block(.paragraph, [TextSpan("a"), TextSpan(""), TextSpan("b"), TextSpan("c", bold: true)])]).normalized()
        XCTAssertEqual(2, doc.blocks[0].spans.count)
        XCTAssertEqual("ab", doc.blocks[0].spans[0].text)
    }

    func testNormalizeFlattensNewlinesOutsideCodeAndStripsCodeFormatting() {
        let doc = PageDocument([
            Block(.paragraph, [TextSpan("a\r\nb\nc")]),
            Block(.code, [TextSpan("x\ny", bold: true, link: "https://example.com")]),
            Block(.heading, [TextSpan("h")], level: 9),
        ]).normalized()
        XCTAssertEqual("a b c", doc.blocks[0].text)
        XCTAssertEqual(TextSpan("x\ny"), doc.blocks[1].spans[0])
        XCTAssertEqual(3, doc.blocks[2].level)
    }

    func testMarkdownParserHandlesDraftedNoteShapes() {
        let doc = Markdown.parse("## Findings\nFirst line\ncontinues here.\n\n- [ ] Check *this*\n3) Third\n> quoted\n[bad](javascript:x)")
        XCTAssertEqual(.heading, doc.blocks[0].type)
        XCTAssertEqual(2, doc.blocks[0].level)
        XCTAssertEqual("First line continues here.", doc.blocks[1].text)
        XCTAssertEqual(.todo, doc.blocks[2].type)
        XCTAssertTrue(doc.blocks[2].spans[1].italic)
        XCTAssertEqual(.numbered, doc.blocks[3].type)
        XCTAssertEqual(.quote, doc.blocks[4].type)
        XCTAssertNil(doc.blocks[5].spans[0].link)
    }

    func testJSONSerializerEscapesControlCharactersAndKeepsUnicode() throws {
        let doc = PageDocument([Block.paragraph("tab\there \"quoted\" back\\slash é 🦥 \u{2}")])
        let json = doc.toJSON()
        XCTAssertTrue(json.contains("tab\\there \\\"quoted\\\" back\\\\slash é 🦥 \\u0002"))
        XCTAssertEqual(doc.normalized(), try PageDocument.parse(json))
    }

    func testJSONParserDistinguishesIntegersAndRejectsGarbage() throws {
        XCTAssertEqual(JSON.int(1), try JSON.parse("1"))
        XCTAssertEqual(JSON.double(1.5), try JSON.parse("1.5"))
        XCTAssertEqual(JSON.string("😀"), try JSON.parse("\"\\ud83d\\ude00\""))
        for bad in ["", "{", "[1,]", "{\"a\":1,}", "01", "{\"a\":1,\"a\":2}", "tru", "\"\u{1}\"", "1 2"] {
            XCTAssertThrowsError(try JSON.parse(bad), bad)
        }
    }
}
