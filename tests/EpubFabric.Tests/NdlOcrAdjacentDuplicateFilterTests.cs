using EpubFabric.Pipeline;

namespace EpubFabric.Tests;

using LineCandidate = NdlOcrAdjacentDuplicateFilter.LineCandidate;

public sealed class NdlOcrAdjacentDuplicateFilterTests
{
    [Fact]
    public void KeepIndices_ReturnsEmpty_ForNoLines()
    {
        Assert.Empty(NdlOcrAdjacentDuplicateFilter.KeepIndices([]));
    }

    [Fact]
    public void KeepIndices_KeepsDistinctLines()
    {
        var lines = new List<LineCandidate>
        {
            new(860, 60, 900, 900, 0.95),
            new(800, 60, 840, 900, 0.94),
            new(740, 60, 780, 900, 0.93),
        };

        Assert.Equal([0, 1, 2], NdlOcrAdjacentDuplicateFilter.KeepIndices(lines));
    }

    [Fact]
    public void KeepIndices_DropsLowerConfidenceOfAnAdjacentPair()
    {
        var lines = new List<LineCandidate>
        {
            new(860, 60, 900, 900, 0.95),
            new(858, 58, 902, 902, 0.80),
            new(740, 60, 780, 900, 0.93),
        };

        Assert.Equal([0, 2], NdlOcrAdjacentDuplicateFilter.KeepIndices(lines));
    }

    [Fact]
    public void KeepIndices_ReplacesTheEarlierLineWhenTheLaterOneIsMoreConfident()
    {
        var lines = new List<LineCandidate>
        {
            new(860, 60, 900, 900, 0.60),
            new(858, 58, 902, 902, 0.97),
        };

        Assert.Equal([1], NdlOcrAdjacentDuplicateFilter.KeepIndices(lines));
    }

    [Fact]
    public void KeepIndices_KeepsTheEarlierLineOnEqualConfidence()
    {
        var lines = new List<LineCandidate>
        {
            new(860, 60, 900, 900, 0.90),
            new(858, 58, 902, 902, 0.90),
        };

        Assert.Equal([0], NdlOcrAdjacentDuplicateFilter.KeepIndices(lines));
    }

    [Fact]
    public void KeepIndices_DropsANestedLineThatIouWouldMiss()
    {
        // 小さい行が大きい行へ収まっている重複。交差÷和集合は0.36程度で
        // 検出段階のIoU判定では残るが、交差÷小さいほうの面積は1.0になる。
        var large = new LineCandidate(800, 100, 900, 900, 0.95);
        var nested = new LineCandidate(820, 400, 880, 700, 0.70);
        var lines = new List<LineCandidate> { large, nested };

        Assert.Equal([0], NdlOcrAdjacentDuplicateFilter.KeepIndices(lines));
    }

    [Fact]
    public void KeepIndices_OnlyComparesWithTheImmediatelyPrecedingLine()
    {
        // 公式版は直前の1行としか比べない。間に別の行が入れば両方残る。
        var lines = new List<LineCandidate>
        {
            new(860, 60, 900, 900, 0.95),
            new(600, 60, 640, 900, 0.94),
            new(858, 58, 902, 902, 0.93),
        };

        Assert.Equal([0, 1, 2], NdlOcrAdjacentDuplicateFilter.KeepIndices(lines));
    }

    [Fact]
    public void KeepIndices_KeepsPartiallyOverlappingLines()
    {
        // 隣り合う列が少し重なる程度では落とさない。
        var lines = new List<LineCandidate>
        {
            new(860, 60, 900, 900, 0.95),
            new(845, 60, 885, 900, 0.94),
        };

        Assert.Equal([0, 1], NdlOcrAdjacentDuplicateFilter.KeepIndices(lines));
    }

    [Fact]
    public void KeepIndices_IgnoresZeroAreaLines()
    {
        var lines = new List<LineCandidate>
        {
            new(100, 100, 100, 100, 0.95),
            new(100, 100, 100, 100, 0.94),
        };

        Assert.Equal([0, 1], NdlOcrAdjacentDuplicateFilter.KeepIndices(lines));
    }
}
