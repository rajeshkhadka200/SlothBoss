using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using SlothBoss.Core.Documents;
using static SlothBoss.App.Editor.RichTextConverter;

namespace SlothBoss.App.Editor;

/// <summary>Formatting operations on a RichEditBox, expressed in SlothBoss block semantics.</summary>
public sealed class EditorCommands(RichEditBox box, string bodyFont)
{
    private RichEditTextDocument Doc => box.Document;
    public string BodyFont => bodyFont;

    public void ToggleBold() => Doc.Selection.CharacterFormat.Bold = FormatEffect.Toggle;
    public void ToggleItalic() => Doc.Selection.CharacterFormat.Italic = FormatEffect.Toggle;

    public void ToggleInlineCode()
    {
        var sel = Doc.Selection;
        sel.CharacterFormat.Name = IsMono(sel.CharacterFormat.Name) ? bodyFont : MonoFont;
    }

    /// <summary>Paragraph starts (ascending) for every paragraph touched by the selection.</summary>
    private List<int> SelectedParagraphStarts()
    {
        var sel = Doc.Selection;
        var starts = new List<int>();
        var pos = sel.StartPosition;
        var end = Math.Max(sel.EndPosition, sel.StartPosition);
        while (true)
        {
            var p = Doc.GetRange(pos, pos);
            p.Expand(TextRangeUnit.Paragraph);
            if (starts.Count == 0 || starts[^1] != p.StartPosition) starts.Add(p.StartPosition);
            if (p.EndPosition > end || p.EndPosition <= pos || p.EndPosition >= Doc.GetRange(0, int.MaxValue).EndPosition) break;
            pos = p.EndPosition;
            if (pos >= end) break;
        }
        return starts;
    }

    public BlockType CurrentBlockType(out int level)
    {
        var block = ReadParagraphAt(Doc, Doc.Selection.StartPosition);
        level = block.Level;
        return block.Type;
    }

    /// <summary>Converts the selected paragraphs to a block type; applying the current type again reverts to a paragraph.</summary>
    public void SetBlock(BlockType type, int level = 0)
    {
        var current = CurrentBlockType(out var currentLevel);
        if (current == type && (type != BlockType.Heading || currentLevel == level)) type = BlockType.Paragraph;

        var starts = SelectedParagraphStarts();
        // Work from the last paragraph backwards so prefix edits don't shift earlier positions.
        for (var i = starts.Count - 1; i >= 0; i--)
        {
            var para = Doc.GetRange(starts[i], starts[i]);
            para.Expand(TextRangeUnit.Paragraph);
            RemoveTodoPrefix(para.StartPosition);
            para = Doc.GetRange(starts[i], starts[i]);
            para.Expand(TextRangeUnit.Paragraph);

            var cf = para.CharacterFormat;
            if (type == BlockType.Heading)
            {
                cf.Size = HeadingSize(level);
                cf.Bold = FormatEffect.On;
                cf.Name = bodyFont;
            }
            else
            {
                if (current == BlockType.Heading || cf.Size != BodySize) { cf.Size = BodySize; if (current == BlockType.Heading) cf.Bold = FormatEffect.Off; }
                if (current == BlockType.Code && type != BlockType.Code) cf.Name = bodyFont;
            }
            if (type == BlockType.Quote) cf.Italic = FormatEffect.On;
            else if (current == BlockType.Quote) cf.Italic = FormatEffect.Off;
            ApplyBlockParagraph(para, type, bodyFont);
            if (type == BlockType.Todo)
            {
                var at = Doc.GetRange(para.StartPosition, para.StartPosition);
                at.SetText(TextSetOptions.None, UncheckedBox + " ");
                ApplyChar(at, bodyFont, BodySize, false, false, false);
            }
        }

        // A collapsed selection has its own insertion format; align it so the next typed text matches the block.
        var caret = Doc.Selection;
        if (caret.Length == 0)
        {
            var f = caret.CharacterFormat;
            f.Size = type == BlockType.Heading ? HeadingSize(level) : BodySize;
            f.Bold = type == BlockType.Heading ? FormatEffect.On : FormatEffect.Off;
            f.Name = type == BlockType.Code ? MonoFont : bodyFont;
            f.Italic = type == BlockType.Quote ? FormatEffect.On : FormatEffect.Off;
        }
    }

    private void RemoveTodoPrefix(int paraStart)
    {
        var head = Doc.GetRange(paraStart, paraStart + 2);
        head.GetText(TextGetOptions.NoHidden, out var text);
        if (text.Length == 2 && (text[0] == UncheckedBox || text[0] == CheckedBox) && text[1] == ' ')
            head.SetText(TextSetOptions.None, "");
    }

