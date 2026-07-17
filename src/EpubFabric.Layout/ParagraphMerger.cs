using EpubFabric.Core.Models;

namespace EpubFabric.Layout;

/// <summary>
/// レイアウト解析が行単位で出力するブロックのうち、同じ段で縦に連続する
/// 本文（Body）・囲み記事（Aside）を段落単位へ統合する。行ごとに&lt;p&gt;が
/// 生成されて読みにくくなるのを防ぐ。読み順はレイアウト解析の結果を保ち、
/// 統合後に振り直す。
/// </summary>
public sealed class ParagraphMerger
{
    /// <summary>次行までの縦の隙間がこの倍率（行高さ比）を超えたら段落の切れ目とみなす。</summary>
    private const double MaxLineGapRatio = 0.8;

    /// <summary>行頭がこの倍率（行高さ≒1em比）以上右へ下がっていたら字下げ＝新しい段落とみなす。</summary>
    private const double IndentRatio = 0.9;

    /// <summary>行高さの比がこれを超える行同士は別ブロック（フォントサイズが異なる）とみなす。</summary>
    private const double MaxHeightRatio = 1.4;

    public List<PageBlock> Merge(List<PageBlock> blocks)
    {
        var result = new List<PageBlock>();

        // 統合の可否は「直前に取り込んだ行」との比較で判定する。統合済みブロックの
        // 外接矩形と比べると、段落が伸びるほど高さ比・行間の判定が壊れるため。
        PageBlock? lastLine = null;

        foreach (var block in blocks.OrderBy(b => b.ReadingOrder))
        {
            var previous = result.Count > 0 ? result[^1] : null;

            if (previous is not null && lastLine is not null && CanMerge(previous, lastLine, block))
            {
                previous.OcrText = JoinLineTexts(previous.OcrText, block.OcrText);
                previous.Bounds = Union(previous.Bounds, block.Bounds);
                previous.OcrConfidence = Math.Min(previous.OcrConfidence, block.OcrConfidence);
                previous.RequiresReview |= block.RequiresReview;
                lastLine = block;
                continue;
            }

            result.Add(block);
            lastLine = block;
        }

        for (var i = 0; i < result.Count; i++)
        {
            result[i].ReadingOrder = i;
        }

        return result;
    }

    private static bool CanMerge(PageBlock paragraph, PageBlock lastLine, PageBlock next)
    {
        if (lastLine.Type != next.Type
            || lastLine.TextSource != next.TextSource
            || next.Type is not (BlockType.Body or BlockType.Aside))
        {
            return false;
        }

        if (lastLine.IsExcluded || next.IsExcluded || paragraph.IsManuallyEdited || next.IsManuallyEdited)
        {
            return false;
        }

        var lineHeight = Math.Min(lastLine.Bounds.Height, next.Bounds.Height);
        if (lineHeight <= 0)
        {
            return false;
        }

        // 別の段（横に並んでいる）や、フォントサイズが違う行は統合しない。
        var overlapX = Math.Min(lastLine.Bounds.X + lastLine.Bounds.Width, next.Bounds.X + next.Bounds.Width)
            - Math.Max(lastLine.Bounds.X, next.Bounds.X);
        var narrower = Math.Min(lastLine.Bounds.Width, next.Bounds.Width);
        if (narrower <= 0 || overlapX / narrower < 0.5)
        {
            return false;
        }

        var heightRatio = Math.Max(lastLine.Bounds.Height, next.Bounds.Height) / lineHeight;
        if (heightRatio > MaxHeightRatio)
        {
            return false;
        }

        // 縦の隙間が大きい（段落間スペース）か、字下げされている行は新しい段落。
        var gap = next.Bounds.Y - (lastLine.Bounds.Y + lastLine.Bounds.Height);
        if (gap < -0.5 * lineHeight || gap > MaxLineGapRatio * lineHeight)
        {
            return false;
        }

        if (next.Bounds.X - lastLine.Bounds.X > IndentRatio * next.Bounds.Height)
        {
            return false;
        }

        return true;
    }

    private static BoundingBox Union(BoundingBox a, BoundingBox b)
    {
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        var right = Math.Max(a.X + a.Width, b.X + b.Width);
        var bottom = Math.Max(a.Y + a.Height, b.Y + b.Height);
        return new BoundingBox(x, y, right - x, bottom - y);
    }

    private static string JoinLineTexts(string a, string b)
    {
        if (a.Length == 0)
        {
            return b;
        }

        if (b.Length == 0)
        {
            return a;
        }

        // 欧文の行またぎは語間スペースを補い、和文はそのまま連結する。
        return char.IsAscii(a[^1]) && !char.IsWhiteSpace(a[^1]) && char.IsAscii(b[0]) && !char.IsWhiteSpace(b[0])
            ? $"{a} {b}"
            : a + b;
    }
}
