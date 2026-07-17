using System.Xml.Linq;
using EpubFabric.Core.Models;
using EpubFabric.Document;

namespace EpubFabric.Epub;

/// <summary>
/// 12.3 XHTML変換規則に基づき、章をXHTMLへ変換する。
/// </summary>
public sealed class EpubXhtmlGenerator
{
    private static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";
    private static readonly XNamespace EpubOps = "http://www.idpf.org/2007/ops";

    public XDocument GenerateChapter(DocumentChapter chapter, IReadOnlyDictionary<string, PageBlock> blocksById)
    {
        var body = new XElement(Xhtml + "body", new XElement(Xhtml + "h1", chapter.Title));

        foreach (var blockId in chapter.BlockIds)
        {
            if (!blocksById.TryGetValue(blockId, out var block))
            {
                continue;
            }

            var text = block.CorrectedText ?? block.OcrText;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            body.Add(CreateElement(block.Type, text));
        }

        var html = new XElement(
            Xhtml + "html",
            new XAttribute(XNamespace.Xmlns + "epub", EpubOps),
            new XElement(
                Xhtml + "head",
                new XElement(Xhtml + "title", chapter.Title),
                new XElement(
                    Xhtml + "link",
                    new XAttribute("rel", "stylesheet"),
                    new XAttribute("type", "text/css"),
                    new XAttribute("href", "../styles/book.css"))),
            body);

        return new XDocument(new XDeclaration("1.0", "UTF-8", null), html);
    }

    private static XElement CreateElement(BlockType type, string text) => type switch
    {
        BlockType.ChapterTitle => new XElement(Xhtml + "h1", text),
        BlockType.SectionHeading => new XElement(Xhtml + "h2", text),
        BlockType.Subheading => new XElement(Xhtml + "h3", text),
        BlockType.Caption => new XElement(Xhtml + "p", new XAttribute("class", "caption"), text),
        BlockType.Aside => new XElement(Xhtml + "aside", new XAttribute(EpubOps + "type", "sidebar"), new XElement(Xhtml + "p", text)),
        BlockType.PullQuote => new XElement(Xhtml + "blockquote", new XElement(Xhtml + "p", text)),
        BlockType.Footnote => new XElement(Xhtml + "aside", new XAttribute(EpubOps + "type", "footnote"), new XElement(Xhtml + "p", text)),
        _ => new XElement(Xhtml + "p", text),
    };
}
