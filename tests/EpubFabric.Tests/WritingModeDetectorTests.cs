using EpubFabric.Core.Models;
using EpubFabric.Layout;

namespace EpubFabric.Tests;

public sealed class WritingModeDetectorTests
{
    [Fact]
    public void 縦長の本文行が支配的なページは縦書きと判定する()
    {
        var lines = new[]
        {
            new TextLine(new BoundingBox(0.70, 0.10, 0.03, 0.40), "縦書きの本文の行がここにある", 0.9),
            new TextLine(new BoundingBox(0.65, 0.10, 0.03, 0.40), "二行目の縦書き本文もここにある", 0.9),
            new TextLine(new BoundingBox(0.60, 0.10, 0.03, 0.40), "三行目の縦書き本文が続いている", 0.9),
            // ノンブル・柱（横長だが短い）
            new TextLine(new BoundingBox(0.45, 0.95, 0.05, 0.02), "12", 0.9),
            new TextLine(new BoundingBox(0.10, 0.02, 0.15, 0.02), "地理 2026", 0.9),
        };

        Assert.Equal(WritingMode.Vertical, WritingModeDetector.DetectPageMode(lines));
    }

    [Fact]
    public void 横長の本文行が支配的なページは横書きと判定する()
    {
        var lines = new[]
        {
            new TextLine(new BoundingBox(0.10, 0.10, 0.60, 0.03), "横書きの本文の行がここにある", 0.9),
            new TextLine(new BoundingBox(0.10, 0.15, 0.60, 0.03), "二行目の横書き本文もここにある", 0.9),
            // 図中の縦ラベル（短い）
            new TextLine(new BoundingBox(0.85, 0.40, 0.02, 0.10), "図1", 0.9),
        };

        Assert.Equal(WritingMode.Horizontal, WritingModeDetector.DetectPageMode(lines));
    }

    [Fact]
    public void 行がないページは横書き扱い()
    {
        Assert.Equal(WritingMode.Horizontal, WritingModeDetector.DetectPageMode([]));
    }

    [Fact]
    public void 書籍全体は縦書きページの多数決で決まる()
    {
        Assert.Equal(
            WritingMode.Vertical,
            WritingModeDetector.DetectDocumentMode([WritingMode.Vertical, WritingMode.Vertical, WritingMode.Horizontal]));
        Assert.Equal(
            WritingMode.Horizontal,
            WritingModeDetector.DetectDocumentMode([WritingMode.Vertical, WritingMode.Horizontal, WritingMode.Horizontal]));
        Assert.Equal(WritingMode.Horizontal, WritingModeDetector.DetectDocumentMode([]));
    }
}
