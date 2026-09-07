using EpubFabric.Core.Models;
using EpubFabric.Layout;

namespace EpubFabric.Tests;

public class ExternalLineTypeClassificationTests
{
    [Fact]
    public void AnalyzePage_PreservesExternalReadingOrder()
    {
        var lines = new[]
        {
            new TextLine(
                new BoundingBox(0.2, 0.5, 0.03, 0.3),
                "二番目",
                0.95,
                TextSourceKind.NdlOcr,
                SourceReadingOrder: 1,
                SourceType: "本文"),
            new TextLine(
                new BoundingBox(0.8, 0.1, 0.03, 0.3),
                "一番目",
                0.95,
                TextSourceKind.NdlOcr,
                SourceReadingOrder: 0,
                SourceType: "本文"),
        };

        var blocks = new HeuristicLayoutAnalyzer().AnalyzePage(
            1,
            lines,
            writingMode: WritingMode.Vertical);

        Assert.Equal(["一番目", "二番目"], blocks.OrderBy(block => block.ReadingOrder).Select(block => block.OcrText));
    }

    [Fact]
    public void AnalyzePage_InsertsFigureBeforeExternalCaptionAndLinksIt()
    {
        var lines = new[]
        {
            new TextLine(
                new BoundingBox(0.2, 0.52, 0.6, 0.03),
                "図1 説明",
                0.95,
                TextSourceKind.NdlOcr,
                SourceReadingOrder: 0,
                SourceType: "キャプション"),
            new TextLine(
                new BoundingBox(0.8, 0.6, 0.03, 0.3),
                "本文",
                0.95,
                TextSourceKind.NdlOcr,
                SourceReadingOrder: 1,
                SourceType: "本文"),
        };
        var regions = new[]
        {
            new NonTextRegion(new BoundingBox(0.1, 0.1, 0.8, 0.4), NonTextRegionKind.Figure),
        };

        var blocks = new HeuristicLayoutAnalyzer().AnalyzePage(
            1,
            lines,
            regions,
            WritingMode.Vertical);

        Assert.Equal([BlockType.Figure, BlockType.Caption, BlockType.Body], blocks.Select(block => block.Type));
        Assert.Equal(blocks[0].Id, blocks[1].RelatedBlockId);
    }

    [Fact]
    public void AnalyzePage_DoesNotInsertFigureInMiddleOfSentence()
    {
        var lines = new[]
        {
            NdlLine("文の途中", 0.92, 0.1, 0, "本文"),
            new TextLine(
                new BoundingBox(0.2, 0.52, 0.6, 0.03),
                "図1 説明",
                0.95,
                TextSourceKind.NdlOcr,
                SourceReadingOrder: 1,
                SourceType: "キャプション"),
            NdlLine("から続いて完結する。", 0.7, 0.6, 2, "本文"),
            NdlLine("次の段落", 0.6, 0.6, 3, "本文"),
        };
        var regions = new[]
        {
            new NonTextRegion(new BoundingBox(0.1, 0.1, 0.8, 0.4), NonTextRegionKind.Figure),
        };

        var blocks = new HeuristicLayoutAnalyzer().AnalyzePage(
            1,
            lines,
            regions,
            WritingMode.Vertical);

        Assert.Equal(
            ["文の途中", "から続いて完結する。", "", "図1 説明", "次の段落"],
            blocks.Select(block => block.OcrText));
        Assert.Equal(BlockType.Figure, blocks[2].Type);
        Assert.Equal(blocks[2].Id, blocks[3].RelatedBlockId);
    }

    private static TextLine NdlLine(string text, double x, double y, int order, string sourceType) => new(
        new BoundingBox(x, y, 0.03, 0.3),
        text,
        0.95,
        TextSourceKind.NdlOcr,
        SourceReadingOrder: order,
        SourceType: sourceType);

    [Theory]
    [InlineData("キャプション", BlockType.Caption, false)]
    [InlineData("注", BlockType.Footnote, false)]
    [InlineData("柱", BlockType.Header, true)]
    [InlineData("ノンブル", BlockType.PageNumber, true)]
    public void AnalyzePage_UsesLimitedExternalLineTypeHints(
        string sourceType,
        BlockType expectedType,
        bool expectedExcluded)
    {
        var line = new TextLine(
            new BoundingBox(0.2, 0.2, 0.5, 0.03),
            "外部OCR行",
            0.95,
            TextSourceKind.NdlOcr,
            SourceType: sourceType);

        var block = Assert.Single(new HeuristicLayoutAnalyzer().AnalyzePage(1, [line]));

        Assert.Equal(expectedType, block.Type);
        Assert.Equal(expectedExcluded, block.IsExcluded);
        Assert.Equal(TextSourceKind.NdlOcr, block.TextSource);
    }
}
