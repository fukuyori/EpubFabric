using EpubFabric.Core.Models;

namespace EpubFabric.Pipeline;

/// <summary>
/// NDLOCR-Lite（DEIM）が検出した非文字行の領域を、レイアウト解析の非テキスト領域へ変換する。
/// </summary>
/// <remarks>
/// 図版・組織図・数式・化学式・表組を <see cref="NonTextRegionKind.Figure"/> として渡す。
/// 表組は内部の文字が多ければ、レイアウト解析側（IsTextDominatedRegion）が画像化せず本文に残す。
/// 柱・ノンブルは領域ではなく、行の種別（<see cref="TextLine.SourceType"/>）として渡すため、ここでは扱わない。
/// 広告は本文の一部として扱われることが多く、図にすると文字が失われるため扱わない。
/// </remarks>
public static class NdlOcrRegionMapper
{
    private static readonly HashSet<string> FigureClassNames = new(StringComparer.Ordinal)
    {
        "block_fig", "block_chart", "block_eqn", "block_cfm", "block_table",
    };

    /// <summary>
    /// 画像処理で検出した図候補のうち、DEIMの図領域と重なるものを除いて、DEIMの図領域を加える。
    /// 囲み罫（<see cref="NonTextRegionKind.Boxed"/>）は画像処理の検出結果をそのまま使う。
    /// </summary>
    /// <param name="imageRegions">画像処理（NonTextRegionDetector）の検出結果。</param>
    /// <param name="ndlRegions">NDLOCR-Liteの領域検出結果。座標は同じページ比率の系であること。</param>
    public static IReadOnlyList<NonTextRegion> Merge(
        IReadOnlyList<NonTextRegion> imageRegions,
        IReadOnlyList<NdlOcrRegion> ndlRegions)
    {
        var ndlFigures = ndlRegions
            .Where(region => FigureClassNames.Contains(region.ClassName))
            .Select(region => new NonTextRegion(region.Bounds, NonTextRegionKind.Figure))
            .ToList();
        if (ndlFigures.Count == 0)
        {
            return imageRegions;
        }

        var merged = imageRegions
            .Where(region => region.Kind != NonTextRegionKind.Figure
                || !ndlFigures.Any(figure => OverlapOfSmaller(region.Bounds, figure.Bounds) >= 0.5))
            .ToList();
        merged.AddRange(ndlFigures);
        return merged;
    }

    /// <summary>交差面積 / 小さい方の面積。</summary>
    private static double OverlapOfSmaller(BoundingBox a, BoundingBox b)
    {
        var width = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
        var height = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
        if (width <= 0 || height <= 0)
        {
            return 0;
        }

        var smaller = Math.Min(a.Width * a.Height, b.Width * b.Height);
        return smaller <= 0 ? 0 : width * height / smaller;
    }
}
