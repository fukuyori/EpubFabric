using EpubFabric.Core.Models;
using EpubFabric.Pipeline;

namespace EpubFabric.Tests;

public class ExternalFigureRegionMergerTests
{
    [Fact]
    public void Merge_AddsOnlyHighConfidenceFigureLabels()
    {
        var regions = new List<NonTextRegion>();
        var candidates = new[]
        {
            Candidate("image", 0.91, new BoundingBox(0.1, 0.1, 0.3, 0.3)),
            Candidate("chart", 0.50, new BoundingBox(0.5, 0.1, 0.3, 0.3)),
            Candidate("text", 0.99, new BoundingBox(0.1, 0.5, 0.3, 0.3)),
        };

        var added = ExternalFigureRegionMerger.Merge(regions, candidates, minimumConfidence: 0.75);

        Assert.Equal(1, added);
        Assert.Single(regions);
        Assert.Equal(candidates[0].Bounds, regions[0].Bounds);
    }

    [Fact]
    public void Merge_DoesNotDuplicateOverlappingExistingFigure()
    {
        var existing = new NonTextRegion(
            new BoundingBox(0.1, 0.1, 0.4, 0.4),
            NonTextRegionKind.Figure);
        var regions = new List<NonTextRegion> { existing };
        var candidates = new[]
        {
            Candidate("table", 0.95, new BoundingBox(0.12, 0.12, 0.36, 0.36)),
        };

        var added = ExternalFigureRegionMerger.Merge(regions, candidates, minimumConfidence: 0.75);

        Assert.Equal(0, added);
        Assert.Same(existing, Assert.Single(regions));
    }

    private static PpDocLayoutRegion Candidate(string label, double confidence, BoundingBox bounds) =>
        new(bounds, label, confidence, null);
}
