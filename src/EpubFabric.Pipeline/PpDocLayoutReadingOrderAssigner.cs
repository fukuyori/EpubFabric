using EpubFabric.Core.Models;

namespace EpubFabric.Pipeline;

public sealed record PpDocLayoutReadingOrderAssignment(
    IReadOnlyList<TextLine> Lines,
    int AssignedLineCount,
    double Coverage,
    bool Adopted);

/// <summary>
/// PP-DocLayoutV2の領域読み順を、文字列を変更せず既存の文字行へ割り当てる。
/// </summary>
public static class PpDocLayoutReadingOrderAssigner
{
    public static PpDocLayoutReadingOrderAssignment Assign(
        IReadOnlyList<TextLine> lines,
        IReadOnlyList<PpDocLayoutRegion> regions,
        WritingMode writingMode,
        double minimumCoverage)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(regions);
        if (minimumCoverage is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumCoverage));
        }

        if (lines.Count == 0
            || regions.Count == 0
            || lines.Any(line => line.SourceReadingOrder is not null))
        {
            return new PpDocLayoutReadingOrderAssignment(lines, 0, 0, false);
        }

        var assignments = lines
            .Select((line, index) => new LineAssignment(line, index, FindBestRegion(line.Bounds, regions)))
            .ToList();
        var assignedCount = assignments.Count(item => item.Region is not null);
        var coverage = (double)assignedCount / lines.Count;
        var orderedRegionCount = assignments
            .Where(item => item.Region?.ReadingOrder is not null)
            .Select(item => item.Region)
            .Distinct()
            .Count();

        if (coverage < minimumCoverage || orderedRegionCount < 2)
        {
            return new PpDocLayoutReadingOrderAssignment(lines, assignedCount, coverage, false);
        }

        var ordered = assignments
            .GroupBy(item => item.Region)
            .OrderBy(group => group.Key is null || group.Key.ReadingOrder is null ? 1 : 0)
            .ThenBy(group => group.Key?.ReadingOrder ?? int.MaxValue)
            .ThenBy(group => SpatialRegionKey(group.Key, writingMode))
            .SelectMany(group => OrderWithinRegion(group, group.Key, writingMode))
            .Select((item, order) => item.Line with
            {
                SourceReadingOrder = order,
                SourceType = item.Line.SourceType ?? MapSourceType(item.Region?.Label),
                SourceRegionId = item.Line.SourceRegionId ?? RegionId(item.Region),
            })
            .ToList();

        return new PpDocLayoutReadingOrderAssignment(ordered, assignedCount, coverage, true);
    }

    private static PpDocLayoutRegion? FindBestRegion(
        BoundingBox line,
        IReadOnlyList<PpDocLayoutRegion> regions)
    {
        var lineArea = line.Width * line.Height;
        return regions
            .Select(region => new
            {
                Region = region,
                Overlap = IntersectionArea(line, region.Bounds) / lineArea,
                ContainsCenter = ContainsCenter(region.Bounds, line),
                Area = region.Bounds.Width * region.Bounds.Height,
            })
            .Where(candidate => candidate.ContainsCenter || candidate.Overlap >= 0.4)
            .OrderByDescending(candidate => candidate.ContainsCenter)
            .ThenByDescending(candidate => candidate.Overlap)
            .ThenBy(candidate => candidate.Area)
            .Select(candidate => candidate.Region)
            .FirstOrDefault();
    }

    private static IEnumerable<LineAssignment> OrderWithinRegion(
        IEnumerable<LineAssignment> group,
        PpDocLayoutRegion? region,
        WritingMode pageWritingMode)
    {
        var isVertical = region?.Label == "vertical_text"
            || (region is null && pageWritingMode == WritingMode.Vertical);
        if (isVertical)
        {
            return group
                .OrderByDescending(item => item.Line.Bounds.X)
                .ThenBy(item => item.Line.Bounds.Y)
                .ToList();
        }

        var ordered = group.ToList();
        ordered.Sort((left, right) => CompareHorizontal(left.Line.Bounds, right.Line.Bounds));
        return ordered;
    }

    private static int CompareHorizontal(BoundingBox left, BoundingBox right)
    {
        if (OverlapsOnAxis(left.Y, left.Height, right.Y, right.Height))
        {
            var rowOrder = left.X.CompareTo(right.X);
            if (rowOrder != 0)
            {
                return rowOrder;
            }
        }

        var horizontalOrder = left.Y.CompareTo(right.Y);
        return horizontalOrder != 0 ? horizontalOrder : left.X.CompareTo(right.X);
    }

    private static bool OverlapsOnAxis(double firstStart, double firstLength, double secondStart, double secondLength)
    {
        var overlap = Math.Max(
            0,
            Math.Min(firstStart + firstLength, secondStart + secondLength) - Math.Max(firstStart, secondStart));
        return overlap / Math.Min(firstLength, secondLength) >= 0.5;
    }

    private static double SpatialRegionKey(PpDocLayoutRegion? region, WritingMode writingMode)
    {
        if (region is null)
        {
            return double.MaxValue;
        }

        return writingMode == WritingMode.Vertical
            ? 1 - region.Bounds.X
            : region.Bounds.Y;
    }

    private static string? MapSourceType(string? label) => label switch
    {
        "doc_title" => "文書タイトル",
        "figure_title" => "キャプション",
        "footnote" or "vision_footnote" => "注",
        "header" => "柱",
        "number" => "ノンブル",
        _ => null,
    };

    private static string? RegionId(PpDocLayoutRegion? region) => region is null
        ? null
        : $"pp:{region.ReadingOrder}:{region.Label}:"
            + $"{region.Bounds.X:R},{region.Bounds.Y:R},{region.Bounds.Width:R},{region.Bounds.Height:R}";

    private static bool ContainsCenter(BoundingBox container, BoundingBox item)
    {
        var x = item.X + item.Width / 2;
        var y = item.Y + item.Height / 2;
        return x >= container.X
            && x <= container.X + container.Width
            && y >= container.Y
            && y <= container.Y + container.Height;
    }

    private static double IntersectionArea(BoundingBox left, BoundingBox right)
    {
        var width = Math.Max(0, Math.Min(left.X + left.Width, right.X + right.Width) - Math.Max(left.X, right.X));
        var height = Math.Max(0, Math.Min(left.Y + left.Height, right.Y + right.Height) - Math.Max(left.Y, right.Y));
        return width * height;
    }

    private sealed record LineAssignment(TextLine Line, int OriginalIndex, PpDocLayoutRegion? Region);
}
