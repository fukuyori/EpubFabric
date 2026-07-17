using EpubFabric.Core.Models;
using EpubFabric.Layout;

namespace EpubFabric.Tests;

public class TextLayerBlockBuilderTests
{
    [Fact]
    public void Build_PreservesEveryTextLineWithItsBoundsAndSource()
    {
        var lowerLine = new TextLine(
            new BoundingBox(0.1, 0.5, 0.6, 0.04),
            "下の行",
            0.72,
            TextSourceKind.Ocr);
        var upperLine = new TextLine(
            new BoundingBox(0.2, 0.1, 0.5, 0.03),
            "上の行",
            0.98,
            TextSourceKind.Ocr);

        var blocks = new TextLayerBlockBuilder().Build(3, [lowerLine, upperLine]);

        Assert.Equal(2, blocks.Count);
        Assert.Equal("上の行", blocks[0].OcrText);
        Assert.Equal(upperLine.Bounds, blocks[0].Bounds);
        Assert.Equal(TextSourceKind.Ocr, blocks[0].TextSource);
        Assert.False(blocks[0].RequiresReview);
        Assert.Equal("下の行", blocks[1].OcrText);
        Assert.True(blocks[1].RequiresReview);
        Assert.Equal([0, 1], blocks.Select(block => block.ReadingOrder));
    }

    [Fact]
    public void Build_DropsOnlyEmptyLines()
    {
        var lines = new[]
        {
            new TextLine(new BoundingBox(0, 0, 1, 0.1), "本文", 1, TextSourceKind.PdfTextLayer),
            new TextLine(new BoundingBox(0, 0.2, 1, 0.1), " ", 1, TextSourceKind.PdfTextLayer),
        };

        var block = Assert.Single(new TextLayerBlockBuilder().Build(1, lines));

        Assert.Equal("本文", block.OcrText);
        Assert.Equal(TextSourceKind.PdfTextLayer, block.TextSource);
    }
}
