using EpubFabric.Core.Models;

namespace EpubFabric.Layout;

/// <summary>
/// 原PDFに印刷された目次ブロックをリフロー本文から除外する。
/// EPUB側では文書モデルからナビゲーションを再生成するため、印刷目次の番号付き項目を
/// 章見出しとして残すと、偽の章と重複目次が生じる。
/// </summary>
public sealed class SourceTableOfContentsClassifier
{
    private const int MinimumProseLength = 50;

    public int Classify(IReadOnlyList<DocumentPage> pages)
    {
        var orderedPages = pages.OrderBy(page => page.PageNumber).ToList();
        var changedCount = 0;

        for (var pageIndex = 0; pageIndex < orderedPages.Count; pageIndex++)
        {
            var blocks = OrderedIncludedBlocks(orderedPages[pageIndex]);
            var markerIndex = blocks.FindIndex(IsTableOfContentsMarker);
            if (markerIndex < 0)
            {
                continue;
            }

            // 部扉の「部名 + Table of Contents」だけが置かれたページでは、部名を前章の
            // 小見出しとして残さない。明示的な章タイトルや本文が前にあるページは保持する。
            var beforeMarker = blocks.Take(markerIndex).ToList();
            if (beforeMarker.Count > 0
                && beforeMarker.All(block => IsHeading(block) && block.Type != BlockType.ChapterTitle))
            {
                foreach (var block in beforeMarker)
                {
                    changedCount += Exclude(block);
                }
            }

            var marker = blocks[markerIndex];
            changedCount += Exclude(marker);
            var finished = false;

            for (var i = markerIndex + 1; i < blocks.Count; i++)
            {
                if (LooksLikeProse(blocks[i]) && blocks[i].Bounds.X <= marker.Bounds.X + 0.02)
                {
                    finished = true;
                    break;
                }

                changedCount += Exclude(blocks[i]);
            }

            if (finished)
            {
                continue;
            }

            for (var followingPageIndex = pageIndex + 1; followingPageIndex < orderedPages.Count; followingPageIndex++)
            {
                var followingBlocks = OrderedIncludedBlocks(orderedPages[followingPageIndex]);
                if (followingBlocks.Count == 0)
                {
                    continue;
                }

                // 新しい節・章の見出しで始まり、そのページに文章があるなら目次は前ページで終了。
                if (IsLikelyContentStartHeading(followingBlocks[0]) && followingBlocks.Any(LooksLikeProse))
                {
                    break;
                }

                foreach (var block in followingBlocks)
                {
                    changedCount += Exclude(block);
                }

                pageIndex = followingPageIndex;
            }
        }

        return changedCount;
    }

    private static List<PageBlock> OrderedIncludedBlocks(DocumentPage page) => page.Blocks
        .Where(block => !block.IsExcluded)
        .OrderBy(block => block.ReadingOrder)
        .ToList();

    private static bool IsTableOfContentsMarker(PageBlock block) =>
        string.Equals(block.OcrText.Trim(), "Table of Contents", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeProse(PageBlock block)
    {
        if (block.Type is not (BlockType.Body or BlockType.Aside or BlockType.PullQuote))
        {
            return false;
        }

        var text = block.OcrText.Trim();
        if (text.Length < MinimumProseLength)
        {
            return false;
        }

        var end = text.Length - 1;
        while (end >= 0 && text[end] is ')' or ']' or '}' or '\'' or '"' or '’' or '”')
        {
            end--;
        }

        return end >= 0 && text[end] is '.' or '!' or '?';
    }

    private static bool IsHeading(PageBlock block) => block.Type is
        BlockType.ChapterTitle or BlockType.SectionHeading or BlockType.Subheading;

    private static bool IsLikelyContentStartHeading(PageBlock block)
    {
        var text = block.OcrText.TrimStart();
        return IsHeading(block) && text.Length > 0 && !char.IsAsciiDigit(text[0]);
    }

    private static int Exclude(PageBlock block)
    {
        if (block.IsExcluded)
        {
            return 0;
        }

        block.IsExcluded = true;
        return 1;
    }
}
