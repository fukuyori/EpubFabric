using EpubFabric.Core.Models;
using EpubFabric.Pipeline;

namespace EpubFabric.Tests;

public class PpDocLayoutReadingOrderAssignerTests
{
    [Fact]
    public void Assign_UsesRegionOrderAndPreservesText()
    {
        var lines = new[]
        {
            Line("右下", 0.6, 0.2),
            Line("左下", 0.1, 0.2),
            Line("右上", 0.6, 0.1),
            Line("左上", 0.1, 0.1),
        };
        var regions = new[]
        {
            Region("text", 2, 0.55, 0.05, 0.4, 0.3),
            Region("text", 1, 0.05, 0.05, 0.4, 0.3),
        };

        var result = PpDocLayoutReadingOrderAssigner.Assign(
            lines,
            regions,
            WritingMode.Horizontal,
            minimumCoverage: 0.75);

        Assert.True(result.Adopted);
        Assert.Equal(4, result.AssignedLineCount);
        Assert.Equal(new[] { "左上", "左下", "右上", "右下" }, result.Lines.Select(line => line.Text));
        Assert.Equal(new int?[] { 0, 1, 2, 3 }, result.Lines.Select(line => line.SourceReadingOrder));
    }

    [Fact]
    public void Assign_OrdersVerticalLinesFromRightToLeftWithinRegion()
    {
        var lines = new[]
        {
            Line("左", 0.2, 0.1, width: 0.03, height: 0.3),
            Line("右", 0.7, 0.1, width: 0.03, height: 0.3),
            Line("次", 0.7, 0.6, width: 0.03, height: 0.3),
        };
        var regions = new[]
        {
            Region("vertical_text", 1, 0.1, 0.05, 0.7, 0.4),
            Region("vertical_text", 2, 0.6, 0.55, 0.2, 0.4),
        };

        var result = PpDocLayoutReadingOrderAssigner.Assign(
            lines,
            regions,
            WritingMode.Vertical,
            minimumCoverage: 0.75);

        Assert.True(result.Adopted);
        Assert.Equal(new[] { "右", "左", "次" }, result.Lines.Select(line => line.Text));
    }

    [Fact]
    public void Assign_DoesNotAdoptLowCoverageResult()
    {
        var lines = new[]
        {
            Line("一", 0.1, 0.1),
            Line("二", 0.1, 0.2),
            Line("三", 0.7, 0.1),
        };
        var regions = new[]
        {
            Region("text", 1, 0.05, 0.05, 0.4, 0.2),
            Region("text", 2, 0.05, 0.25, 0.4, 0.1),
        };

        var result = PpDocLayoutReadingOrderAssigner.Assign(
            lines,
            regions,
            WritingMode.Horizontal,
            minimumCoverage: 0.75);

        Assert.False(result.Adopted);
        Assert.Same(lines, result.Lines);
    }

    [Fact]
    public void Assign_OrdersOverlappingTitleFragmentsFromLeftToRightDespiteSmallYDifference()
    {
        var lines = new[]
        {
            Line("右半分", 0.45, 0.099, width: 0.35, height: 0.07),
            Line("左半分", 0.20, 0.100, width: 0.32, height: 0.09),
            Line("本文", 0.10, 0.30, width: 0.8, height: 0.1),
        };
        var regions = new[]
        {
            Region("doc_title", 1, 0.15, 0.08, 0.7, 0.15),
            Region("text", 2, 0.05, 0.25, 0.9, 0.2),
        };

        var result = PpDocLayoutReadingOrderAssigner.Assign(
            lines,
            regions,
            WritingMode.Horizontal,
            minimumCoverage: 0.75);

        Assert.True(result.Adopted);
        Assert.Equal(new[] { "左半分", "右半分", "本文" }, result.Lines.Select(line => line.Text));
        Assert.Equal(result.Lines[0].SourceRegionId, result.Lines[1].SourceRegionId);
        Assert.NotEqual(result.Lines[1].SourceRegionId, result.Lines[2].SourceRegionId);
    }

    private static TextLine Line(
        string text,
        double x,
        double y,
        double width = 0.2,
        double height = 0.03) =>
        new(new BoundingBox(x, y, width, height), text, 0.95, TextSourceKind.Ocr);

    private static PpDocLayoutRegion Region(
        string label,
        int order,
        double x,
        double y,
        double width,
        double height) =>
        new(new BoundingBox(x, y, width, height), label, 0.95, order);
}