    /// <summary>Toggles ☐/☑ on the current checklist item. Returns false if the caret isn't in one.</summary>
    public bool ToggleChecked(int? position = null)
    {
        var para = Doc.GetRange(position ?? Doc.Selection.StartPosition, position ?? Doc.Selection.StartPosition);
        para.Expand(TextRangeUnit.Paragraph);
        var box1 = Doc.GetRange(para.StartPosition, para.StartPosition + 1);
        box1.GetText(TextGetOptions.NoHidden, out var c);
        if (c.Length != 1 || (c[0] != UncheckedBox && c[0] != CheckedBox)) return false;
        box1.SetText(TextSetOptions.None, (c[0] == UncheckedBox ? CheckedBox : UncheckedBox).ToString());
        ApplyChar(box1, bodyFont, BodySize, false, false, false);
        return true;
    }

    /// <summary>Links the selection (or inserts <paramref name="textIfEmpty"/> as linked text).</summary>
    public void ApplyLink(string url, string textIfEmpty)
    {
        var sel = Doc.Selection;
        if (sel.Length == 0)
        {
            sel.SetText(TextSetOptions.None, textIfEmpty);
        }
        sel.Link = "\"" + url + "\"";
        sel.Collapse(false);
    }

    public string? LinkAtCaret()
    {
        var link = Doc.Selection.Link?.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(link) ? null : link;
    }

    /// <summary>Plain-text paste: keeps the surrounding block formatting and drops foreign styles.</summary>
    public void PastePlain(string text)
    {
        var normalized = text.Replace("\r\n", "\r").Replace('\n', '\r');
        var sel = Doc.Selection;
        sel.SetText(TextSetOptions.None, normalized);
        sel.Collapse(false);
    }

    /// <summary>
    /// Markdown-style shortcuts typed at the start of a paragraph ("# ", "- ", "1. ", "[] ", "> ", "```").
    /// Returns true if a conversion happened.
    /// </summary>
    public bool TryMarkdownShortcut()
    {
        var sel = Doc.Selection;
        if (sel.Length != 0) return false;
        var para = Doc.GetRange(sel.StartPosition, sel.StartPosition);
        para.Expand(TextRangeUnit.Paragraph);
        var before = Doc.GetRange(para.StartPosition, sel.StartPosition);
        before.GetText(TextGetOptions.NoHidden, out var prefix);
        if (prefix.Length is < 2 or > 4) return false;

        (BlockType Type, int Level)? target = prefix switch
        {
            "# " => (BlockType.Heading, 1),
            "## " => (BlockType.Heading, 2),
            "### " => (BlockType.Heading, 3),
            "- " or "* " => (BlockType.Bulleted, 0),
            "1. " => (BlockType.Numbered, 0),
            "[] " or "[ ] " => (BlockType.Todo, 0),
            "> " => (BlockType.Quote, 0),
            "```" => (BlockType.Code, 0),
            _ => null,
        };
        if (target is null) return false;
        var current = ReadParagraphAt(Doc, para.StartPosition).Type;
        if (current is BlockType.Code or BlockType.Todo || current == target.Value.Type) return false;

        before.SetText(TextSetOptions.None, "");
        sel.SetRange(para.StartPosition, para.StartPosition);
        SetBlock(target.Value.Type, target.Value.Level);
        // Put the caret after a newly inserted checkbox.
        if (target.Value.Type == BlockType.Todo) sel.SetRange(para.StartPosition + 2, para.StartPosition + 2);
        return true;
    }

    /// <summary>Enter key behavior for structured blocks. Returns true if handled.</summary>
    public bool HandleEnter()
    {
        var sel = Doc.Selection;
        var para = Doc.GetRange(sel.StartPosition, sel.StartPosition);
        para.Expand(TextRangeUnit.Paragraph);
        var block = ReadParagraphAt(Doc, sel.StartPosition);
        var atEnd = sel.EndPosition >= para.EndPosition - 1;

        switch (block.Type)
        {
            case BlockType.Todo when block.Text.Length == 0:
            case BlockType.Bulleted when block.Text.Length == 0:
            case BlockType.Numbered when block.Text.Length == 0:
            case BlockType.Quote when block.Text.Length == 0:
                SetBlock(block.Type); // toggling the same type exits the list
                return true;
            case BlockType.Todo:
                sel.TypeText("\r");
                var at = Doc.GetRange(sel.StartPosition, sel.StartPosition);
                at.SetText(TextSetOptions.None, UncheckedBox + " ");
                ApplyChar(at, bodyFont, BodySize, false, false, false);
                sel.SetRange(at.EndPosition, at.EndPosition);
                return true;
            case BlockType.Heading when atEnd:
                sel.TypeText("\r");
                var next = Doc.GetRange(sel.StartPosition, sel.StartPosition);
                next.Expand(TextRangeUnit.Paragraph);
                ApplyChar(next, bodyFont, BodySize, false, false, false);
                ResetParagraph(next);
                sel.CharacterFormat.Size = BodySize;
                sel.CharacterFormat.Bold = FormatEffect.Off;
                return true;
            default:
                return false;
        }
    }
}
