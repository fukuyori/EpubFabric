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
    public void 中間帯の長い数字列は網点ゴミとして破棄される()
    {
        // 実データ（科学202601）で網点模様が「05630900060000009000960000000009」のような
        // 数字列として誤認識された。数字はWordCharRatioを通過するため専用ルールで弾く。
        var filter = new OcrLineFilter();
        var result = filter.Filter([OcrLine("05630900060000009000960000000009", 0.75)]);

        Assert.Empty(result.Lines);
        Assert.Equal(1, result.DroppedCount);
    }

    [Fact]
    public void 区切りを含む数値やISBNは残る()
    {
        var filter = new OcrLineFilter();
        var result = filter.Filter([
            OcrLine("ISBN:978-4-8079-2002-0", 0.75),
            OcrLine("TEL:03-3946-5311 〒112-0011", 0.75),
            OcrLine("2023年7月時点の情報です。", 0.75),
        ]);

        Assert.Equal(3, result.Lines.Count);
        Assert.Equal(0, result.DroppedCount);
    }

    [Fact]
    public void 高信頼なら長い数字列でも残る()
    {
        // 本物の数表・シリアル番号を高信頼で読めているケースまで消さない。
        var filter = new OcrLineFilter();
        var result = filter.Filter([OcrLine("12345678901234567890", 0.95)]);

        Assert.Single(result.Lines);
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
