namespace EpubFabric.Pipeline;

// C# adaptation of remove_dup / check_dup / check_iou in
// reading_order/order/reorder.py from ndl-lab/ndlocr-lite at
// d25e0d415b607ad44459ca6b95c7512a54363935.
// NDLOCR-Lite: Copyright (c) 2023 National Diet Library, Japan, CC BY 4.0.
// Upstream applies this inside each TEXTBLOCK and to the children directly
// under PAGE. EpubFabric keeps one flat list of lines per page, so this
// adaptation walks the whole page in reading order instead.

/// <summary>
/// 読み順に並べた文字行から、直前の行とほぼ同じ領域を占める行を取り除く。
/// </summary>
/// <remarks>
/// 検出段階の重複抑制はIoUで判定するため、小さい行が大きい行へ入れ子になっている
/// 重複を取り逃がす。公式版と同じく、交差面積を小さいほうの面積で割った比で判定する。
/// </remarks>
public static class NdlOcrAdjacentDuplicateFilter
{
    /// <summary>公式版の<c>check_iou</c>が重複とみなす比。</summary>
    private const double OverlapThreshold = 0.8;

    /// <summary>読み順に並んだ1行分の外接矩形と信頼度。</summary>
    public readonly record struct LineCandidate(int X0, int Y0, int X1, int Y1, double Confidence);

    /// <summary>
    /// 残す行の添字を読み順のまま返す。入力は読み順に並んでいることを前提とする。
    /// </summary>
    public static List<int> KeepIndices(IReadOnlyList<LineCandidate> ordered)
    {
        ArgumentNullException.ThrowIfNull(ordered);

        var kept = new List<int>(ordered.Count);
        foreach (var index in Enumerable.Range(0, ordered.Count))
        {
            if (kept.Count > 0 && IsDuplicate(ordered[kept[^1]], ordered[index]))
            {
                // 公式版は信頼度が同じなら先に見た行を残す。
                if (ordered[kept[^1]].Confidence >= ordered[index].Confidence)
                {
                    continue;
                }

                kept.RemoveAt(kept.Count - 1);
            }

            kept.Add(index);
        }

        return kept;
    }

    private static bool IsDuplicate(LineCandidate left, LineCandidate right)
    {
        var intersectionWidth = Math.Max(0, Math.Min(left.X1, right.X1) - Math.Max(left.X0, right.X0));
        var intersectionHeight = Math.Max(0, Math.Min(left.Y1, right.Y1) - Math.Max(left.Y0, right.Y0));
        var intersection = (long)intersectionWidth * intersectionHeight;
        var leftArea = (long)(left.X1 - left.X0) * (left.Y1 - left.Y0);
        var rightArea = (long)(right.X1 - right.X0) * (right.Y1 - right.Y0);
        var minimumArea = Math.Min(leftArea, rightArea);
        return minimumArea > 0 && intersection / (double)minimumArea > OverlapThreshold;
    }
}
