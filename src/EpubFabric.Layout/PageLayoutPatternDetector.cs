using EpubFabric.Core.Models;

namespace EpubFabric.Layout;

/// <summary>
/// 本文行の方向と、本文領域を縦断する段間の空白帯から、ページを
/// 横書き1段・横書き2段・縦書き1段・縦書き2段へ分類する。
/// </summary>
public static class PageLayoutPatternDetector
{
    private const int MinimumCandidateCount = 4;
    private const int MinimumTextLength = 3;
    private const double OrientationAspectRatio = 1.25;
    private const double SearchInsetRatio = 0.22;
    private const double GutterStep = 0.005;
    // 段をまたぐリード文・大見出しを持つ雑誌ページを許容する。本文の約7割が左右へ
    // 分かれていれば2段と判断できる一方、通常の1段本文は中央を跨ぐ比率が大きい。
    private const double MaximumCrossingWeightRatio = 0.30;
    private const double MinimumSideWeightRatio = 0.18;
    private const double MinimumGutterWidth = 0.018;
    private const double MinimumSideVerticalSpan = 0.12;

    public static PageLayoutProfile Detect(
        IReadOnlyList<TextLine> lines,
        WritingMode? forcedWritingMode = null)
    {
        var writingMode = forcedWritingMode ?? WritingModeDetector.DetectPageMode(lines);
        var oriented = lines
            .Where(line => line.Text.Count(character => !char.IsWhiteSpace(character)) >= MinimumTextLength)
            .Select(line => new Candidate(
                writingMode == WritingMode.Vertical
                    ? HeuristicLayoutAnalyzer.ToReadingCoordinates(line.Bounds)
                    : line.Bounds,
                Math.Min(80, line.Text.Count(character => !char.IsWhiteSpace(character)))))
            .Where(candidate => candidate.Bounds.Width >= candidate.Bounds.Height * OrientationAspectRatio)
            .ToList();

        if (oriented.Count < MinimumCandidateCount)
        {
            return SingleColumn(writingMode, confidence: 0.35);
        }

        // 大見出しや全面広告の巨大文字は段間を跨ぐため、本文行高さの中央値から外れる
        // 行を分類材料から外す。短い段落末行は段幅の証拠になるので残す。
        var heights = oriented.Select(candidate => candidate.Bounds.Height).OrderBy(height => height).ToList();
        var medianHeight = heights[heights.Count / 2];
        var bodyCandidates = oriented
            .Where(candidate => candidate.Bounds.Height <= medianHeight * 1.8)
            .ToList();
        if (bodyCandidates.Count < MinimumCandidateCount)
        {
            bodyCandidates = oriented;
        }

        // 3〜4段組みや不等幅の技術誌ページは4分類へ無理に押し込まず、従来の再帰段検出へ
        // 逃がす。大見出し由来の単独グループは、項目数・文字量・縦方向の広がりで除く。
        var totalBodyWeight = bodyCandidates.Sum(candidate => candidate.Weight);
        var substantialColumnCount = ColumnDetector
            .DetectColumns(bodyCandidates, candidate => candidate.Bounds)
            .Count(group => group.Count >= MinimumCandidateCount
                && group.Sum(candidate => candidate.Weight) >= totalBodyWeight * 0.12
                && VerticalSpan(group) >= 0.20);
        // 縦書き2段は、回転後の各段内にある段落間の空白まで再帰分割されて3群以上に
        // 見えやすい。縦書きは明示的な1段/2段分類を優先し、この退避は横書きに限る。
        if (writingMode == WritingMode.Horizontal && substantialColumnCount >= 3)
        {
            return new PageLayoutProfile(
                PageLayoutPattern.HorizontalComplex,
                Math.Clamp(0.65 + substantialColumnCount * 0.08, 0, 0.95));
        }

        var contentStart = bodyCandidates.Min(candidate => candidate.Bounds.X);
        var contentEnd = bodyCandidates.Max(candidate => candidate.Bounds.X + candidate.Bounds.Width);
        var contentWidth = contentEnd - contentStart;
        if (contentWidth <= 0.2)
        {
            return SingleColumn(writingMode, confidence: 0.55);
        }

        var searchStart = contentStart + contentWidth * SearchInsetRatio;
        var searchEnd = contentEnd - contentWidth * SearchInsetRatio;
        var totalWeight = bodyCandidates.Sum(candidate => candidate.Weight);
        var validPositions = new List<double>();

        for (var x = searchStart; x <= searchEnd + 1e-9; x += GutterStep)
        {
            var crossingWeight = bodyCandidates
                .Where(candidate => Crosses(candidate.Bounds, x))
                .Sum(candidate => candidate.Weight);
            var left = bodyCandidates.Where(candidate => Center(candidate.Bounds) < x).ToList();
            var right = bodyCandidates.Where(candidate => Center(candidate.Bounds) >= x).ToList();
            var leftWeight = left.Sum(candidate => candidate.Weight);
            var rightWeight = right.Sum(candidate => candidate.Weight);

            if ((double)crossingWeight / totalWeight <= MaximumCrossingWeightRatio
                && (double)leftWeight / totalWeight >= MinimumSideWeightRatio
                && (double)rightWeight / totalWeight >= MinimumSideWeightRatio
                && VerticalSpan(left) >= MinimumSideVerticalSpan
                && VerticalSpan(right) >= MinimumSideVerticalSpan)
            {
                validPositions.Add(x);
            }
        }

        var band = WidestContinuousBand(validPositions);
        if (band is null || band.Value.End - band.Value.Start < MinimumGutterWidth)
        {
            return SingleColumn(writingMode, confidence: 0.75);
        }

        var gutter = (band.Value.Start + band.Value.End) / 2;
        var crossingRatio = (double)bodyCandidates
            .Where(candidate => Crosses(candidate.Bounds, gutter))
            .Sum(candidate => candidate.Weight) / totalWeight;
        var leftRatio = (double)bodyCandidates
            .Where(candidate => Center(candidate.Bounds) < gutter)
            .Sum(candidate => candidate.Weight) / totalWeight;
        var rightRatio = (double)bodyCandidates
            .Where(candidate => Center(candidate.Bounds) >= gutter)
            .Sum(candidate => candidate.Weight) / totalWeight;
        var balance = Math.Min(leftRatio, rightRatio) / Math.Max(leftRatio, rightRatio);
        var confidence = Math.Clamp(0.55 + (1 - crossingRatio) * 0.25 + balance * 0.20, 0, 0.99);

        return new PageLayoutProfile(
            writingMode == WritingMode.Vertical
                ? PageLayoutPattern.VerticalTwoColumn
                : PageLayoutPattern.HorizontalTwoColumn,
            confidence,
            gutter);
    }

