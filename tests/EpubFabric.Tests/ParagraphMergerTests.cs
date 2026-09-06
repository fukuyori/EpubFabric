using EpubFabric.Core.Models;
using EpubFabric.Layout;

namespace EpubFabric.Tests;

public class ParagraphMergerTests
{
    private static PageBlock Line(string id, double y, string text, BlockType type = BlockType.Body, double x = 0.1, double width = 0.8, double height = 0.02, int readingOrder = 0, int pageNumber = 1) => new()
    {
        Id = id,
        PageNumber = pageNumber,
        Bounds = new BoundingBox(x, y, width, height),
        Type = type,
        OcrText = text,
        OcrConfidence = 0.95,
        ReadingOrder = readingOrder,
    };

    [Fact]
    public void Merge_JoinsConsecutiveBodyLinesIntoParagraph()
    {
        var blocks = new List<PageBlock>
        {
            Line("b1", 0.10, "一行目の本文で", readingOrder: 0),
            Line("b2", 0.125, "二行目に続く。", readingOrder: 1),
            Line("b3", 0.15, "Latin text", readingOrder: 2),
        };

        var merged = new ParagraphMerger().Merge(blocks);

        var paragraph = Assert.Single(merged);
        Assert.Equal("一行目の本文で二行目に続く。Latin text", paragraph.OcrText);
        Assert.Equal(0.10, paragraph.Bounds.Y, precision: 5);
        Assert.Equal(0.07, paragraph.Bounds.Height, precision: 5);
        Assert.Equal(0, paragraph.ReadingOrder);
    }

    [Fact]
    public void Merge_StartsNewParagraphOnLargeGapOrIndent()
    {
        var blocks = new List<PageBlock>
        {
            Line("b1", 0.10, "第一段落。", readingOrder: 0),
            // 行間の3倍の隙間 → 新しい段落。
            Line("b2", 0.19, "第二段落。", readingOrder: 1),
            // 字下げ → 新しい段落。
            Line("b3", 0.215, "　第三段落。", x: 0.14, width: 0.76, readingOrder: 2),
        };

        var merged = new ParagraphMerger().Merge(blocks);

        Assert.Equal(3, merged.Count);
        Assert.Equal([0, 1, 2], merged.Select(b => b.ReadingOrder));
    }

    [Fact]
    public void Merge_DoesNotMergeAcrossTypesColumnsOrHeadings()
    {
        var blocks = new List<PageBlock>
        {
            Line("b1", 0.10, "見出し", BlockType.SectionHeading, height: 0.03, readingOrder: 0),
            Line("b2", 0.14, "左段の本文", x: 0.05, width: 0.4, readingOrder: 1),
            // 同じ高さの右段 → 横に並んでいるので統合しない。
            Line("b3", 0.14, "右段の本文", x: 0.55, width: 0.4, readingOrder: 2),
            Line("b4", 0.18, "囲み記事", BlockType.Aside, readingOrder: 3),
        };

        var merged = new ParagraphMerger().Merge(blocks);

        Assert.Equal(4, merged.Count);
    }

    [Fact]
    public void Merge_DoesNotInsertSpaceAfterLineEndHyphen()
    {
        var blocks = new List<PageBlock>
        {
            Line("b1", 0.10, "hardware-", readingOrder: 0),
            Line("b2", 0.125, "independent standard", readingOrder: 1),
        };

        var paragraph = Assert.Single(new ParagraphMerger().Merge(blocks));

        Assert.Equal("hardware-independent standard", paragraph.OcrText);
    }

    [Fact]
    public void Merge_InsertsSpaceAfterCurlyClosingQuoteInEnglish()
    {
        var blocks = new List<PageBlock>
        {
            Line("b1", 0.10, "the words ‘culture’, ‘art’", readingOrder: 0),
            Line("b2", 0.125, "and ‘philosophy’ a lot.", readingOrder: 1),
        };

        var paragraph = Assert.Single(new ParagraphMerger().Merge(blocks));

        Assert.Equal("the words ‘culture’, ‘art’ and ‘philosophy’ a lot.", paragraph.OcrText);
    }

