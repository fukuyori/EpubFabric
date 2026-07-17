using EpubFabric.Core.Models;
using EpubFabric.Pdf;

namespace EpubFabric.Tests;

public class PdfTextLayerQualityEvaluatorTests
{
    private readonly PdfTextLayerQualityEvaluator _evaluator = new();

    [Fact]
    public void Assess_AcceptsNormalTextWithPageCoordinates()
    {
        var lines = new[]
        {
            Line("固定レイアウトEPUB", new BoundingBox(0.1, 0.1, 0.6, 0.04)),
            Line("文字レイヤーを利用する", new BoundingBox(0.1, 0.16, 0.7, 0.04)),
        };

        var result = _evaluator.Assess("固定レイアウトEPUB\n文字レイヤーを利用する", lines);

        Assert.True(result.IsUsable);
        Assert.Equal(1, result.PositionCoverage);
    }

    [Fact]
    public void Assess_RejectsTextWithoutCharacterCoordinates()
    {
        var result = _evaluator.Assess("文字列は存在する", []);

        Assert.False(result.IsUsable);
        Assert.Contains("文字座標", result.Reason);
    }

    [Fact]
    public void Assess_RejectsLowPositionCoverage()
    {
        var rawText = new string('A', 100);
        var lines = new[] { Line(new string('A', 20), new BoundingBox(0.1, 0.1, 0.3, 0.04)) };

        var result = _evaluator.Assess(rawText, lines);

        Assert.False(result.IsUsable);
        Assert.Equal(0.2, result.PositionCoverage, precision: 3);
    }

    [Fact]
    public void Assess_RejectsPrivateUseEncodedText()
    {
        var privateUseText = new string('\uE123', 8) + "AB";
        var lines = new[] { Line(privateUseText, new BoundingBox(0.1, 0.1, 0.3, 0.04)) };

        var result = _evaluator.Assess(privateUseText, lines);

        Assert.False(result.IsUsable);
        Assert.Contains("私用領域", result.Reason);
    }

    [Fact]
    public void Assess_RejectsInvalidBounds()
    {
        var lines = new[] { Line("本文", new BoundingBox(0.1, 0.1, 1.2, 0.04)) };

        var result = _evaluator.Assess("本文", lines);

        Assert.False(result.IsUsable);
        Assert.Contains("文字座標", result.Reason);
    }

    [Fact]
    public void Assess_AcceptsValidSupplementaryUnicodeCharacters()
    {
        const string text = "𠮷野😀";
        var lines = new[] { Line(text, new BoundingBox(0.1, 0.1, 0.3, 0.04)) };

        var result = _evaluator.Assess(text, lines);

        Assert.True(result.IsUsable);
    }

    private static TextLine Line(string text, BoundingBox bounds) =>
        new(bounds, text, 1, TextSourceKind.PdfTextLayer);
}
