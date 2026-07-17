using EpubFabric.Core.Models;
using EpubFabric.Layout;

namespace EpubFabric.Tests;

public class HeuristicLayoutAnalyzerTests
{
    private readonly HeuristicLayoutAnalyzer _analyzer = new();

    [Fact]
    public void AnalyzePage_LargeLineAboveBody_IsClassifiedAsHeading()
    {
        var lines = new List<TextLine>
        {
            new(new BoundingBox(0.1, 0.10, 0.4, 0.05), "見出し", 0.95), // 本文の約2.2倍の高さ
            new(new BoundingBox(0.1, 0.20, 0.6, 0.0225), "本文1行目です。", 0.95),
            new(new BoundingBox(0.1, 0.23, 0.6, 0.0225), "本文2行目です。", 0.95),
            new(new BoundingBox(0.1, 0.26, 0.6, 0.0225), "本文3行目です。", 0.95),
        };

        var blocks = _analyzer.AnalyzePage(pageNumber: 1, lines);

        var heading = blocks.Single(b => b.OcrText == "見出し");
        Assert.Equal(BlockType.ChapterTitle, heading.Type);
        Assert.Equal(1, heading.HeadingLevel);
        Assert.Equal(0, heading.ReadingOrder);

        Assert.All(blocks.Where(b => b.OcrText != "見出し"), b => Assert.Equal(BlockType.Body, b.Type));
    }

    [Fact]
    public void AnalyzePage_TwoColumnLayout_ReadsLeftColumnFullyBeforeRightColumn()
    {
        var lines = new List<TextLine>
        {
            // 左段（X: 0.05-0.40）を上から下へ
            new(new BoundingBox(0.05, 0.10, 0.35, 0.03), "左段1", 0.9),
            new(new BoundingBox(0.05, 0.14, 0.35, 0.03), "左段2", 0.9),
            new(new BoundingBox(0.05, 0.18, 0.35, 0.03), "左段3", 0.9),
            // 右段（X: 0.55-0.90）を上から下へ
            new(new BoundingBox(0.55, 0.10, 0.35, 0.03), "右段1", 0.9),
            new(new BoundingBox(0.55, 0.14, 0.35, 0.03), "右段2", 0.9),
            new(new BoundingBox(0.55, 0.18, 0.35, 0.03), "右段3", 0.9),
        };

        var blocks = _analyzer.AnalyzePage(pageNumber: 1, lines);
        var orderedTexts = blocks.OrderBy(b => b.ReadingOrder).Select(b => b.OcrText).ToList();

        Assert.Equal(["左段1", "左段2", "左段3", "右段1", "右段2", "右段3"], orderedTexts);
    }

    [Fact]
    public void AnalyzePage_ShortLineNearBottomAllDigits_IsClassifiedAsPageNumberAndExcluded()
    {
        var lines = new List<TextLine>
        {
            new(new BoundingBox(0.1, 0.20, 0.6, 0.03), "本文です。", 0.9),
            new(new BoundingBox(0.48, 0.96, 0.04, 0.02), "8", 0.9),
        };

        var blocks = _analyzer.AnalyzePage(pageNumber: 1, lines);

        var pageNumberBlock = blocks.Single(b => b.OcrText == "8");
        Assert.Equal(BlockType.PageNumber, pageNumberBlock.Type);
        Assert.True(pageNumberBlock.IsExcluded);
    }

    [Fact]
    public void AnalyzePage_NoLines_ReturnsEmpty()
    {
        var blocks = _analyzer.AnalyzePage(pageNumber: 1, []);

        Assert.Empty(blocks);
    }
}
