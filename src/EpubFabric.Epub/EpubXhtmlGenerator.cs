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

    public XDocument GenerateChapter(
        DocumentChapter chapter,
        IReadOnlyDictionary<string, PageBlock> blocksById,
        string language = "ja",
        WritingMode writingMode = WritingMode.Horizontal)
    {
        var chapterTitle = XmlTextSanitizer.Sanitize(chapter.Title);
        var titleTag = $"h{Math.Clamp(chapter.HeadingLevel, 1, 6)}";
        var chapterBlocks = chapter.BlockIds
            .Select(id => blocksById.GetValueOrDefault(id))
            .Where(block => block is not null)
            .Cast<PageBlock>()
            .ToList();
        var leadingMetadata = chapterBlocks
            .TakeWhile(block => block.Type is BlockType.Kicker or BlockType.Author or BlockType.Affiliation)
            .ToList();

        var header = new XElement(Xhtml + "header", new XAttribute("class", "article-header"));
        foreach (var kicker in leadingMetadata.Where(block => block.Type == BlockType.Kicker))
        {
            header.Add(CreateElement(kicker, XmlTextSanitizer.Sanitize(kicker.CorrectedText ?? kicker.OcrText)));
        }

        header.Add(new XElement(Xhtml + titleTag, chapterTitle));

        foreach (var metadata in leadingMetadata.Where(block => block.Type is BlockType.Author or BlockType.Affiliation))
        {
            header.Add(CreateElement(metadata, XmlTextSanitizer.Sanitize(metadata.CorrectedText ?? metadata.OcrText)));
        }

        var article = new XElement(Xhtml + "article", header);
        var contentIds = chapter.BlockIds.Skip(leadingMetadata.Count).ToList();

        foreach (var element in GenerateBlockElements(contentIds, blocksById))
        {
            article.Add(element);
        }

        var body = new XElement(Xhtml + "body", new XAttribute("class", "publication"), article);

        var html = new XElement(
            Xhtml + "html",
            new XAttribute(XNamespace.Xmlns + "epub", EpubOps),
            new XAttribute(XNamespace.Xml + "lang", language),
            new XElement(
                Xhtml + "head",
                new XElement(Xhtml + "title", chapterTitle),
                new XElement(
                    Xhtml + "link",
                    new XAttribute("rel", "stylesheet"),
                    new XAttribute("type", "text/css"),
                    new XAttribute("href", "../styles/book.css"))),
            body);

        if (writingMode == WritingMode.Vertical)
        {
            html.SetAttributeValue("class", "vertical");
        }

        return new XDocument(new XDeclaration("1.0", "UTF-8", null), html);
    }

    /// <summary>
    /// ブロックID列をXHTML要素列へ変換する。章の本文生成と、評価レポートの
    /// ページ単位断片生成の両方から使う共通経路。
    /// </summary>
    public List<XElement> GenerateBlockElements(IReadOnlyList<string> blockIds, IReadOnlyDictionary<string, PageBlock> blocksById)
    {
        var elements = new List<XElement>();

        // 図に関連付けられたキャプションは <figcaption> として図の中に描画するため、
        // 本文側では個別のブロックとして重複描画しないよう除外する。
        var captionIdsByFigureId = blockIds
            .Select(id => blocksById.GetValueOrDefault(id))
            .Where(b => b is { Type: BlockType.Caption, RelatedBlockId: not null })
            .GroupBy(b => b!.RelatedBlockId!)
            .ToDictionary(g => g.Key, g => g.Select(b => b!.Id).ToList());
        var consumedCaptionIds = captionIdsByFigureId.Values.SelectMany(ids => ids).ToHashSet();

        foreach (var blockId in blockIds)
        {
            if (consumedCaptionIds.Contains(blockId) || !blocksById.TryGetValue(blockId, out var block))
            {
                continue;
            }

            if (block.Type == BlockType.Figure)
            {
                var captions = captionIdsByFigureId.GetValueOrDefault(block.Id, [])
                    .Select(id => blocksById[id])
                    .ToList();
                elements.Add(CreateFigureElement(block, captions));
                continue;
            }

            var text = XmlTextSanitizer.Sanitize(block.CorrectedText ?? block.OcrText);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            elements.Add(CreateElement(block, text));
        }

        return elements;
    }

    private static XElement CreateFigureElement(PageBlock figureBlock, IReadOnlyList<PageBlock> captions)
    {
        var figure = new XElement(Xhtml + "figure");
        var altText = captions.Count > 0
            ? XmlTextSanitizer.Sanitize(captions[0].CorrectedText ?? captions[0].OcrText)
            : "図";

        if (figureBlock.ExtractedImagePath is not null)
        {
            var fileName = Path.GetFileName(figureBlock.ExtractedImagePath);
            figure.Add(new XElement(
                Xhtml + "img",
                new XAttribute("src", $"../images/{fileName}"),
                new XAttribute("alt", altText)));
        }

        // figure要素の子にできるfigcaptionは1つまでのため、複数行にわたるキャプションは
        // 1つにまとめる（各行はOCRの行検出単位であり、文としては連続している）。
        var captionText = XmlTextSanitizer.Sanitize(string.Join(
            "",
            captions.Select(c => c.CorrectedText ?? c.OcrText).Where(t => !string.IsNullOrWhiteSpace(t))));
        if (!string.IsNullOrWhiteSpace(captionText))
        {
            figure.Add(new XElement(Xhtml + "figcaption", captionText));
        }

        return figure;
    }

    private static XElement CreateElement(PageBlock block, string text) => block.Type switch
    {
        // 見出しには目次（nav.xhtml）からアンカーで参照できるようidを付ける。
        BlockType.ChapterTitle => new XElement(Xhtml + "h1", new XAttribute("id", block.Id), text),
        BlockType.SectionHeading => new XElement(Xhtml + "h2", new XAttribute("id", block.Id), text),
        BlockType.Subheading => new XElement(Xhtml + "h3", new XAttribute("id", block.Id), text),
        BlockType.Caption => new XElement(Xhtml + "p", new XAttribute("class", "caption"), text),
        BlockType.Kicker => new XElement(Xhtml + "p", new XAttribute("class", "kicker"), text),
        BlockType.Author => new XElement(Xhtml + "p", new XAttribute("class", "byline"), text),
        BlockType.Affiliation => new XElement(Xhtml + "p", new XAttribute("class", "affiliation"), text),
        BlockType.Abstract => new XElement(
            Xhtml + "section",
            new XAttribute("class", "abstract"),
            new XAttribute(EpubOps + "type", "abstract"),
            new XElement(Xhtml + "h2", "要旨"),
            new XElement(Xhtml + "p", text)),
        BlockType.Aside => new XElement(Xhtml + "aside", new XAttribute(EpubOps + "type", "sidebar"), new XElement(Xhtml + "p", text)),
        BlockType.PullQuote => new XElement(Xhtml + "blockquote", new XElement(Xhtml + "p", text)),
        BlockType.Footnote => new XElement(Xhtml + "aside", new XAttribute(EpubOps + "type", "footnote"), new XElement(Xhtml + "p", text)),
        BlockType.Code => new XElement(Xhtml + "pre", new XElement(Xhtml + "code", text)),
        _ => new XElement(Xhtml + "p", text),
    };
}
