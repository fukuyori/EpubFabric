using EpubFabric.Core.Models;
using EpubFabric.Pipeline;

namespace EpubFabric.Tests;

public sealed class NdlOcrRegionMapperTests
{
    [Fact]
    public void Merge_ReturnsImageRegionsWhenNoNdlFigures()
    {
        var imageRegions = new[] { new NonTextRegion(new BoundingBox(0.1, 0.1, 0.3, 0.3), NonTextRegionKind.Figure) };
        var ndlRegions = new[] { new NdlOcrRegion("block_pillar", new BoundingBox(0, 0, 1, 0.05), 0.9f) };

        Assert.Same(imageRegions, NdlOcrRegionMapper.Merge(imageRegions, ndlRegions));
    }

    [Theory]
    [InlineData("block_fig")]
    [InlineData("block_chart")]
    [InlineData("block_eqn")]
    [InlineData("block_cfm")]
    [InlineData("block_table")]
    public void Merge_AddsFigureLikeClassesAsFigureRegions(string className)
    {
        var bounds = new BoundingBox(0.5, 0.5, 0.2, 0.2);

        var merged = NdlOcrRegionMapper.Merge([], [new NdlOcrRegion(className, bounds, 0.9f)]);

        var region = Assert.Single(merged);
        Assert.Equal(NonTextRegionKind.Figure, region.Kind);
        Assert.Equal(bounds, region.Bounds);
    }

    [Theory]
    [InlineData("block_ad")]
    [InlineData("block_pillar")]
    [InlineData("block_folio")]
    public void Merge_IgnoresNonFigureClasses(string className)
    {
        Assert.Empty(NdlOcrRegionMapper.Merge([], [new NdlOcrRegion(className, new BoundingBox(0, 0, 0.2, 0.2), 0.9f)]));
    }

    [Fact]
    public void Merge_ReplacesOverlappingImageFigureAndKeepsOthers()
    {
        var overlapping = new NonTextRegion(new BoundingBox(0.12, 0.12, 0.26, 0.26), NonTextRegionKind.Figure);
        var separate = new NonTextRegion(new BoundingBox(0.6, 0.6, 0.2, 0.2), NonTextRegionKind.Figure);
        var boxed = new NonTextRegion(new BoundingBox(0.1, 0.1, 0.3, 0.3), NonTextRegionKind.Boxed);
        var ndlFigure = new NdlOcrRegion("block_fig", new BoundingBox(0.1, 0.1, 0.3, 0.3), 0.9f);

        var merged = NdlOcrRegionMapper.Merge([overlapping, separate, boxed], [ndlFigure]);

        Assert.Equal(
            [separate, boxed, new NonTextRegion(ndlFigure.Bounds, NonTextRegionKind.Figure)],
            merged);
    }
}
