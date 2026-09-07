using EpubFabric.Core.Models;

namespace EpubFabric.Pipeline;

/// <summary>
/// 外部レイアウト解析の図版候補を既存検出結果へ安全に統合する。
/// </summary>
public static class ExternalFigureRegionMerger
{
    private static readonly HashSet<string> FigureLabels =
        new(StringComparer.Ordinal) { "image", "chart", "table" };

    public static int Merge(
        List<NonTextRegion> regions,
        IReadOnlyList<PpDocLayoutRegion> externalRegions,
        double minimumConfidence)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(externalRegions);

        var added = 0;
        foreach (var candidate in externalRegions.Where(region =>
            FigureLabels.Contains(region.Label)
            && region.Confidence >= minimumConfidence))
        {
            if (regions.Any(region =>
                region.Kind == NonTextRegionKind.Figure
                && OverlapsSignificantly(region.Bounds, candidate.Bounds)))
            {
                continue;
            }

            regions.Add(new NonTextRegion(candidate.Bounds, NonTextRegionKind.Figure));
            added++;
        }

        return added;
    }

    private static bool OverlapsSignificantly(BoundingBox left, BoundingBox right)
    {
        var overlapWidth = Math.Max(0, Math.Min(left.X + left.Width, right.X + right.Width) - Math.Max(left.X, right.X));
        var overlapHeight = Math.Max(0, Math.Min(left.Y + left.Height, right.Y + right.Height) - Math.Max(left.Y, right.Y));
        var overlapArea = overlapWidth * overlapHeight;
        var smallerArea = Math.Min(left.Width * left.Height, right.Width * right.Height);
        return smallerArea > 0 && overlapArea / smallerArea > 0.5;
    }
}
