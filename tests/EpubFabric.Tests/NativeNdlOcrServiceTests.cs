using EpubFabric.Pipeline;

namespace EpubFabric.Tests;

using DetectionBox = NativeNdlOcrService.DetectionBox;

/// <summary>モデルを使わずに検証できる、NDLOCR-Lite移植部分の前処理・後処理。</summary>
public sealed class NativeNdlOcrServiceTests
{
    [Fact]
    public void SuppressOverlappingRegions_DropsSmallerSameClassBoxCoveredByLargerOne()
    {
        var boxes = new[]
        {
            new DetectionBox("line_main", 10, 10, 20, 110),
            new DetectionBox("line_main", 0, 0, 30, 200),
        };

        Assert.Equal([1], NativeNdlOcrService.SuppressOverlappingRegions(boxes));
    }

    [Fact]
    public void SuppressOverlappingRegions_KeepsDifferentClasses()
    {
        // 柱の領域に含まれる本文行は、クラスが異なるため残す（行の種別付けに使う）。
        var boxes = new[]
        {
            new DetectionBox("block_pillar", 0, 0, 100, 400),
            new DetectionBox("line_main", 10, 10, 30, 300),
        };

        Assert.Equal([0, 1], NativeNdlOcrService.SuppressOverlappingRegions(boxes));
    }

    [Fact]
    public void SuppressOverlappingRegions_KeepsBoxBelowThreshold()
    {
        // 100x10 のうち 89x10 だけが覆われる（89%）。しきい値90%未満なので残す。
        var boxes = new[]
        {
            new DetectionBox("line_main", 0, 0, 100, 10),
            new DetectionBox("line_main", 11, 0, 200, 10),
        };

        Assert.Equal([0, 1], NativeNdlOcrService.SuppressOverlappingRegions(boxes));
    }

    [Fact]
    public void SuppressOverlappingRegions_KeepsEqualSizedDuplicates()
    {
        // 公式版は自分より厳密に大きい領域だけで抑制するため、同じ大きさの重複は両方残る。
        // 隣接する重複は読み順決定後の remove_dup 相当で落とす。
        var boxes = new[]
        {
            new DetectionBox("line_main", 0, 0, 10, 100),
            new DetectionBox("line_main", 0, 0, 10, 100),
        };

        Assert.Equal([0, 1], NativeNdlOcrService.SuppressOverlappingRegions(boxes));
    }

    [Fact]
    public void SuppressOverlappingRegions_KeepsZeroAreaBoxes()
    {
        var boxes = new[]
        {
            new DetectionBox("line_main", 0, 0, 100, 100),
            new DetectionBox("line_main", 10, 10, 10, 50),
        };

        Assert.Equal([0, 1], NativeNdlOcrService.SuppressOverlappingRegions(boxes));
    }

    [Theory]
    [InlineData("これは 、テスト 。", "これは、テスト。")]
    [InlineData("前、 後。 次", "前、後。次")]
    [InlineData("English words stay", "English words stay")]
    public void RemoveSpacesAroundJapanesePunctuation_MatchesUpstream(string input, string expected)
    {
        Assert.Equal(expected, NativeNdlOcrService.RemoveSpacesAroundJapanesePunctuation(input));
    }

    [Fact]
    public void ResolveSourceType_UsesContainerTypeWhenLineCenterIsInsidePillar()
    {
        var regions = new[] { ("block_pillar", 0, 0, 100, 50) };

        Assert.Equal("柱", NativeNdlOcrService.ResolveSourceType("line_main", 10, 10, 90, 30, regions));
    }

    [Fact]
    public void ResolveSourceType_UsesContainerTypeForFolio()
    {
        var regions = new[] { ("block_folio", 400, 900, 460, 940) };

        Assert.Equal("ノンブル", NativeNdlOcrService.ResolveSourceType("line_main", 410, 905, 450, 935, regions));
    }

    [Fact]
    public void ResolveSourceType_IgnoresNonContainerRegionsAndOutsideLines()
    {
        var regions = new[]
        {
            ("block_fig", 0, 0, 500, 500),
            ("block_pillar", 0, 0, 100, 50),
        };

        // 中心(250, 300)は柱の外、図の中。図は種別を変えない。
        Assert.Equal("キャプション", NativeNdlOcrService.ResolveSourceType("line_caption", 200, 290, 300, 310, regions));
    }
}
