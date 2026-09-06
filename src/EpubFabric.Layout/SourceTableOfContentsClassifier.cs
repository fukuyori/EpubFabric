using EpubFabric.Core.Models;
using System.Text.RegularExpressions;

namespace EpubFabric.Layout;

/// <summary>
/// 原PDFに印刷された目次ブロックをリフロー本文から除外する。
/// EPUB側では文書モデルからナビゲーションを再生成するため、印刷目次の番号付き項目を
/// 章見出しとして残すと、偽の章と重複目次が生じる。
/// </summary>
public sealed partial class SourceTableOfContentsClassifier
{
    private const int MinimumProseLength = 50;

    public int Classify(IReadOnlyList<DocumentPage> pages)
    {
        var orderedPages = pages.OrderBy(page => page.PageNumber).ToList();
        var changedCount = 0;

        // 日本語雑誌では「目次」という表題が装飾画像になり、OCRテキストとして得られない
        // ことがある。前付けに限り、ページ番号が多数並ぶ索引状ページと、価格表示が反復する
        // 広告ページを本文から除外する。通常の本文ページへ波及しないよう先頭10ページ以内かつ
        // 複数の見出しを持つ断片的な紙面に限定する。
        foreach (var page in orderedPages.Take(10))
        {
            var blocks = OrderedIncludedBlocks(page);
            if (!LooksLikeUnmarkedMagazineTocOrAdvertisement(blocks))
            {
                continue;
            }

            foreach (var block in blocks)
            {
                changedCount += Exclude(block);
            }
        }

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
        string.Equals(block.OcrText.Trim(), "Table of Contents", StringComparison.OrdinalIgnoreCase)
        || string.Equals(block.OcrText.Trim(), "Contents", StringComparison.OrdinalIgnoreCase)
        || string.Equals(block.OcrText.Trim(), "目次", StringComparison.Ordinal);

    private static bool LooksLikeUnmarkedMagazineTocOrAdvertisement(IReadOnlyList<PageBlock> blocks)
    {
        if (blocks.Count < 12 || blocks.Count(IsHeading) < 3)
        {
            return false;
        }

        var exactPageReferences = blocks.Count(block => PageReferencePattern().IsMatch(block.OcrText.Trim()));
        var trailingPageReferences = blocks.Count(block => block.OcrText.Trim().Length <= 60
            && TrailingPageReferencePattern().IsMatch(block.OcrText.Trim()));
        var dottedEntries = blocks.Count(block => DottedTocEntryPattern().IsMatch(block.OcrText));
        var prices = blocks.Count(block => block.OcrText.Contains("定価", StringComparison.Ordinal));
        return exactPageReferences >= 4 || trailingPageReferences >= 6 || dottedEntries >= 3 || prices >= 3;
    }

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

    [GeneratedRegex(@"^[\s\[\(（]*[0-9０-９]{1,3}[\s\]\)）]*$")]
    private static partial Regex PageReferencePattern();

    [GeneratedRegex(@"[…\.．·・]{2,}\s*[0-9０-９]{1,3}\s*$")]
    private static partial Regex DottedTocEntryPattern();

    [GeneratedRegex(@"[^0-9０-９][0-9０-９]{1,3}\s*$")]
    private static partial Regex TrailingPageReferencePattern();
}