    private static PageLayoutProfile SingleColumn(WritingMode mode, double confidence) => new(
        mode == WritingMode.Vertical
            ? PageLayoutPattern.VerticalSingleColumn
            : PageLayoutPattern.HorizontalSingleColumn,
        confidence);

    private static (double Start, double End)? WidestContinuousBand(IReadOnlyList<double> positions)
    {
        if (positions.Count == 0)
        {
            return null;
        }

        var bestStart = positions[0];
        var bestEnd = positions[0];
        var currentStart = positions[0];
        var previous = positions[0];

        for (var i = 1; i < positions.Count; i++)
        {
            var position = positions[i];
            if (position - previous > GutterStep * 1.5)
            {
                if (previous - currentStart > bestEnd - bestStart)
                {
                    bestStart = currentStart;
                    bestEnd = previous;
                }

                currentStart = position;
            }

            previous = position;
        }

        if (previous - currentStart > bestEnd - bestStart)
        {
            bestStart = currentStart;
            bestEnd = previous;
        }

        return (bestStart, bestEnd);
    }

    private static double VerticalSpan(IReadOnlyList<Candidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return 0;
        }

        return candidates.Max(candidate => candidate.Bounds.Y + candidate.Bounds.Height)
            - candidates.Min(candidate => candidate.Bounds.Y);
    }

    private static bool Crosses(BoundingBox bounds, double x) =>
        bounds.X < x && bounds.X + bounds.Width > x;

    private static double Center(BoundingBox bounds) => bounds.X + bounds.Width / 2;

    private readonly record struct Candidate(BoundingBox Bounds, int Weight);
}