    [Fact]
    public void Merge_AllowsGlyphHeightVariationWithinSameFont()
    {
        var blocks = new List<PageBlock>
        {
            Line("b1", 0.10, "number-crunching", height: 0.012, readingOrder: 0),
            Line("b2", 0.1165, "behemoths and database back ends.", height: 0.0083, readingOrder: 1),
        };

        var paragraph = Assert.Single(new ParagraphMerger().Merge(blocks));

        Assert.Equal("number-crunching behemoths and database back ends.", paragraph.OcrText);
    }

    [Fact]
    public void Merge_JoinsConsecutiveHeadingLinesIntoOneHeading()
    {
        var blocks = new List<PageBlock>
        {
            Line("b1", 0.10, "複数行にまたがる", BlockType.Subheading, height: 0.03, readingOrder: 0),
            Line("b2", 0.135, "太字の小見出し", BlockType.Subheading, height: 0.03, readingOrder: 1),
        };

        var heading = Assert.Single(new ParagraphMerger().Merge(blocks));

        Assert.Equal(BlockType.Subheading, heading.Type);
        Assert.Equal("複数行にまたがる太字の小見出し", heading.OcrText);
    }

    [Fact]
    public void Merge_VerticalText_JoinsLinesFromRightToLeft()
    {
        var blocks = new List<PageBlock>
        {
            Line("b1", 0.10, "右側の縦書き本文", x: 0.70, width: 0.03, height: 0.50, readingOrder: 0),
            Line("b2", 0.10, "左隣の本文です。", x: 0.66, width: 0.03, height: 0.50, readingOrder: 1),
        };

        var paragraph = Assert.Single(new ParagraphMerger().Merge(blocks, WritingMode.Vertical));

        Assert.Equal("右側の縦書き本文左隣の本文です。", paragraph.OcrText);
        Assert.Equal(0.66, paragraph.Bounds.X, precision: 5);
        Assert.Equal(0.07, paragraph.Bounds.Width, precision: 5);
    }

    [Fact]
    public void MergeAcrossPages_JoinsLowercaseContinuationAtPageBoundary()
    {
        var previous = Line("p1-b1", 0.75, "This sentence continues", readingOrder: 0);
        previous.TextSource = TextSourceKind.PdfTextLayer;
        var next = Line("p2-b1", 0.10, "on the next page.", readingOrder: 0, pageNumber: 2);
        next.TextSource = TextSourceKind.PdfTextLayer;
        var pages = new[] { Page(1, previous), Page(2, next) };

        var count = new ParagraphMerger().MergeAcrossPages(pages);

        Assert.Equal(1, count);
        Assert.Equal("This sentence continues on the next page.", previous.OcrText);
        Assert.Empty(pages[1].Blocks);
    }

    [Fact]
    public void MergeAcrossPages_DoesNotJoinNewParagraphOrHeading()
    {
        var previous = Line("p1-b1", 0.75, "A complete paragraph.", readingOrder: 0);
        var next = Line("p2-b1", 0.10, "New paragraph", readingOrder: 0, pageNumber: 2);
        var pages = new[] { Page(1, previous), Page(2, next) };

        var count = new ParagraphMerger().MergeAcrossPages(pages);

        Assert.Equal(0, count);
        Assert.Single(pages[1].Blocks);
    }

    private static DocumentPage Page(int pageNumber, params PageBlock[] blocks)
    {
        var page = new DocumentPage
        {
            PageNumber = pageNumber,
            OriginalImagePath = $"page-{pageNumber}.png",
            ProcessedImagePath = $"page-{pageNumber}.png",
            PreviewImagePath = $"page-{pageNumber}.png",
        };
        page.Blocks.AddRange(blocks);
        return page;
    }
}
