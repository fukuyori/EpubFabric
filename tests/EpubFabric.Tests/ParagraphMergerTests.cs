using EpubFabric.Core.Models;
using EpubFabric.Layout;

namespace EpubFabric.Tests;

public class ParagraphMergerTests
{
    private static PageBlock Line(string id, double y, string text, BlockType type = BlockType.Body, double x = 0.1, double width = 0.8, double height = 0.02, int readingOrder = 0) => new()
    {
        Id = id,
        PageNumber = 1,
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
}
