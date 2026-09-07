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
    private const double MaxHeightRatio = 1.6;

    /// <summary>本文行の何倍の高さならドロップキャップ（段落頭の飾り文字）とみなすか。</summary>
    private const double DropCapHeightRatio = 1.5;

    /// <summary>ドロップキャップとみなす文字数の上限。</summary>
    private const int DropCapMaxLength = 2;

    public List<PageBlock> Merge(List<PageBlock> blocks, WritingMode writingMode = WritingMode.Horizontal)
    {
        blocks = MergeDropCaps(blocks, writingMode);

        var result = new List<PageBlock>();

        // 統合の可否は「直前に取り込んだ行」との比較で判定する。統合済みブロックの
        // 外接矩形と比べると、段落が伸びるほど高さ比・行間の判定が壊れるため。
        PageBlock? lastLine = null;

        foreach (var block in blocks.OrderBy(b => b.ReadingOrder))
        {
            var previous = result.Count > 0 ? result[^1] : null;

            if (previous is not null && lastLine is not null && CanMerge(previous, lastLine, block, writingMode))
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

    /// <summary>
    /// ページ末尾で途切れ、次ページの先頭から小文字で続く本文段落を連結する。
    /// ページ単位解析のままでは、改ページ位置ごとに不要な &lt;p&gt; が生じるため。
    /// </summary>
    public int MergeAcrossPages(IReadOnlyList<DocumentPage> pages)
    {
        var mergedCount = 0;

        for (var i = 1; i < pages.Count; i++)
        {
            var previous = pages[i - 1].Blocks
                .Where(block => !block.IsExcluded)
                .OrderBy(block => block.ReadingOrder)
                .LastOrDefault();
            var next = pages[i].Blocks
                .Where(block => !block.IsExcluded)
                .OrderBy(block => block.ReadingOrder)
                .FirstOrDefault();

            if (previous is null || next is null || !CanMergeAcrossPage(previous, next))
            {
                continue;
            }

            previous.OcrText = JoinLineTexts(previous.OcrText, next.OcrText);
            previous.OcrConfidence = Math.Min(previous.OcrConfidence, next.OcrConfidence);
            previous.RequiresReview |= next.RequiresReview;
            pages[i].Blocks.Remove(next);
            mergedCount++;
        }

        return mergedCount;
    }

    /// <summary>
    /// 段落頭の飾り文字（ドロップキャップ）を、直後の行の先頭へ連結する。
    /// 雑誌の巻頭言などで段落の1文字目を数行分の大きさで組む体裁があり、OCRはこれを
    /// 独立した行として拾うため、そのままでは「2」と「025年2月…」に分かれてしまう。
    /// 位置関係だけで判定し、飾り文字の外接矩形は取り込まない（大きな高さを持ち込むと
    /// 後続の行高さ比較が壊れて、段落統合そのものが止まるため）。
    /// </summary>
    private static List<PageBlock> MergeDropCaps(List<PageBlock> blocks, WritingMode writingMode)
    {
        var lineHeights = blocks
            .Where(b => b.OcrText.Trim().Length > DropCapMaxLength && b.Bounds.Height > 0)
            .Select(b => ReadingBounds(b, writingMode).Height)
            .OrderBy(h => h)
            .ToList();
        if (lineHeights.Count == 0)
        {
            return blocks;
        }

        var medianHeight = lineHeights[lineHeights.Count / 2];
        var merged = new HashSet<string>();
        var caps = new List<PageBlock>();

        foreach (var cap in blocks)
        {
            var capText = cap.OcrText.Trim();
            var capBounds = ReadingBounds(cap, writingMode);
            if (capText.Length is 0 or > DropCapMaxLength
                || cap.IsExcluded
                || capBounds.Height < medianHeight * DropCapHeightRatio)
            {
                continue;
            }

            // 飾り文字と縦に重なり、その右側から始まる行のうち最も上の行が続きの本文。
            var target = blocks
                .Where(b => !ReferenceEquals(b, cap)
                    && !b.IsExcluded
                    && !merged.Contains(b.Id)
                    && b.OcrText.Trim().Length > DropCapMaxLength
                    && ReadingBounds(b, writingMode).X >= capBounds.X
                    && VerticalOverlap(capBounds, ReadingBounds(b, writingMode)) > 0)
                .OrderBy(b => ReadingBounds(b, writingMode).Y)
                .FirstOrDefault();

            if (target is null || target.IsManuallyEdited)
            {
                continue;
            }

            target.OcrText = capText + target.OcrText;
            target.OcrConfidence = Math.Min(target.OcrConfidence, cap.OcrConfidence);
            target.RequiresReview |= cap.RequiresReview;
            merged.Add(target.Id);
            caps.Add(cap);
        }

        return caps.Count == 0 ? blocks : blocks.Where(b => !caps.Contains(b)).ToList();
    }

    private static double VerticalOverlap(BoundingBox a, BoundingBox b) =>
        Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);

    private static bool CanMerge(PageBlock paragraph, PageBlock lastLine, PageBlock next, WritingMode writingMode)
    {
        var sameExternalTitleRegion = lastLine.Type == next.Type
            && next.Type is BlockType.ChapterTitle or BlockType.SectionHeading
            && !string.IsNullOrWhiteSpace(lastLine.SourceRegionId)
            && lastLine.SourceRegionId == next.SourceRegionId;
        if (lastLine.Type != next.Type
            || lastLine.TextSource != next.TextSource
            || (next.Type is not (BlockType.Body or BlockType.Aside
                or BlockType.SectionHeading or BlockType.Subheading)
                && !sameExternalTitleRegion))
        {
            return false;
        }

        if (lastLine.IsExcluded || next.IsExcluded || paragraph.IsManuallyEdited || next.IsManuallyEdited)
        {
            return false;
        }

        // PP-DocLayoutV2が一つのタイトル領域へまとめた断片は、紙面上で横に並ぶ
        // 場合でも一つの見出しへ戻す。領域IDが一致する見出しだけに限定する。
        if (sameExternalTitleRegion)
        {
            return true;
        }

        var lastBounds = ReadingBounds(lastLine, writingMode);
        var nextBounds = ReadingBounds(next, writingMode);
        var lineHeight = Math.Min(lastBounds.Height, nextBounds.Height);
        if (lineHeight <= 0)
        {
            return false;
        }

        // 別の段（横に並んでいる）や、フォントサイズが違う行は統合しない。
        var overlapX = Math.Min(lastBounds.X + lastBounds.Width, nextBounds.X + nextBounds.Width)
            - Math.Max(lastBounds.X, nextBounds.X);
        var narrower = Math.Min(lastBounds.Width, nextBounds.Width);
        if (narrower <= 0 || overlapX / narrower < 0.5)
        {
            return false;
        }

        var heightRatio = Math.Max(lastBounds.Height, nextBounds.Height) / lineHeight;
        if (heightRatio > MaxHeightRatio)
        {
            return false;
        }

        // 縦の隙間が大きい（段落間スペース）か、字下げされている行は新しい段落。
        var gap = nextBounds.Y - (lastBounds.Y + lastBounds.Height);
        if (gap < -0.5 * lineHeight || gap > MaxLineGapRatio * lineHeight)
        {
            return false;
        }

        if (nextBounds.X - lastBounds.X > IndentRatio * nextBounds.Height)
        {
            return false;
        }

        return true;
    }

    private static BoundingBox ReadingBounds(PageBlock block, WritingMode writingMode) =>
        writingMode == WritingMode.Vertical
            ? HeuristicLayoutAnalyzer.ToReadingCoordinates(block.Bounds)
            : block.Bounds;

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

        // 行末ハイフンの直後へ空白を入れると "applica- tion" のように語が壊れる。
        // ハイフン自体が語の一部か組版上の分綴かは辞書なしでは確定できないため保持する。
        if (a[^1] == '-' && char.IsAsciiLetter(b[0]))
        {
            return a + b;
        }

        // 縦書きでは西暦などの横組み数字が複数行ボックスへ分割されることがある。
        // 数字同士の境界へ欧文用スペースを入れると「19 88年」のように壊れる。
        if (char.IsDigit(a[^1]) && char.IsDigit(b[0]))
        {
            return a + b;
        }

        // 欧文の行またぎは語間スペースを補い、和文はそのまま連結する。右引用符などの
        // 非ASCII句読点で行が終わる英文もあるため、ASCII判定だけには限定しない。
        var previous = a[^1];
        var next = b[0];
        if (char.IsWhiteSpace(previous)
            || char.IsWhiteSpace(next)
            || IsCjk(previous)
            || IsCjk(next)
            || previous is '(' or '[' or '{' or '“' or '‘'
            || next is ',' or '.' or ';' or ':' or '!' or '?' or ')' or ']' or '}' or '%')
        {
            return a + b;
        }

        return $"{a} {b}";
    }

    private static bool IsCjk(char character) =>
        character is >= '⺀' and <= '鿿'
        || character is >= '豈' and <= '﫿'
        || character is >= '＀' and <= '￯';

    private static bool CanMergeAcrossPage(PageBlock previous, PageBlock next)
    {
        if (previous.Type != BlockType.Body
            || next.Type != BlockType.Body
            || previous.TextSource != next.TextSource
            || previous.IsManuallyEdited
            || next.IsManuallyEdited
            || previous.Bounds.Y + previous.Bounds.Height < 0.65
            || next.Bounds.Y > 0.35)
        {
            return false;
        }

        var previousText = previous.OcrText.TrimEnd();
        var nextText = next.OcrText.TrimStart();
        if (previousText.Length == 0 || nextText.Length == 0)
        {
            return false;
        }

        if (previousText[^1] is '.' or '!' or '?' or ':' or ';' or '…')
        {
            return false;
        }

        var firstLetter = nextText.FirstOrDefault(char.IsLetter);
        return firstLetter != default && char.IsLower(firstLetter);
    }
}
