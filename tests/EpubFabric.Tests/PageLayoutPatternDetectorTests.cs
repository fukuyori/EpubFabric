using EpubFabric.Core.Models;
using EpubFabric.Layout;

namespace EpubFabric.Tests;

public sealed class PageLayoutPatternDetectorTests
{
    [Fact]
    public void Detect_HorizontalSingleColumn()
    {
        var lines = Enumerable.Range(0, 8)
            .Select(index => Line(0.1, 0.1 + index * 0.07, 0.8, 0.025, $"横書き本文{index}です"))
            .ToList();

        var profile = PageLayoutPatternDetector.Detect(lines);

        Assert.Equal(PageLayoutPattern.HorizontalSingleColumn, profile.Pattern);
        Assert.Equal(1, profile.ColumnCount);
        Assert.Null(profile.GutterPosition);
    }

    [Fact]
    public void Detect_HorizontalTwoColumn_IgnoresSpanningHeading()
    {
        var lines = TwoHorizontalColumns();
        lines.Insert(0, Line(0.08, 0.03, 0.84, 0.05, "左右をまたぐ大見出し"));

        var profile = PageLayoutPatternDetector.Detect(lines);

        Assert.Equal(PageLayoutPattern.HorizontalTwoColumn, profile.Pattern);
        Assert.Equal(2, profile.ColumnCount);
        Assert.InRange(profile.GutterPosition!.Value, 0.44, 0.56);
    }

    [Fact]
    public void Detect_VerticalSingleColumn()
    {
        var lines = Enumerable.Range(0, 8)
            .Select(index => Line(0.82 - index * 0.07, 0.1, 0.025, 0.8, $"縦書き本文{index}です"))
            .ToList();

        var profile = PageLayoutPatternDetector.Detect(lines);

        Assert.Equal(PageLayoutPattern.VerticalSingleColumn, profile.Pattern);
        Assert.Equal(1, profile.ColumnCount);
    }

    [Fact]
    public void Detect_VerticalTwoColumn()
    {
        var lines = new List<TextLine>();
        for (var index = 0; index < 6; index++)
        {
            var x = 0.82 - index * 0.07;
            lines.Add(Line(x, 0.08, 0.025, 0.38, $"上段縦書き{index}"));
            lines.Add(Line(x, 0.55, 0.025, 0.38, $"下段縦書き{index}"));
        }

        var profile = PageLayoutPatternDetector.Detect(lines);

        Assert.Equal(PageLayoutPattern.VerticalTwoColumn, profile.Pattern);
        Assert.Equal(2, profile.ColumnCount);
        Assert.InRange(profile.GutterPosition!.Value, 0.44, 0.57);
    }

    [Fact]
    public void Detect_ForcedDirectionStillDetectsColumnCount()
    {
        var profile = PageLayoutPatternDetector.Detect(TwoHorizontalColumns(), WritingMode.Horizontal);

        Assert.Equal(PageLayoutPattern.HorizontalTwoColumn, profile.Pattern);
    }

    [Fact]
    public void Detect_HorizontalThreeColumn_UsesComplexLayoutFallback()
    {
        var lines = new List<TextLine>();
        for (var index = 0; index < 8; index++)
        {
            var y = 0.10 + index * 0.07;
            lines.Add(Line(0.05, y, 0.26, 0.025, $"左段本文{index}です"));
            lines.Add(Line(0.37, y, 0.26, 0.025, $"中央段本文{index}です"));
            lines.Add(Line(0.69, y, 0.26, 0.025, $"右段本文{index}です"));
        }

        var profile = PageLayoutPatternDetector.Detect(lines);

        Assert.Equal(PageLayoutPattern.HorizontalComplex, profile.Pattern);
        Assert.True(profile.UsesGenericColumnDetection);
    }

    private static List<TextLine> TwoHorizontalColumns()
    {
        var lines = new List<TextLine>();
        for (var index = 0; index < 7; index++)
        {
            var y = 0.12 + index * 0.07;
            lines.Add(Line(0.08, y, 0.38, 0.025, $"左段本文{index}です"));
            lines.Add(Line(0.54, y, 0.38, 0.025, $"右段本文{index}です"));
        }

        return lines;
    }

    private static TextLine Line(double x, double y, double width, double height, string text) =>
        new(new BoundingBox(x, y, width, height), text, 0.95, TextSourceKind.Ocr);
}
