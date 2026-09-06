using System.Text;
using System.Text.RegularExpressions;
using EpubFabric.Core.Models;

namespace EpubFabric.Layout;

/// <summary>
/// 全ページの上下余白に反復して現れる文字列を、柱・フッターとして分類する。
/// ページ単位の位置判定だけで本文を除外せず、文書全体の反復性を確認することで
/// ページ上端・下端にある短い本文の誤除外を防ぐ。
/// </summary>
public sealed partial class RepeatedMarginClassifier
{
    private const double MarginBand = 0.08;
    private const double MinimumRecurrence = 0.5;

    public int Classify(IReadOnlyList<DocumentPage> pages)
    {
        if (pages.Count == 0)
        {
            return 0;
        }

        var topCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var bottomCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var page in pages)
        {
            var topSeen = new HashSet<string>(StringComparer.Ordinal);
            var bottomSeen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var block in page.Blocks.Where(IsTextBlock))
            {
                var normalized = Normalize(block.OcrText);
                if (normalized.Length == 0)
                {
                    continue;
                }

                if (IsTopMargin(block) && topSeen.Add(normalized))
                {
                    Increment(topCounts, normalized);
                }

                if (IsBottomMargin(block) && bottomSeen.Add(normalized))
                {
                    Increment(bottomCounts, normalized);
                }
            }
        }

        var minimumCount = Math.Max(2, (int)Math.Ceiling(pages.Count * MinimumRecurrence));
        var changedCount = 0;

        foreach (var block in pages.SelectMany(page => page.Blocks).Where(IsTextBlock))
        {
            if (block.IsManuallyEdited)
            {
                continue;
            }

            var atTop = IsTopMargin(block);
            var atBottom = IsBottomMargin(block);
            if (!atTop && !atBottom)
            {
                continue;
            }

            var previousType = block.Type;
            var previousExcluded = block.IsExcluded;
            var text = block.OcrText.Trim();

            if (PageNumberPattern().IsMatch(text))
            {
                block.Type = BlockType.PageNumber;
                block.HeadingLevel = null;
                block.IsExcluded = true;
            }
            else
            {
                var normalized = Normalize(text);
                var recurringAtTop = atTop
                    && normalized.Length > 0
                    && topCounts.GetValueOrDefault(normalized) >= minimumCount;
                var recurringAtBottom = atBottom
                    && normalized.Length > 0
                    && bottomCounts.GetValueOrDefault(normalized) >= minimumCount;

                if (recurringAtTop || recurringAtBottom)
                {
                    block.Type = recurringAtTop ? BlockType.Header : BlockType.Footer;
                    block.HeadingLevel = null;
                    block.IsExcluded = true;
                }
                else if (block.Type is BlockType.Header or BlockType.Footer)
                {
                    block.Type = BlockType.Body;
                    block.HeadingLevel = null;
                    block.IsExcluded = false;
                }
            }

            if (block.Type != previousType || block.IsExcluded != previousExcluded)
            {
                changedCount++;
            }
        }

        return changedCount;
    }

    private static bool IsTextBlock(PageBlock block) =>
        block.Type is not (BlockType.Figure or BlockType.Code or BlockType.Decorative)
        && !string.IsNullOrWhiteSpace(block.OcrText);

    private static bool IsTopMargin(PageBlock block) => block.Bounds.Y < MarginBand;

    private static bool IsBottomMargin(PageBlock block) =>
        block.Bounds.Y + block.Bounds.Height > 1 - MarginBand;

    private static void Increment(Dictionary<string, int> counts, string key) =>
        counts[key] = counts.GetValueOrDefault(key) + 1;

    private static string Normalize(string text)
    {
        var normalized = new StringBuilder(text.Length);
        var previousWasSpace = true;

        foreach (var character in text)
        {
            if (char.IsDigit(character))
            {
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (!previousWasSpace)
                {
                    normalized.Append(' ');
                    previousWasSpace = true;
                }

                continue;
            }

            normalized.Append(char.ToLowerInvariant(character));
            previousWasSpace = false;
        }

        return normalized.ToString().Trim();
    }

    [GeneratedRegex(@"^[\s\-—\[\(]*(?:p\.?\s*|pp\.?\s*|page\s+)?\d+\s*(?:[/-]\s*\d+)?[\s\-—\]\)]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PageNumberPattern();
}
