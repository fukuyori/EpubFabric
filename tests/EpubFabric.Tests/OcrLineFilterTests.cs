using EpubFabric.Core.Models;
using EpubFabric.Ocr;

namespace EpubFabric.Tests;

public sealed class OcrLineFilterTests
{
    private static readonly BoundingBox Bounds = new(0.1, 0.1, 0.5, 0.05);

    private static TextLine OcrLine(string text, double confidence) =>
        new(Bounds, text, confidence, TextSourceKind.Ocr);

    [Fact]
    public void 足切り未満の行は破棄される()
    {
        var filter = new OcrLineFilter();
        var result = filter.Filter([OcrLine("Lin• 1v-n-t:", 0.45)]);

        Assert.Empty(result.Lines);
        Assert.Equal(1, result.DroppedCount);
    }

    [Fact]
    public void 高信頼の行は記号だらけでも残る()
    {
        var filter = new OcrLineFilter();
        var result = filter.Filter([OcrLine("……——……", 0.95)]);

        Assert.Single(result.Lines);
        Assert.Equal(0, result.DroppedCount);
    }

    [Fact]
    public void 中間帯の記号だらけの行は破棄される()
    {
        var filter = new OcrLineFilter();
        var result = filter.Filter([OcrLine("•- :• …-", 0.75)]);

        Assert.Empty(result.Lines);
        Assert.Equal(1, result.DroppedCount);
    }

    [Fact]
    public void 中間帯でも通常の和文は残る()
    {
        var filter = new OcrLineFilter();
        var result = filter.Filter([OcrLine("第3章 環境構築の手順（前編）", 0.75)]);

        Assert.Single(result.Lines);
        Assert.Equal(0, result.DroppedCount);
    }

    [Fact]
    public void PDFテキスト層由来の行は信頼度にかかわらず残る()
    {
        var filter = new OcrLineFilter();
        var line = new TextLine(Bounds, "•••", 0.1, TextSourceKind.PdfTextLayer);
        var result = filter.Filter([line]);

        Assert.Single(result.Lines);
        Assert.Equal(0, result.DroppedCount);
    }

    [Fact]
    public void しきい値は調整できる()
    {
        var strict = new OcrLineFilter(minimumConfidence: 0.9);
        var result = strict.Filter([OcrLine("第3章 環境構築", 0.85)]);

        Assert.Empty(result.Lines);
        Assert.Equal(1, result.DroppedCount);
    }
}
